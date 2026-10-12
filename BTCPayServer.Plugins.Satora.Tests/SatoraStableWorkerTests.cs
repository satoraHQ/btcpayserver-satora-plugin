using System.Numerics;
using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Satora.Sdk;
using Xunit;
using Problems = BTCPayServer.Plugins.Satora.Stable.SatoraStableNotification.Problems;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;
using SdkRelayFundingState = uniffi.satora_sdk_ffi.RelayFundingState;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;

namespace BTCPayServer.Plugins.Satora.Tests;

// One worker tick per step, over rows kept in memory, and an SDK whose relay keeps a journal per swap
// as the real one does: nothing is sent again while a request may have gone out.
public class SatoraStableWorkerTests
{
    private const string Store = "store";
    private const string Id = "store-swap";
    // The swap's deposit: 10.13 USDC.
    private const long Deposit = 10_130_000;
    // What the swap's HTLC locked on Arbitrum.
    private const string Tbtc = "0x6c84a8f1c29108F47a79964b5Fe888D4f4D0dE40";

    private readonly List<string> _events = [];
    private readonly MemorySwaps _swaps = new();
    private readonly FakeWallets _wallets;
    private readonly RecordingCredit _credit;
    private readonly MemorySentAlerts _sentAlerts = new();
    private readonly TestWorker _worker;

    public SatoraStableWorkerTests()
    {
        _wallets = new FakeWallets(_events);
        _credit = new RecordingCredit(_events);
        _worker = new TestWorker(_swaps, _wallets, _credit, _sentAlerts);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SatoraStableSwap Row => _swaps.Rows[Id];

    [Fact]
    public async Task AnAddressHoldingLessThanTheDepositShowsWhatArrivedAndIsNotFunded()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = 4_000_000;

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}", $"progress {Id} 4000000"], _events);
        Assert.Null(Row.FundingAttemptedAt);
    }

    [Fact]
    public async Task TheDepositIsFundedThroughTheRelayOnceAndTheServerIsLeftToSeeIt()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}", $"progress {Id} {Deposit}", $"fund {Id}"], _events);
        Assert.True(Row is { FundTxHash: "0xfund-store-swap", PaymentRecorded: false, NeedsAttentionAt: null });
        Assert.Equal(_worker.Clock, Row.FundingAttemptedAt);
        // Long before its funding cutoff, the signatures get the SDK's full deadline.
        Assert.Equal([SatoraStableActions.MaxPermitDeadline], _wallets.PermitDeadlines);
    }

    [Fact]
    public async Task ADepositIsFundedEvenIfTheCheckoutCannotBeShownWhatArrived()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        _credit.ProgressError = new InvalidOperationException("the invoice could not be saved");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}", $"progress {Id} {Deposit}", $"fund {Id}"], _events);
        Assert.Equal("0xfund-store-swap", Row.FundTxHash);
    }

    // A funding started close to the cutoff signs a deadline that still lets it land in time.
    [Fact]
    public async Task AFundingStartedNearTheCutoffSignsAShorterDeadline()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        _worker.Clock = Row.FundingStartCutoff - TimeSpan.FromMinutes(1);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([SatoraStableActions.MinPermitDeadline + TimeSpan.FromMinutes(1)], _wallets.PermitDeadlines);
    }

    // The cutoff and the permit deadline are judged when the funding starts, not before a slow deposit read.
    [Theory]
    [InlineData(-2, true)]
    [InlineData(1, false)]
    public async Task TheFundingCutoffAndPermitDeadlineAreJudgedAfterTheDepositRead(int minutesPastCutoffAfterRead, bool funded)
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        _worker.Clock = Row.FundingStartCutoff - TimeSpan.FromMinutes(10);
        _wallets.DuringRead = () => _worker.Clock = Row.FundingStartCutoff + TimeSpan.FromMinutes(minutesPastCutoffAfterRead);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(funded ? [SatoraStableActions.MinPermitDeadline + TimeSpan.FromMinutes(2)] : [], _wallets.PermitDeadlines);
        Assert.Equal(funded, Row.FundingAttemptedAt is not null);
    }

    // The relay refuses before anything is signed, such as when the DEX rate moved.
    [Fact]
    public async Task ARefusalBeforeSigningIsTriedAgainOnTheNextTickAndAlertsOnTheSecond()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        _wallets.FundRefusal = new SdkException(SdkErrorKind.Api, "the rate moved, re-quote and retry", 500);

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        await _worker.TickAsync(Store, Ct);
        Assert.Equal([Problems.FundingFailing], _worker.Sent.Select(n => n.Problem));
        Assert.Equal(Row.FundingStartCutoff, _worker.Sent[0].Deadline);

        _wallets.FundRefusal = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(3, _events.Count(e => e == $"fund {Id}"));
        Assert.DoesNotContain(_events, e => e.StartsWith("reconcile", StringComparison.Ordinal));
        Assert.Equal(("0xfund-store-swap", 1), (Row.FundTxHash, _wallets.Sent));
        Assert.Single(_worker.Sent);
    }

    // A request that went out and timed out: the SDK refuses to sign again until a reconcile says what
    // became of it, and the worker reconciles every tick, reading nothing else, until it does.
    [Fact]
    public async Task ARequestThatMayHaveGoneOutIsReconciledAndNeverSentAgain()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        _wallets.SendThenTimeout = true;
        _wallets.ReconcileAnswer = new SdkRelayFundingState.Pending(false, 1_800_000_000);
        // Past the fast reads: an unresolved request is reconciled every tick anyway.
        _worker.Clock = Row.InvoiceExpiresAt + SatoraStableActions.FastPollWindow;

        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraStableActions.LatePoll;
        await _worker.TickAsync(Store, Ct);
        _events.Clear();
        _worker.Clock += SatoraStableWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"reconcile {Id}"], _events);
        Assert.Null(Row.FundTxHash);

        _wallets.ReconcileAnswer = new SdkRelayFundingState.Funded("0xrelay");
        _worker.Clock += SatoraStableWorker.Interval;
        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraStableWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"reconcile {Id}", $"reconcile {Id}"], _events);
        Assert.Equal(("0xrelay", 1), (Row.FundTxHash, _wallets.Sent));
        Assert.Empty(_worker.Sent);
    }

    // After a restart cut a relay request off, the log shows the SDK refusing another funding and each
    // reconcile that finds the request pending.
    [Fact]
    public async Task ARefusedFundingAndEachReconcileThatFindsItPendingAreLogged()
    {
        var logs = new RecordingLogger<SatoraStableWorker>();
        var worker = new TestWorker(_swaps, _wallets, _credit, _sentAlerts, logs);
        Open(new SdkSwapStatus.Pending(), fundingAttempted: true);
        worker.Clock = _worker.Clock;
        _wallets.Journal[Id] = "Sending";
        _wallets.Deposited[Id] = Deposit;

        await worker.TickAsync(Store, Ct);
        worker.Clock += SatoraStableWorker.Interval;
        await worker.TickAsync(Store, Ct);

        const string pending = $"The relay funding of Satora stablecoin swap {Id} of store {Store} is pending, and its signature can land until 2027-01-15 08:00:00Z; reconciling it again";
        Assert.Equal([
                (LogLevel.Information, $"The SDK refused to fund Satora stablecoin swap {Id} of store {Store} again, as an earlier relay request may have gone out; reconciling it (relay funding may already be submitted; reconcile it before another attempt)"),
                (LogLevel.Information, pending),
                (LogLevel.Information, pending)
            ],
            logs.Entries);
        Assert.All(logs.Errors, e => Assert.Null(e));
        Assert.Equal(0, _wallets.Sent);
    }

    // A restart cut the relay request off, and the funding landed meanwhile. The server has moved past
    // Pending, where Fund reconciles, so the funding is looked up after the claim until the SDK proves it.
    [Fact]
    public async Task AFundingARestartCutOffIsRecordedOnceTheSdkProvesItsTransaction()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true);
        _wallets.Journal[Id] = "Sending";
        _wallets.ReconcileAnswer = new SdkRelayFundingState.Pending(true, 1_800_000_000);

        await _worker.TickAsync(Store, Ct);
        _wallets.ReconcileAnswer = new SdkRelayFundingState.Funded("0xrelay");
        _worker.Clock += SatoraStableWorker.Interval;
        await _worker.TickAsync(Store, Ct);
        Assert.Null(Row.FundTxHash);
        _worker.Clock += SatoraStableWorker.FundingLookupInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"record {Id} Processing", $"claim {Id} own-address", $"record {Id} Settled 99400", $"reconcile {Id}", $"reconcile {Id}"],
            _events.Where(e => !e.StartsWith("read", StringComparison.Ordinal)));
        Assert.True(Row is { FundTxHash: "0xrelay", Credited: true });
        Assert.Equal(0, _wallets.Sent);
    }

    [Fact]
    public async Task ARequestWhoseSignaturesExpiredUnspentLetsTheDepositBeFundedAgain()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        _wallets.SendThenTimeout = true;
        await _worker.TickAsync(Store, Ct);
        _wallets.SendThenTimeout = false;
        _wallets.ReconcileAnswer = new SdkRelayFundingState.Expired();

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        // The second call is refused by the journal and reconciled; the third signs again.
        Assert.Equal([$"fund {Id}", $"fund {Id}", $"reconcile {Id}", $"fund {Id}"],
            _events.Where(e => !e.StartsWith("read", StringComparison.Ordinal) && !e.StartsWith("progress", StringComparison.Ordinal)));
        Assert.Equal(("0xfund-store-swap", 2), (Row.FundTxHash, _wallets.Sent));
    }

    // The SDK cannot be interrupted once it signs, so nothing starts once the worker is stopping.
    [Fact]
    public async Task StoppingWhileTheDepositIsReadStartsNoFunding()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        using var stopping = new CancellationTokenSource();
        _wallets.DuringRead = stopping.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.TickAsync(Store, stopping.Token));

        Assert.DoesNotContain(_events, e => e.StartsWith("fund", StringComparison.Ordinal));
        Assert.Null(Row.FundingAttemptedAt);
    }

    [Fact]
    public async Task AnOverpaymentIsNotedAndAlertedOnceAndTheDepositIsStillFunded()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit + 500_000;
        _wallets.FundRefusal = new SdkException(SdkErrorKind.Api, "the rate moved, re-quote and retry", 500);
        await _worker.TickAsync(Store, Ct);
        _wallets.FundRefusal = null;

        await _worker.TickAsync(Store, Ct);

        var alert = Assert.Single(_worker.Sent, n => n.Problem == Problems.Overpaid);
        Assert.Equal(("0.500000 USDC", "invoice-swap"), (alert.Amount, alert.InvoiceId));
        Assert.Contains("sent 0.500000 USDC more", SatoraStableNotification.Body(alert, _worker.Clock));
        Assert.Contains("0.500000 USDC more than Satora stablecoin swap store-swap asked for", Assert.Single(_credit.Notes).Message);
        Assert.Equal("0xfund-store-swap", Row.FundTxHash);
    }

    // Until the relay funds the swap every tick reads the overpaid deposit again.
    [Fact]
    public async Task AnOverpaymentIsNotNotedOrAlertedAgainAfterARestart()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit + 500_000;
        _wallets.FundRefusal = new SdkException(SdkErrorKind.Api, "the rate moved, re-quote and retry", 500);
        await _worker.TickAsync(Store, Ct);

        var restarted = new TestWorker(_swaps, _wallets, _credit, _sentAlerts) { Clock = _worker.Clock };
        await restarted.TickAsync(Store, Ct);

        Assert.Equal([Problems.Overpaid], _worker.Sent.Select(n => n.Problem));
        Assert.DoesNotContain(restarted.Sent, n => n.Problem == Problems.Overpaid);
        Assert.Single(_credit.Notes);
    }

    [Fact]
    public async Task AfterTheInvoiceExpiresTheAddressIsReadEveryFiveMinutesAndALateDepositIsFunded()
    {
        Open(new SdkSwapStatus.Pending());
        _worker.Clock = Row.InvoiceExpiresAt + SatoraStableActions.FastPollWindow;

        await _worker.TickAsync(Store, Ct);
        _wallets.Deposited[Id] = Deposit;
        _worker.Clock += SatoraStableWorker.Interval;
        await _worker.TickAsync(Store, Ct);
        Assert.Equal([$"read {Id}", $"progress {Id} 0"], _events);

        _worker.Clock += SatoraStableActions.LatePoll;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal($"fund {Id}", _events[^1]);
        Assert.Equal("0xfund-store-swap", Row.FundTxHash);
    }

    // The customer looked at the method and paid another way.
    [Theory]
    [InlineData(true, 0, 60, true)]
    [InlineData(true, 0, 59, false)]
    [InlineData(true, 1, 60, false)]
    [InlineData(false, 0, 60, false)]
    public async Task ASwapWhoseInvoiceWasPaidAnotherWayIsClosedOnceNothingCameAnHourAfterExpiry(bool settledOtherwise, long deposited,
        int minutesAfterExpiry, bool closed)
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = deposited;
        _credit.SettledOtherwise = settledOtherwise;
        _worker.Clock = Row.InvoiceExpiresAt + TimeSpan.FromMinutes(minutesAfterExpiry);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(closed ? SatoraStableActions.AbandonedReason : null, Row.ClosedReason);
        Assert.Equal(closed, Row.CompletedAt is not null);
        Assert.Empty(_worker.Sent);
    }

    [Fact]
    public async Task PastTheFundingCutoffWhatTheAddressHoldsGoesToTheOwnerAndIsReadNoMore()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit;
        _worker.Clock = Row.FundingStartCutoff;

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}", $"deactivate {Id}"], _events);
        Assert.True(Row is { AttentionReason: SatoraStableAttention.Sweep, FundingAttemptedAt: null, CompletedAt: null });
        Assert.Single(_wallets.StatusReads);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.NeedsSweep, SatoraStableActions.PastCutoffReason), (alert.Problem, alert.Status));
        Assert.Contains("(paid after the swap's funding cutoff)", SatoraStableNotification.Body(alert, _worker.Clock));
        Assert.Contains($"Tokens at the deposit address {Row.DepositAddress}", Assert.Single(_credit.Notes).Message);
    }

    // A customer who sent part of the deposit in time did not pay late.
    [Fact]
    public async Task PastTheFundingCutoffADepositStillShortIsNotSaidToBeLate()
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = Deposit / 2;
        _worker.Clock = Row.FundingStartCutoff;

        await _worker.TickAsync(Store, Ct);

        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.NeedsSweep, SatoraStableActions.ShortReason), (alert.Problem, alert.Status));
        Assert.Contains("(less than the deposit by the swap's funding cutoff)", Assert.Single(_credit.Notes).Message);
    }

    [Theory]
    [InlineData(0)]
    // Less than a hundredth of a USDC.
    [InlineData(9_999)]
    public async Task PastTheFundingCutoffAnAddressHoldingNothingWorthSendingBackClosesTheSwap(long deposited)
    {
        Open(new SdkSwapStatus.Pending());
        _wallets.Deposited[Id] = deposited;
        _worker.Clock = Row.FundingStartCutoff;

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}", $"deactivate {Id}"], _events);
        Assert.True(Row is { ClosedReason: SatoraStableActions.UnfundedReason, CompletedAt: not null, NeedsAttentionAt: null });
        Assert.Empty(_worker.Sent);
    }

    // A signature signed before the swap expired can still land until its deadline.
    [Fact]
    public async Task AnExpiredSwapWithASignatureThatMayStillLandWaitsForIt()
    {
        Open(new SdkSwapStatus.Expired(), fundingAttempted: true);
        _wallets.Journal[Id] = "Sending";
        _wallets.ReconcileAnswer = new SdkRelayFundingState.Pending(false, 1_800_000_000);
        _wallets.Deposited[Id] = Deposit;

        await _worker.TickAsync(Store, Ct);
        Assert.Equal([$"reconcile {Id}", $"read {Id}"], _events);
        Assert.Null(Row.NeedsAttentionAt);

        _wallets.ReconcileAnswer = new SdkRelayFundingState.Expired();
        _worker.Clock += SatoraStableWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(SatoraStableAttention.Sweep, Row.AttentionReason);
        // Signed for, so the deposit was whole before the cutoff, not sent after the swap expired.
        Assert.Equal((Problems.NeedsSweep, SatoraStableActions.NotFundedReason), (_worker.Sent.Single().Problem, _worker.Sent.Single().Status));
    }

    // The relay's answer was lost, and its funding landed after the server stopped following the swap.
    [Fact]
    public async Task AFundingThatLandedAfterTheServerExpiredTheSwapIsLeftForItsHtlcRefund()
    {
        Open(new SdkSwapStatus.Expired(), fundingAttempted: true);
        _wallets.Journal[Id] = "Sending";
        _wallets.ReconcileAnswer = new SdkRelayFundingState.Funded("0xrelay");

        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { FundTxHash: "0xrelay", AttentionReason: SatoraStableAttention.HtlcRefund, CompletedAt: null });
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.NeedsHtlcRefund, SatoraStableActions.FundedLateReason, Row.EvmRefundLocktime), (alert.Problem, alert.Status, alert.Deadline));
        Assert.Contains("contact Satora support at support@satora.io", SatoraStableNotification.Body(alert, _worker.Clock));
    }

    // Nothing at the address and no journal of the funding the worker tried: the deposit may be in the
    // HTLC, so the swap is not closed as empty.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEmptyAddressWithAFundingTheSdkCannotAccountForIsNeverClosedAsEmpty(bool expired)
    {
        Open(expired ? new SdkSwapStatus.Expired() : new SdkSwapStatus.Pending(), fundingAttempted: true);
        _worker.Clock = Row.FundingStartCutoff;

        await _worker.TickAsync(Store, Ct);

        Assert.Null(Row.CompletedAt);
        Assert.Equal(expired ? SatoraStableAttention.HtlcRefund : null, Row.AttentionReason);
        Assert.Equal(expired ? [(Problems.NeedsHtlcRefund, SatoraStableActions.FundingUnknownReason)] : [],
            _worker.Sent.Select(n => (n.Problem, n.Status)));
    }

    [Fact]
    public async Task RecordsAProcessingPaymentOnceTheServerSeesTheFunding()
    {
        Open(new SdkSwapStatus.ClientFundingSeen(), fundingAttempted: true);

        await _worker.TickAsync(Store, Ct);
        _wallets.Statuses[Id] = new SdkSwapStatus.ClientFunded();
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"record {Id} Processing"], _events);
        Assert.True(Row.PaymentRecorded);
    }

    // The swap server passes a DEX surplus on into the VHTLC: the exactly paid invoice is credited what
    // was due, and the surplus stays in the wallet, noted on the invoice.
    [Theory]
    [InlineData(99_501, "1 sat")]
    [InlineData(99_502, "2 sats")]
    public async Task ASurplusTheExchangeDeliveredIsCreditedAtTheAmountDueAndNoted(long vhtlcSats, string surplus)
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        _wallets.Settlement = swapId => new SdkArkadeSettlement.Offchain("ark-" + swapId, (ulong)vhtlcSats);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"record {Id} Settled 99500"], _events);
        Assert.True(Row is { Credited: true } && Row.VhtlcSats == vhtlcSats);
        Assert.Equal((InvoiceEventData.EventSeverity.Info, $"The exchange delivered {surplus} more than due for Satora stablecoin swap {Id}. The surplus stays in the Satora wallet."),
            Assert.Single(_credit.Notes));
    }

    // The SDK claims a VHTLC funded short by the DEX only within this limit, the invoice's payment
    // tolerance, which each claim reads afresh.
    [Theory]
    [InlineData(1.5, 150u)]
    [InlineData(null, 0u)]
    public async Task EachClaimIsLimitedToTheShortfallItsInvoicesPaymentToleranceCovers(double? tolerance, uint limit)
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        _credit.Tolerance = (decimal?)tolerance;
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");

        await _worker.TickAsync(Store, Ct);
        _credit.Tolerance = 2;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([limit, 200u], _wallets.ShortfallLimits);
    }

    // The swap server funded the VHTLC 900 sats short of the 99 500 due, within the invoice's 1%: the
    // invoice is settled with what arrived, and the shortfall noted.
    [Fact]
    public async Task AClaimTheExchangeDeliveredShortIsCreditedWithWhatArrivedAndNoted()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        _wallets.Settlement = swapId => new SdkArkadeSettlement.Offchain("ark-" + swapId, 98_600);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"record {Id} Settled 98600"], _events);
        Assert.Equal([100u], _wallets.ShortfallLimits);
        Assert.True(Row is { Credited: true, VhtlcSats: 98_600 });
        Assert.Equal((InvoiceEventData.EventSeverity.Info,
                $"The exchange delivered 900 sats less than due for Satora stablecoin swap {Id}. The payment is recorded at the amount that arrived."),
            Assert.Single(_credit.Notes));
    }

    // A tick that settled the payment but failed before marking the swap credited noted it already.
    [Theory]
    [InlineData(99_501)]
    [InlineData(98_600)]
    public async Task ASurplusOrShortfallIsNotedOnlyByTheCreditThatSettledThePayment(long vhtlcSats)
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true, claimTxId: "ark");
        Row.VhtlcSats = vhtlcSats;
        _credit.Outcome = SatoraCreditOutcome.AlreadyRecorded;

        await _worker.TickAsync(Store, Ct);

        Assert.True(Row.Credited);
        Assert.Empty(_credit.Notes);
    }

    [Fact]
    public async Task ClaimsAFundedSwapAndSettlesItWithWhatTheVhtlcHeldThenClosesOnceTheServerTookTheHtlc()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"record {Id} Settled 99400"], _events);
        Assert.True(Row is { ClaimTxId: "ark-store-swap", VhtlcSats: 99_400, Credited: true, CompletedAt: null });

        _events.Clear();
        _wallets.Statuses[Id] = new SdkSwapStatus.ServerRedeemed();
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}"], _events);
        Assert.NotNull(Row.CompletedAt);
        Assert.Empty(_worker.Sent);
    }

    // The funding was never recorded: the worker was down, or the server was quicker.
    [Fact]
    public async Task AFundingMissedBeforeTheClaimIsRecordedFirst()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"record {Id} Processing", $"claim {Id} own-address", $"record {Id} Settled 99400"], _events);
    }

    // Recoverable outputs settle in a batch, whose receipt carries no amount.
    [Fact]
    public async Task AClaimSettledInABatchIsCreditedWithEverythingTheVhtlcHeld()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        _wallets.Settlement = _ => new SdkArkadeSettlement.Delegated("commitment");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"amounts {Id}", $"record {Id} Settled 99300"], _events);
        Assert.True(Row is { ClaimTxId: "commitment", VhtlcSats: 99_300 });
    }

    // The amount is written once, so a batch the indexer does not show yet is claimed again, which
    // resumes the claim, and read again.
    [Fact]
    public async Task AClaimSettledInABatchIsNeverCreditedWithNothing()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        _wallets.Settlement = _ => new SdkArkadeSettlement.Delegated("commitment");
        _wallets.Amounts = new VhtlcAmounts(0, 0, 0, []);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"amounts {Id}"], _events);
        Assert.True(Row is { ClaimTxId: null, VhtlcSats: null, Credited: false });

        _events.Clear();
        _wallets.Amounts = new VhtlcAmounts(0, 0, 99_300, []);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"amounts {Id}", $"record {Id} Settled 99300"], _events);
        Assert.True(Row is { ClaimTxId: "commitment", VhtlcSats: 99_300, Credited: true });
    }

    [Fact]
    public async Task AClaimFailingTwiceAlertsOnce()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");

        for (var i = 0; i < 3; i++)
            await _worker.TickAsync(Store, Ct);

        Assert.Equal([Problems.ClaimFailing], _worker.Sent.Select(n => n.Problem));
        Assert.Equal(3, _events.Count(e => e.StartsWith("claim", StringComparison.Ordinal)));
        Assert.True(Row is { ClaimTxId: null, NeedsAttentionAt: null });
    }

    // A claim the SDK refuses by its rules, as one short by more than its limit, is refused again on
    // every tick until the claim deadline: one line each, without the stack, which a failure keeps, as
    // does an InvalidSwap from a read before or after the claim.
    [Fact]
    public async Task AClaimTheSdkRefusesIsLoggedOnOneLinePerAttempt()
    {
        var logs = new RecordingLogger<SatoraStableWorker>();
        var worker = new TestWorker(_swaps, _wallets, _credit, _sentAlerts, logs);
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        worker.Clock = _worker.Clock;
        const string refusal = "Arkade HTLC is underfunded: the VHTLC holds 98600 sats, 900 below the target of 99500, " +
                               "which is more than this client's limit of 50 basis points; raise it with Client::set_conversion_shortfall_limit";
        _wallets.ClaimError = new SdkException(SdkErrorKind.InvalidSwap, refusal);

        await worker.TickAsync(Store, Ct);
        await worker.TickAsync(Store, Ct);
        var failure = _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        await worker.TickAsync(Store, Ct);
        _wallets.ClaimError = null;
        var addressFailure = _wallets.ClaimAddressError = new SdkException(SdkErrorKind.InvalidSwap, "Arkade server identity or network mismatch");
        await worker.TickAsync(Store, Ct);
        _wallets.ClaimAddressError = null;
        _wallets.Settlement = _ => new SdkArkadeSettlement.Delegated("commitment");
        var amountsFailure = _wallets.AmountsError = new SdkException(SdkErrorKind.InvalidSwap, "swap record missing");
        await worker.TickAsync(Store, Ct);

        Assert.Equal([
                (LogLevel.Warning, $"Claiming Satora stablecoin swap {Id} of store {Store} was refused by the SDK (1 so far): {refusal}", null),
                (LogLevel.Warning, $"Claiming Satora stablecoin swap {Id} of store {Store} was refused by the SDK (2 so far): {refusal}", null),
                (LogLevel.Warning, $"Claiming Satora stablecoin swap {Id} of store {Store} failed (3 so far)", (Exception?)failure),
                (LogLevel.Warning, $"Claiming Satora stablecoin swap {Id} of store {Store} failed (4 so far)", addressFailure),
                (LogLevel.Warning, $"Claiming Satora stablecoin swap {Id} of store {Store} failed (5 so far)", amountsFailure)
            ],
            logs.Entries.Zip(logs.Errors, (entry, error) => (entry.Level, entry.Message, error))
                .Where(entry => entry.Message.StartsWith("Claiming", StringComparison.Ordinal)));
        Assert.Equal([Problems.ClaimFailing], worker.Sent.Select(n => n.Problem));
    }

    // A claim that cannot complete any more is final; one that can is finished, never refunded.
    [Fact]
    public async Task PastTheVhtlcLocktimeTheDepositIsLeftForItsRefundOnlyOnceNoClaimCanComplete()
    {
        Open(new SdkSwapStatus.ServerFunded(), fundingAttempted: true, paymentRecorded: true);
        _worker.Clock = Row.VhtlcRefundLocktime;
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        _wallets.ClaimPossible = true;

        await _worker.TickAsync(Store, Ct);
        Assert.Equal([$"claim {Id} own-address"], _events);
        Assert.Null(Row.NeedsAttentionAt);

        _wallets.ClaimPossible = false;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"unaccount {Id}", $"deactivate {Id}"], _events);
        Assert.Equal(SatoraStableAttention.HtlcRefund, Row.AttentionReason);
        Assert.Equal((Problems.NeedsHtlcRefund, SatoraStableActions.UnclaimedReason), (_worker.Sent[^1].Problem, _worker.Sent[^1].Status));
    }

    // The server reports taking the VHTLC back, which only the SDK's word that no claim can complete confirms.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AVhtlcTheServerReportsTakenBackIsLeftForTheHtlcRefundOnlyOnceNoClaimCanComplete(bool claimPossible)
    {
        Open(new SdkSwapStatus.ClientFundedServerRefunded(), fundingAttempted: true, paymentRecorded: true);
        _worker.Clock = Row.VhtlcRefundLocktime;
        _wallets.ClaimPossible = claimPossible;

        await _worker.TickAsync(Store, Ct);

        if (claimPossible)
        {
            Assert.Equal([$"claim {Id} own-address", $"record {Id} Settled 99400"], _events);
            Assert.True(Row is { ClaimTxId: "ark-store-swap", Credited: true, NeedsAttentionAt: null });
            Assert.Empty(_worker.Sent);
            return;
        }
        Assert.Equal([$"unaccount {Id}", $"deactivate {Id}"], _events);
        Assert.Equal(SatoraStableAttention.HtlcRefund, Row.AttentionReason);
        Assert.Equal((Problems.NeedsHtlcRefund, SatoraStableActions.UnclaimedReason), (_worker.Sent.Single().Problem, _worker.Sent.Single().Status));
    }

    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerWontFund))]
    [InlineData(typeof(SdkSwapStatus.ClientFundedTooLate))]
    [InlineData(typeof(SdkSwapStatus.ClientInvalidFunded))]
    public async Task AFundingTheServerWillNotCompleteComesOffTheInvoiceAndIsLeftInTheHtlc(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, fundingAttempted: true, paymentRecorded: true);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"unaccount {Id}", $"deactivate {Id}"], _events);
        Assert.True(Row is { AttentionReason: SatoraStableAttention.HtlcRefund, CompletedAt: null });
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.NeedsHtlcRefund, status.Name, Row.EvmRefundLocktime), (alert.Problem, alert.Status, alert.Deadline));
        Assert.Contains($"refunded to the deposit address from {Row.EvmRefundLocktime:u}", Assert.Single(_credit.Notes).Message);
    }

    // The HTLC's refund pays the deposit address, in the token it locked.
    [Fact]
    public async Task AnHtlcRefundedToTheDepositAddressGoesToTheOwner()
    {
        Open(new SdkSwapStatus.ClientRefunded(), fundingAttempted: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"deactivate {Id}"], _events);
        Assert.True(Row is { AttentionReason: SatoraStableAttention.Sweep, SweepToken: Tbtc });
        Assert.Equal((Problems.NeedsSweep, SatoraStableActions.HtlcRefundedReason), (_worker.Sent.Single().Problem, _worker.Sent.Single().Status));

        Row.SweepDestination = SatoraStableRefundsTests.Customer;
        _wallets.Held[(Id, Tbtc)] = 120_000;
        _wallets.Gas[Id] = 1;
        await _worker.TickAsync(Store, Ct);

        Assert.Contains($"sweep {Id} {SatoraStableRefundsTests.Customer} {Tbtc}", _events);
        Assert.True(Row is { SweepTxHash: "0xsweep-1", CompletedAt: not null });
    }

    // The funding spent the swap's nonce, so anything still at the address was sent on top of the deposit.
    [Fact]
    public async Task WhatIsLeftAtTheDepositAddressOfACreditedSwapGoesToTheOwner()
    {
        Open(new SdkSwapStatus.ServerRedeemed(), fundingAttempted: true, paymentRecorded: true, claimTxId: "ark", credited: true);
        _wallets.Deposited[Id] = 500_000;

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}", $"deactivate {Id}"], _events);
        Assert.True(Row is { AttentionReason: SatoraStableAttention.Sweep, Credited: true, CompletedAt: null, SweepToken: null });
        Assert.Equal((Problems.NeedsSweep, SatoraStableActions.LeftoverReason), (_worker.Sent.Single().Problem, _worker.Sent.Single().Status));
    }

    // The HTLC went back to the deposit address after the wallet's claim, so the token it locked is there.
    [Fact]
    public async Task TheRefundedTokenOfAClaimedSwapWhoseHtlcWentBackGoesToTheOwner()
    {
        Open(new SdkSwapStatus.ClientRedeemedAndClientRefunded(), fundingAttempted: true, paymentRecorded: true, claimTxId: "ark", credited: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"deactivate {Id}"], _events);
        Assert.True(Row is { AttentionReason: SatoraStableAttention.Sweep, Credited: true, CompletedAt: null, SweepToken: Tbtc });
        Assert.Equal((Problems.NeedsSweep, SatoraStableActions.HtlcRefundedReason), (_worker.Sent.Single().Problem, _worker.Sent.Single().Status));
    }

    // A claim the server saw but whose receipt was lost to a timeout or a restart.
    [Fact]
    public async Task AClaimOfASwapWhoseHtlcWentBackIsFinishedAndCreditedBeforeTheOwnerIsTold()
    {
        Open(new SdkSwapStatus.ClientRedeemedAndClientRefunded(), fundingAttempted: true, paymentRecorded: true);
        _worker.Clock = Row.ClaimCutoff;
        _wallets.ClaimPossible = true;

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"amounts {Id}", $"claim {Id} own-address", $"record {Id} Settled 99400", $"deactivate {Id}"], _events);
        Assert.True(Row is { ClaimTxId: "ark-store-swap", Credited: true, AttentionReason: SatoraStableAttention.Sweep, CompletedAt: null });
    }

    // The VHTLC still holds what the server says was claimed: claiming it would take both legs of the swap.
    [Fact]
    public async Task ASwapWhoseHtlcWentBackIsNotClaimedAnewAndGoesToTheOwnerOnceNoClaimCanComplete()
    {
        Open(new SdkSwapStatus.ClientRedeemedAndClientRefunded(), fundingAttempted: true, paymentRecorded: true);
        _wallets.ClaimPossible = true;
        _wallets.Amounts = new VhtlcAmounts(99_400, 0, 0, []);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"amounts {Id}"], _events);
        Assert.Null(Row.NeedsAttentionAt);

        _worker.Clock = Row.ClaimCutoff;
        _wallets.ClaimPossible = false;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"amounts {Id}", $"unaccount {Id}", $"deactivate {Id}"], _events);
        Assert.True(Row is { AttentionReason: SatoraStableAttention.Sweep, ClaimTxId: null, CompletedAt: null, SweepToken: Tbtc });
        Assert.Equal((Problems.NeedsSweep, SatoraStableActions.HtlcRefundedReason), (_worker.Sent.Single().Problem, _worker.Sent.Single().Status));
    }

    [Fact]
    public async Task AClaimTheServerReportsButNoneCanCompleteClosesOnceTheServerTookTheHtlc()
    {
        Open(new SdkSwapStatus.ServerRedeemed(), fundingAttempted: true, paymentRecorded: true);
        _worker.Clock = Row.ClaimCutoff;
        _wallets.ClaimError = new SdkException(SdkErrorKind.InvalidSwap, "claim window closed");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"claim {Id} own-address", $"read {Id}", $"unaccount {Id}"], _events);
        Assert.True(Row is { Credited: false, CompletedAt: not null });
        Assert.Equal([Problems.ClaimUnverified], _worker.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task AStatusTheTableDoesNotHandleAlertsOnceAndMovesNothing()
    {
        Open(new SdkSwapStatus.Other("Bridging"));

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.Equal([Problems.UnexpectedStatus], _worker.Sent.Select(n => n.Problem));
        Assert.True(Row is { NeedsAttentionAt: null, CompletedAt: null });
    }

    [Fact]
    public async Task ASwapTheServerDoesNotKnowIsReleasedOnceItsVhtlcLocktimePassed()
    {
        Open(null);
        _wallets.NotFound.Add(Id);

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events);
        _worker.Clock = Row.VhtlcRefundLocktime;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"read {Id}", $"deactivate {Id}"], _events);
        Assert.Equal(SatoraStableActions.UnfundedReason, Row.ClosedReason);
        Assert.Equal([(Problems.UnknownToServer, "invoice-swap")], _worker.Sent.Select(n => (n.Problem, n.InvoiceId)));
    }

    [Fact]
    public async Task StatusesUnreadableForHalfAnHourAlertOnceWithTheEarliestClaimCutoff()
    {
        Open(new SdkSwapStatus.ServerFunded());
        _wallets.StatusError = new SdkException(SdkErrorKind.Transport, "swap server unreachable");

        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraStableWorker.StatusFailureAlertAfter;
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.StatusUnavailable, Row.ClaimCutoff), (alert.Problem, alert.Deadline));
        Assert.Empty(_events);
    }

    // The registry has logged the locked wallet once already, as an error.
    [Fact]
    public async Task LogsStatusesALockedWalletCannotReadAtDebugWithoutAStackTrace()
    {
        var logs = new RecordingLogger<SatoraStableWorker>();
        var worker = new TestWorker(_swaps, _wallets, _credit, _sentAlerts, logs);
        Open(new SdkSwapStatus.ServerFunded());
        _wallets.StatusError = new SatoraWalletUnavailableException("The store's Satora wallet is not usable (Locked).");

        await worker.TickAsync(Store, Ct);

        Assert.Equal((LogLevel.Debug, "Could not read the Satora stablecoin swaps of store store; retrying (The store's Satora wallet is not usable (Locked).)"),
            Assert.Single(logs.Entries));
        Assert.Null(Assert.Single(logs.Errors));
    }

    [Fact]
    public async Task ASwapWaitingForTheOwnerIsNotRead()
    {
        Open(new SdkSwapStatus.Expired());
        Row.NeedsAttentionAt = _worker.Clock;
        Row.AttentionReason = SatoraStableAttention.Sweep;

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_wallets.StatusReads);
        Assert.Empty(_events);
    }

    // A test fork keeps mainnet's chain id, so without its node the SDK would read mainnet. The registry
    // is null: an SDK call would throw NullReferenceException instead.
    [Fact]
    public async Task OffMainnetNothingIsReadOrSentForASwapWithoutItsNode()
    {
        var wallets = new SatoraStableWallets(null!, null!, new ConfigurationBuilder().Build(), SatoraStablePaymentTests.Btc);
        var swap = SatoraStableIntegrationTests.Swap(Store, "swap");

        foreach (var call in new Func<Task>[]
                 {
                     () => wallets.GetDepositedAsync(swap, Ct),
                     () => wallets.FundAsync(swap, SatoraStableActions.MaxPermitDeadline, Ct),
                     () => wallets.ReconcileAsync(swap, Ct)
                 })
            Assert.Contains(SatoraNetworks.EvmRpcArbitrumKey, (await Assert.ThrowsAsync<InvalidOperationException>(call)).Message);
    }

    [Fact]
    public void TheAlertLinksThePluginsPageAndSaysTheClaimDeadlinePassedWhenSentFromIt()
    {
        INotificationHandler handler = new SatoraStableNotification.Handler(new RouteLinks(), new BTCPayServerOptions { RootPath = "/" });
        var deadline = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var vm = new NotificationViewModel { Created = deadline };

        handler.FillViewModel(new SatoraStableNotification { StoreId = Store, SwapId = "swap", Problem = Problems.NearClaimCutoff, Deadline = deadline }, vm);

        Assert.Equal("SatoraPlugin/Index/store", vm.ActionLink);
        Assert.Equal("Satora stablecoin swap swap is funded but was not claimed into the Satora wallet by its claim deadline, 2027-01-15 08:00:00Z. Unless a claim already in progress completes, the customer's deposit has to be refunded from the swap contract.",
            vm.Body);
    }

    // The plugin's own page lists no swaps, so an alert names the swap's invoice and leads to it.
    [Theory]
    [InlineData("invoice-1", "UIInvoice/Invoice/invoice-1", "Satora stablecoin swap swap of invoice invoice-1 is funded")]
    // Sent before alerts named the invoice.
    [InlineData(null, "SatoraPlugin/Index/store", "Satora stablecoin swap swap is funded")]
    public void AnAlertAboutASwapNamesAndLeadsToItsInvoice(string? invoiceId, string link, string body)
    {
        INotificationHandler handler = new SatoraStableNotification.Handler(new RouteLinks(), new BTCPayServerOptions { RootPath = "/" });
        var vm = new NotificationViewModel { Created = DateTimeOffset.UtcNow };

        handler.FillViewModel(new SatoraStableNotification
        {
            StoreId = Store,
            SwapId = "swap",
            InvoiceId = invoiceId,
            Problem = Problems.NearClaimCutoff,
            Deadline = DateTimeOffset.UtcNow.AddMinutes(5)
        }, vm);

        Assert.Equal(link, vm.ActionLink);
        Assert.StartsWith(body, vm.Body);
    }

    [Theory]
    [InlineData(Problems.NeedsSweep)]
    [InlineData(Problems.GasNeeded)]
    [InlineData(Problems.SweepFailing)]
    [InlineData(Problems.NeedsHtlcRefund)]
    public void AnAlertAboutTokensToSendBackLinksTheRefundsPage(Problems problem)
    {
        INotificationHandler handler = new SatoraStableNotification.Handler(new RouteLinks(), new BTCPayServerOptions { RootPath = "/" });
        var vm = new NotificationViewModel { Created = DateTimeOffset.UtcNow };

        handler.FillViewModel(new SatoraStableNotification { StoreId = Store, SwapId = "swap", Problem = problem }, vm);

        Assert.Equal("SatoraStableRefund/Index/store", vm.ActionLink);
    }

    // Nothing is fixed while the address holds no gas, so the destination can still change until some arrives.
    [Fact]
    public async Task ASweepWaitsForGasAtTheAddressAndAlertsOnceWithoutFixingTheDestination()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        _wallets.Held[(Id, null)] = 500_000;

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"funds {Id}", $"funds {Id}"], _events);
        Assert.False(Row.SweepAttempted);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.GasNeeded, "0.0001 ETH on Arbitrum One"), (alert.Problem, alert.Amount));
        Assert.Contains("send it about 0.0001 ETH on Arbitrum One from any wallet", SatoraStableNotification.Body(alert, _worker.Clock));

        Row.SweepDestination = SatoraStableRefundsTests.OtherToken;
        _wallets.Gas[Id] = BigInteger.Pow(10, 14);
        _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal($"sweep {Id} {SatoraStableRefundsTests.OtherToken} ", _events[^2]);
        Assert.True(Row is { SweepAttempted: true, SweepTxHash: "0xsweep-1", CompletedAt: not null, ClosedReason: null });
        Assert.Equal((InvoiceEventData.EventSeverity.Success,
                $"The tokens at the deposit address {Row.DepositAddress} of Satora stablecoin swap {Id} were sent to {SatoraStableRefundsTests.OtherToken} in 0xsweep-1."),
            Assert.Single(_credit.Notes));
    }

    // The SDK records the transfer before sending it: a call after one that timed out returns its receipt.
    [Fact]
    public async Task ASweepIsStartedOnceAndARetryAfterATimeoutReturnsTheSameReceipt()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer, SatoraStableRefundsTests.OtherToken);
        _wallets.Held[(Id, SatoraStableRefundsTests.OtherToken)] = 7;
        _wallets.Gas[Id] = 1;
        _wallets.SweepThenTimeout = true;

        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { SweepAttempted: true, SweepTxHash: null, CompletedAt: null });
        _wallets.SweepThenTimeout = false;
        _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        await _worker.TickAsync(Store, Ct);

        var sweep = $"sweep {Id} {SatoraStableRefundsTests.Customer} {SatoraStableRefundsTests.OtherToken}";
        // The last read is of the swap's own token, which the other token's sweep left alone.
        Assert.Equal([$"funds {Id}", sweep, $"funds {Id}", sweep, $"funds {Id}", $"funds {Id}"], _events);
        Assert.Equal(1, _swaps.SweepStarts);
        Assert.Equal(1, _wallets.Transfers);
        Assert.True(Row is { SweepTxHash: "0xsweep-1", CompletedAt: not null });
        Assert.Single(_credit.Notes);
        Assert.Empty(_worker.Sent);
    }

    // The sweep sent the whole balance, so what is there after it arrived since.
    [Fact]
    public async Task TokensThatArriveAfterASweepGoToTheSameDestination()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        _wallets.Held[(Id, null)] = 500_000;
        _wallets.Gas[Id] = 1;
        _wallets.AfterSweep = () => _wallets.Held[(Id, null)] = 300_000;

        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { SweepTxHash: "0xsweep-1", CompletedAt: null });
        _wallets.AfterSweep = null;
        _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(2, _wallets.Transfers);
        Assert.Equal(1, _swaps.SweepStarts);
        Assert.True(Row is { SweepTxHash: "0xsweep-2", CompletedAt: not null });
        Assert.Equal(["0xsweep-1.", "0xsweep-2."], _credit.Notes.Select(n => n.Message[^10..]));
    }

    // An overpayment's excess beside a token sent by mistake: the swap does not close with the excess
    // still at the address, and where the other token went need not be where the excess goes.
    [Fact]
    public async Task TheSwapsOwnTokenLeftAfterAnotherTokensSweepWaitsForADestinationOfItsOwn()
    {
        const string storeAddress = "0x52908400098527886E0F7030069857D2E4169EE7";
        WaitingForSweep(SatoraStableRefundsTests.Customer, SatoraStableRefundsTests.OtherToken);
        _wallets.Held[(Id, SatoraStableRefundsTests.OtherToken)] = 7;
        _wallets.Held[(Id, null)] = 500_000;
        _wallets.Gas[Id] = 1;

        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is
        {
            CompletedAt: null, AttentionReason: SatoraStableAttention.Sweep, SweepToken: null, SweepDestination: null, SweepAttempted: false,
            SweepTxHash: null
        });
        Assert.Equal((InvoiceEventData.EventSeverity.Warning,
                $"The deposit address {Row.DepositAddress} of Satora stablecoin swap {Id} still holds 0.5 USDC. Enter a refund address for it on Satora's Stablecoin refunds page."),
            _credit.Notes[^1]);

        _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.Equal(1, _wallets.Transfers);

        Row.SweepDestination = storeAddress;
        _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal($"sweep {Id} {storeAddress} ", _events.Last(e => e.StartsWith("sweep ")));
        Assert.Equal(2, _wallets.Transfers);
        Assert.True(Row is { SweepTxHash: "0xsweep-2", CompletedAt: not null });
    }

    [Fact]
    public async Task TooLittleGasForTheSdksEstimateAlertsAsNoGas()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        _wallets.Held[(Id, null)] = 500_000;
        _wallets.Gas[Id] = 1;
        _wallets.SweepError = new SdkException(SdkErrorKind.InsufficientGas, "the depositor holds 1 wei and the transaction needs up to 900 wei; send it gas first");

        for (var i = 0; i < 3; i++)
        {
            await _worker.TickAsync(Store, Ct);
            _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        }

        Assert.Equal([Problems.GasNeeded], _worker.Sent.Select(n => n.Problem));
        Assert.True(Row is { SweepAttempted: true, CompletedAt: null });
    }

    [Fact]
    public async Task ASweepFailingTwiceAlertsOnce()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        _wallets.Held[(Id, null)] = 500_000;
        _wallets.Gas[Id] = 1;
        _wallets.SweepError = new SdkException(SdkErrorKind.Transport, "node unreachable");

        for (var i = 0; i < 3; i++)
        {
            await _worker.TickAsync(Store, Ct);
            _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        }

        Assert.Equal(3, _events.Count(e => e.StartsWith("sweep ")));
        Assert.Equal([Problems.SweepFailing], _worker.Sent.Select(n => n.Problem));
        Assert.Null(Row.CompletedAt);
    }

    // A signature signed before the swap was reopened for a sweep can still pull the deposit.
    [Fact]
    public async Task ASweepWaitsWithoutAlertingWhileARelaySignatureCanStillPullTheDeposit()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        _wallets.Held[(Id, null)] = 500_000;
        _wallets.Gas[Id] = 1;
        _wallets.SweepError = new SdkException(SdkErrorKind.UnresolvedFunding, "a relay funding signature can still pull the deposit");

        for (var i = 0; i < 3; i++)
        {
            await _worker.TickAsync(Store, Ct);
            _worker.Clock += SatoraStableWorker.SweepCheckInterval;
        }

        Assert.Empty(_worker.Sent);
        Assert.Null(Row.CompletedAt);
    }

    // The SDK lost the transfer's hash and finds nothing left to move: that transfer moved it.
    [Fact]
    public async Task AnEmptyAddressAfterASweepWhoseHashWasNotRecordedClosesTheSwap()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        Row.SweepAttempted = true;
        _wallets.Gas[Id] = 1;

        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { ClosedReason: SatoraStableRefunds.SweepUnrecordedReason, CompletedAt: not null, SweepTxHash: null });
        var note = Assert.Single(_credit.Notes);
        Assert.Equal(InvoiceEventData.EventSeverity.Warning, note.Severity);
        Assert.Contains("its transaction was not recorded", note.Message);
    }

    [Fact]
    public async Task AnAddressHoldingNothingWorthTheGasIsNotSwept()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        _wallets.Held[(Id, null)] = 9_999;
        _wallets.Gas[Id] = 1;

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"funds {Id}"], _events);
        Assert.True(Row is { SweepAttempted: false, CompletedAt: null });
        Assert.Empty(_worker.Sent);
    }

    // The owner dismissed the swap after the tick listed it: the swap is read again under the hold.
    [Fact]
    public async Task ASwapDismissedAfterTheTickReadItIsNotSwept()
    {
        WaitingForSweep(SatoraStableRefundsTests.Customer);
        _wallets.Held[(Id, null)] = 500_000;
        _wallets.Gas[Id] = 1;
        _swaps.BeforeFind = () => Row.CompletedAt = _worker.Clock;

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.False(Row.SweepAttempted);
    }

    private void Open(SdkSwapStatus? status, bool fundingAttempted = false, bool paymentRecorded = false, string? claimTxId = null, bool credited = false)
    {
        var swap = SatoraStableIntegrationTests.Swap(Store, "swap");
        swap.FundingAttemptedAt = fundingAttempted ? swap.CreatedAt : null;
        swap.PaymentRecorded = paymentRecorded;
        swap.ClaimTxId = claimTxId;
        swap.VhtlcSats = claimTxId is null ? null : 99_400;
        swap.Credited = credited;
        _swaps.Rows[Id] = swap;
        _worker.Clock = swap.CreatedAt;
        if (status is not null)
            _wallets.Statuses[Id] = status;
    }

    private void WaitingForSweep(string destination, string? token = null)
    {
        Open(null);
        Row.NeedsAttentionAt = _worker.Clock;
        Row.AttentionReason = SatoraStableAttention.Sweep;
        Row.SweepDestination = destination;
        Row.SweepToken = token;
    }

    private sealed class MemorySwaps() : SatoraStableSwaps(null!)
    {
        public Dictionary<string, SatoraStableSwap> Rows { get; } = [];

        // Copies, so only what the worker writes through these methods reaches the rows.
        public override Task<List<SatoraStableSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Values.Where(s => s.StoreId == storeId && s.CompletedAt == null).Select(Copy).ToList());

        private static SatoraStableSwap Copy(SatoraStableSwap row) => JsonSerializer.Deserialize<SatoraStableSwap>(JsonSerializer.Serialize(row))!;

        public Action? BeforeFind { get; set; }

        public int SweepStarts { get; private set; }

        public override Task<SatoraStableSwap?> FindAsync(string storeId, string swapId)
        {
            BeforeFind?.Invoke();
            return Task.FromResult(Rows.TryGetValue(swapId, out var row) && row.StoreId == storeId ? Copy(row) : null);
        }

        public override Task<bool> TryStartSweepAsync(string swapId, string destination, string? token) =>
            Guarded(swapId, row => row is { SweepTxHash: null, CompletedAt: null } && row.SweepDestination == destination && row.SweepToken == token, row =>
            {
                SweepStarts++;
                row.SweepAttempted = true;
            });

        public override Task SetSweepTxHashAsync(string swapId, string txHash) => Set(swapId, row => row.SweepTxHash = txHash);

        public override Task<bool> ResetSweepToOwnTokenAsync(string swapId, string sweptToken) =>
            Guarded(swapId, row => row.SweepToken == sweptToken && row.CompletedAt is null, row =>
            {
                row.SweepToken = row.SweepDestination = row.SweepTxHash = null;
                row.SweepAttempted = false;
            });

        public override Task SetStatusAsync(string swapId, SatoraSwapStatus status) => Set(swapId, row => row.Status = status);

        public override Task SetPaymentRecordedAsync(string swapId, bool recorded) => Set(swapId, row => row.PaymentRecorded = recorded);

        public override Task SetCreditedAsync(string swapId) => Set(swapId, row => row.Credited = row.PaymentRecorded = true);

        public override Task<bool> MarkFundingAttemptedAsync(string swapId, DateTimeOffset now) =>
            Guarded(swapId, row => row is { CompletedAt: null, NeedsAttentionAt: null } && row.FundingStartCutoff >= now,
                row => row.FundingAttemptedAt ??= now);

        public override Task<bool> SetFundedAsync(string swapId, string txHash) =>
            Guarded(swapId, row => row.FundTxHash is null, row => row.FundTxHash = txHash);

        public override Task<bool> SetClaimedAsync(string swapId, string claimTxId, long vhtlcSats) =>
            Guarded(swapId, row => row.ClaimTxId is null, row =>
            {
                row.ClaimTxId = claimTxId;
                row.VhtlcSats = vhtlcSats;
            });

        public override Task<bool> SetNeedsAttentionAsync(string swapId, string? claimTxId, SatoraStableAttention reason, DateTimeOffset now,
            string? sweepToken = null) =>
            Guarded(swapId, row => row is { NeedsAttentionAt: null, CompletedAt: null } && row.ClaimTxId == claimTxId, row =>
            {
                row.NeedsAttentionAt = now;
                row.AttentionReason = reason;
                row.SweepToken = sweepToken;
            });

        public override Task<bool> SetClosedAsync(string swapId, string reason, DateTimeOffset now) =>
            Guarded(swapId, row => row.CompletedAt is null, row =>
            {
                row.ClosedReason = reason;
                row.CompletedAt = now;
            });

        public override Task<bool> SetCompletedAsync(string swapId, DateTimeOffset completedAt) =>
            Guarded(swapId, row => row.CompletedAt is null, row => row.CompletedAt = completedAt);

        private Task Set(string swapId, Action<SatoraStableSwap> update)
        {
            update(Rows[swapId]);
            return Task.CompletedTask;
        }

        private Task<bool> Guarded(string swapId, Func<SatoraStableSwap, bool> expected, Action<SatoraStableSwap> update)
        {
            if (!expected(Rows[swapId]))
                return Task.FromResult(false);
            update(Rows[swapId]);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeWallets(List<string> events) : SatoraStableWallets(null!, null!, null!, null!)
    {
        public Dictionary<string, SdkSwapStatus> Statuses { get; } = [];
        public List<string> NotFound { get; } = [];
        public List<List<string>> StatusReads { get; } = [];
        public Exception? StatusError { get; set; }
        public Dictionary<string, BigInteger> Deposited { get; } = [];
        public Action? DuringRead { get; set; }
        public Exception? ClaimError { get; set; }
        public Func<string, SdkArkadeSettlement>? Settlement { get; set; }
        public bool ClaimPossible { get; set; }
        // What arkd's indexer shows of the VHTLC: by default, a claim spent all of it.
        public VhtlcAmounts Amounts { get; set; } = new(0, 0, 99_300, []);
        public Exception? AmountsError { get; set; }
        public Exception? ClaimAddressError { get; set; }
        // The SDK's relay journal of each swap: Sending once a request may have left, Funded once proven,
        // Expired once every signature expired unspent. None before anything was signed.
        public Dictionary<string, string> Journal { get; } = [];
        // A refusal before anything is signed.
        public Exception? FundRefusal { get; set; }
        // The request leaves, and the call times out before the relay answers.
        public bool SendThenTimeout { get; set; }
        // What a reconcile of a request that may have left finds.
        public SdkRelayFundingState? ReconcileAnswer { get; set; }
        // Requests that left for the relay.
        public int Sent { get; private set; }
        public List<TimeSpan> PermitDeadlines { get; } = [];
        // What each deposit address holds of a token, null for the swap's own, and of gas.
        public Dictionary<(string SwapId, string? Token), BigInteger> Held { get; } = [];
        public Dictionary<string, BigInteger> Gas { get; } = [];
        public Exception? SweepError { get; set; }
        // The transfer is sent and mined, and the call times out before the receipt.
        public bool SweepThenTimeout { get; set; }
        public Action? AfterSweep { get; set; }
        // Transfers sent; the SDK's last settled one is returned again once its balance is gone.
        public int Transfers { get; private set; }
        private readonly Dictionary<string, string> _settled = [];

        public override Task<BulkStatus> GetBulkStatusAsync(string storeId, IEnumerable<string> swapIds, CancellationToken cancellationToken)
        {
            var ids = swapIds.ToList();
            StatusReads.Add(ids);
            if (StatusError is not null)
                throw StatusError;
            return Task.FromResult(new BulkStatus(
                ids.Where(Statuses.ContainsKey).Select(id => new SwapStatusEntry(id, Statuses[id])).ToArray(),
                ids.Where(NotFound.Contains).ToArray()));
        }

        public override Task<string> GetClaimAddressAsync(string storeId, CancellationToken cancellationToken) =>
            ClaimAddressError is null ? Task.FromResult("own-address") : Task.FromException<string>(ClaimAddressError);

        // The conversion shortfall limit each claim was given, kept apart from the events, whose order
        // the other tests pin.
        public List<uint> ShortfallLimits { get; } = [];

        public override Task<SdkArkadeSettlement> ClaimAsync(SatoraStableSwap swap, string destination, uint shortfallLimit,
            CancellationToken cancellationToken)
        {
            events.Add($"claim {swap.SwapId} {destination}");
            ShortfallLimits.Add(shortfallLimit);
            if (ClaimError is not null)
                throw ClaimError;
            return Task.FromResult(Settlement?.Invoke(swap.SwapId) ?? new SdkArkadeSettlement.Offchain("ark-" + swap.SwapId, 99_400));
        }

        public override Task<bool> ClaimPossibleAsync(string storeId, string swapId) => Task.FromResult(ClaimPossible);

        public override Task<VhtlcAmounts> AmountsAsync(string storeId, string swapId, CancellationToken cancellationToken)
        {
            events.Add($"amounts {swapId}");
            if (AmountsError is not null)
                throw AmountsError;
            return Task.FromResult(Amounts);
        }

        public override Task<BigInteger> GetDepositedAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
        {
            events.Add($"read {swap.SwapId}");
            DuringRead?.Invoke();
            return Task.FromResult(Deposited.GetValueOrDefault(swap.SwapId));
        }

        public override Task<RelayFunding> FundAsync(SatoraStableSwap swap, TimeSpan permitDeadline, CancellationToken cancellationToken)
        {
            events.Add($"fund {swap.SwapId}");
            PermitDeadlines.Add(permitDeadline);
            if (Journal.GetValueOrDefault(swap.SwapId) is "Sending")
                throw new SdkException(SdkErrorKind.UnresolvedFunding, "relay funding may already be submitted; reconcile it before another attempt");
            if (FundRefusal is not null)
                throw FundRefusal;
            Sent++;
            Journal[swap.SwapId] = "Sending";
            if (SendThenTimeout)
                throw new SdkException(SdkErrorKind.Timeout, "relay funding timed out and its request may have been sent; reconcile it before another attempt");
            Journal[swap.SwapId] = "Funded";
            Deposited[swap.SwapId] -= BigInteger.Parse(swap.SourceUnits);
            return Task.FromResult(new RelayFunding("0xfund-" + swap.SwapId, true));
        }

        public override Task<SatoraDepositorFunds> GetFundsAsync(SatoraStableSwap swap, string? token, CancellationToken cancellationToken)
        {
            events.Add($"funds {swap.SwapId}");
            return Task.FromResult(new SatoraDepositorFunds(Held.GetValueOrDefault((swap.SwapId, token)), Gas.GetValueOrDefault(swap.SwapId)));
        }

        public override Task<EvmReceipt> SweepAsync(SatoraStableSwap swap, string destination, string? token, CancellationToken cancellationToken)
        {
            events.Add($"sweep {swap.SwapId} {destination} {token}");
            if (SweepError is not null)
                throw SweepError;
            if (Held.GetValueOrDefault((swap.SwapId, token)).IsZero)
                return _settled.TryGetValue(swap.SwapId, out var settled)
                    ? Task.FromResult(new EvmReceipt(settled))
                    : throw new SdkException(SdkErrorKind.InvalidSwap, "the depositor holds none of this token");
            Held[(swap.SwapId, token)] = 0;
            _settled[swap.SwapId] = $"0xsweep-{++Transfers}";
            AfterSweep?.Invoke();
            if (SweepThenTimeout)
                throw new SdkException(SdkErrorKind.Timeout, "deposit sweep timed out; call it again to verify or resume it");
            return Task.FromResult(new EvmReceipt(_settled[swap.SwapId]));
        }

        // Without a journal the SDK refuses before it reads anything.
        public override Task<SdkRelayFundingState> ReconcileAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
        {
            if (!Journal.TryGetValue(swap.SwapId, out var journal))
                throw new SdkException(SdkErrorKind.InvalidSwap, "no relay funding to reconcile");
            events.Add($"reconcile {swap.SwapId}");
            SdkRelayFundingState state = journal switch
            {
                "Funded" => new SdkRelayFundingState.Funded("0xfund-" + swap.SwapId),
                "Expired" => new SdkRelayFundingState.Expired(),
                _ => ReconcileAnswer ?? new SdkRelayFundingState.Pending(false, 1_800_000_000)
            };
            if (state is SdkRelayFundingState.Expired or SdkRelayFundingState.Funded)
                Journal[swap.SwapId] = state is SdkRelayFundingState.Expired ? "Expired" : "Funded";
            return Task.FromResult(state);
        }
    }

    private sealed class RecordingCredit(List<string> events)
        : SatoraStableCredit(null!, null!, null!, null!, NullLogger<SatoraStableCredit>.Instance)
    {
        public bool SettledOtherwise { get; set; }
        public Exception? ProgressError { get; set; }
        // The invoice's payment tolerance in percent; null without the invoice.
        public decimal? Tolerance { get; set; } = 1;
        public SatoraCreditOutcome Outcome { get; set; } = SatoraCreditOutcome.Recorded;

        // Kept apart from the events, whose order the other tests pin.
        public List<(InvoiceEventData.EventSeverity Severity, string Message)> Notes { get; } = [];

        public override Task<SatoraCreditOutcome> RecordAsync(SatoraStableSwap swap, PaymentStatus status, DateTimeOffset at)
        {
            events.Add(status == PaymentStatus.Settled ? $"record {swap.SwapId} {status} {Sats(swap, status)}" : $"record {swap.SwapId} {status}");
            return Task.FromResult(Outcome);
        }

        public override Task UnaccountAsync(SatoraStableSwap swap)
        {
            events.Add($"unaccount {swap.SwapId}");
            return Task.CompletedTask;
        }

        public override Task DeactivatePromptAsync(string invoiceId, PaymentMethodId method, string swapId)
        {
            events.Add($"deactivate {swapId}");
            return Task.CompletedTask;
        }

        public override Task SetDepositProgressAsync(string invoiceId, PaymentMethodId method, string swapId, string receivedUnits)
        {
            events.Add($"progress {swapId} {receivedUnits}");
            return ProgressError is null ? Task.CompletedTask : throw ProgressError;
        }

        public override Task<bool> SettledOtherwiseAsync(SatoraStableSwap swap) => Task.FromResult(SettledOtherwise);

        public override Task<decimal?> PaymentToleranceAsync(SatoraStableSwap swap) => Task.FromResult(Tolerance);

        public override Task NoteAsync(SatoraStableSwap swap, string message, InvoiceEventData.EventSeverity severity)
        {
            Notes.Add((severity, message));
            return Task.CompletedTask;
        }
    }

    private sealed class TestWorker(SatoraStableSwaps swaps, SatoraStableWallets wallets, SatoraStableCredit credit, SatoraSentAlerts? sentAlerts = null,
        ILogger<SatoraStableWorker>? logger = null)
        : SatoraStableWorker(swaps, wallets, credit, null!, sentAlerts ?? new MemorySentAlerts(), logger ?? NullLogger<SatoraStableWorker>.Instance)
    {
        public DateTimeOffset Clock { get; set; } = DateTimeOffset.UtcNow;
        public List<SatoraStableNotification> Sent { get; } = [];

        protected override DateTimeOffset UtcNow => Clock;

        protected override Task SendAsync(SatoraStableNotification notification)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }
}
