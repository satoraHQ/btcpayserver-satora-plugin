using System.Text.Json;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Satora.Sdk;
using Xunit;
using Problems = BTCPayServer.Plugins.Satora.Chain.SatoraChainNotification.Problems;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;

namespace BTCPayServer.Plugins.Satora.Tests;

// One worker tick per step, over rows kept in memory, and an SDK, esplora and invoice that answer as
// each test sets them up.
public class SatoraChainWorkerTests
{
    private const string Store = "store";
    private const string Address = "bcrt1prefund";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly List<string> _events = [];
    private readonly MemorySwaps _swaps = new();
    private readonly FakeWallets _wallets;
    private readonly RecordingCredit _credit;
    private readonly MemorySentAlerts _sentAlerts = new();
    private readonly TestWorker _worker;

    public SatoraChainWorkerTests()
    {
        _wallets = new FakeWallets(_events);
        _credit = new RecordingCredit(_events);
        _worker = new TestWorker(_swaps, _wallets, _credit, sentAlerts: _sentAlerts);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SatoraChainSwap Row => _swaps.Rows["swap"];

    [Fact]
    public async Task APendingSwapReadsNoChainAndRecordsNothing()
    {
        Open(new SdkSwapStatus.Pending());

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.True(Row is { PaymentRecorded: false, CompletedAt: null, NeedsRefundAt: null });
    }

    // The server marks an underpayment it sees in the mempool ClientFundingSeen too.
    [Theory]
    [InlineData(99_999L, false)]
    [InlineData(100_000L, true)]
    [InlineData(150_000L, true)]
    public async Task RecordsAProcessingPaymentOnlyForAnOutputPayingTheDeposit(long value, bool recorded)
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(value);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        // A mempool output is read again until it confirms.
        Assert.Equal(recorded
            ? ["read htlc-swap", "record swap Processing", "read htlc-swap"]
            : ["read htlc-swap", "read htlc-swap"], _events);
        Assert.Equal(recorded, Row.PaymentRecorded);
        // An overpayment is only remembered until the claim.
        Assert.Equal(recorded ? value - 100_000 : 0, Row.OverpaidSats);
        Assert.Empty(_worker.Sent);
    }

    // A confirmed funding the worker missed while it was seen only in the mempool.
    [Fact]
    public async Task RecordsAConfirmedFundingItHadNotSeen()
    {
        Open(new SdkSwapStatus.ClientFunded());
        Pay(100_000, confirmed: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "record swap Processing"], _events);
    }

    [Fact]
    public async Task ClaimsAFundedSwapIntoTheWalletsOwnAddressAndSettlesItWithTheReceipt()
    {
        Open(new SdkSwapStatus.ServerFunded(), paymentRecorded: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "claim swap own-address", "record swap Settled"], _events);
        // Open until the server has swept the customer's output, so a second payment can still be found.
        Assert.True(Row is { ClaimTxId: "ark-swap", Credited: true, CompletedAt: null });

        _events.Clear();
        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerRedeemed();
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap"], _events);
        Assert.NotNull(Row.CompletedAt);
        Assert.Empty(_worker.Sent);
    }

    // Less than the deposit, so it cannot be the output the swap server swept: flagged on the first read.
    [Fact]
    public async Task ASettledSwapWithBitcoinLeftAtItsAddressNeedsARefundAndKeepsItsPayment()
    {
        Open(new SdkSwapStatus.ServerRedeemed(), claimTxId: "ark-swap", credited: true);
        Pay(20_000);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "deactivate swap"], _events);
        Assert.True(Row is { NeedsRefundReason: SatoraChainActions.LeftoverReason, CompletedAt: null });
        Assert.NotNull(Row.NeedsRefundAt);
        Assert.Equal([Problems.LeftoverOutputs], _worker.Sent.Select(n => n.Problem));
    }

    // The swap server reports ServerRedeemed once it has broadcast its sweep, before this esplora may list it.
    [Fact]
    public async Task ASweepThisEsploraHasNotListedYetDoesNotFlagASettledSwapForARefund()
    {
        Open(new SdkSwapStatus.ServerRedeemed(), claimTxId: "ark-swap", credited: true);
        Pay(100_000, confirmed: true);

        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { NeedsRefundAt: null, CompletedAt: null });
        _worker.Clock += SatoraChainWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        _wallets.Outputs.Clear();
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        // Not read in between.
        Assert.Equal(["read htlc-swap", "read htlc-swap"], _events);
        Assert.True(Row is { NeedsRefundAt: null, CompletedAt: not null });
        Assert.Empty(_worker.Sent);
    }

    // A second full payment after the swap settled looks like the swept funding until the second read.
    [Fact]
    public async Task AnOutputPayingTheDepositStillAtASweptAddressTenMinutesLaterNeedsARefund()
    {
        Open(new SdkSwapStatus.ServerRedeemed(), claimTxId: "ark-swap", credited: true);
        Pay(100_000, confirmed: true);
        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval - TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { NeedsRefundAt: null, CompletedAt: null });
        Assert.Empty(_worker.Sent);

        _worker.Clock += TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "read htlc-swap", "deactivate swap"], _events);
        Assert.True(Row is { NeedsRefundReason: SatoraChainActions.LeftoverReason, CompletedAt: null });
        Assert.Equal([Problems.LeftoverOutputs], _worker.Sent.Select(n => n.Problem));
    }

    // Past the cutoff without a receipt, a swept address is read again the same way, and the missing
    // receipt is logged once meanwhile.
    [Fact]
    public async Task ARedeemedSwapWithoutAReceiptReadsItsSweptAddressAgainBeforeFlaggingIt()
    {
        var logs = new RecordingLogger<SatoraChainWorker>();
        var worker = new TestWorker(_swaps, _wallets, new RecordingCredit(_events), logs);
        Open(new SdkSwapStatus.ServerRedeemed(), paymentRecorded: true);
        Pay(100_000, confirmed: true);
        _wallets.ClaimError = new InvalidOperationException("arkd unreachable");
        worker.Clock = Row.ClaimCutoff;
        await worker.TickAsync(Store, Ct);
        worker.Clock += SatoraChainWorker.Interval;
        await worker.TickAsync(Store, Ct);
        Assert.True(Row is { NeedsRefundAt: null, CompletedAt: null });

        _wallets.Outputs.Clear();
        worker.Clock = Row.ClaimCutoff + SatoraChainWorker.RefundCheckInterval;
        await worker.TickAsync(Store, Ct);

        Assert.Equal(["claim swap own-address", "read htlc-swap", "claim swap own-address",
            "claim swap own-address", "read htlc-swap", "unaccount swap"], _events);
        Assert.True(Row is { Credited: false, NeedsRefundAt: null, CompletedAt: not null });
        Assert.Single(logs.Entries, e => e.Level == LogLevel.Error);
    }

    [Theory]
    [InlineData(typeof(SdkSwapStatus.ClientInvalidFunded))]
    [InlineData(typeof(SdkSwapStatus.ServerWontFund))]
    [InlineData(typeof(SdkSwapStatus.ClientFundedServerRefunded))]
    public async Task AFailedSwapTakesItsPaymentOffTheInvoiceAndWaitsForTheOwnersRefund(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, paymentRecorded: true);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        // The prompt showing this swap's address, if still the invoice's, goes inactive so a new swap takes what is due.
        Assert.Equal(["unaccount swap", "deactivate swap"], _events);
        Assert.True(Row is { CompletedAt: null, RefundTxId: null });
        Assert.Equal(status.Name, Row.NeedsRefundReason);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.NeedsRefund, "swap", status.Name, Row.BtcRefundLocktime), (alert.Problem, alert.SwapId, alert.Status, alert.Deadline));
        // Waiting for a refund, the swap's status is no longer read.
        Assert.Single(_wallets.StatusReads);
    }

    [Fact]
    public async Task AFailedSwapWithoutAPaymentTakesNothingOffTheInvoice()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded());

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["deactivate swap"], _events);
        Assert.NotNull(Row.NeedsRefundAt);
    }

    // Seen in the mempool or confirmed, the server funds no VHTLC after its locktime, and the wallet
    // never claims past its cutoff, so the preimage stays private and the customer gets it back.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.ClientFundingSeen))]
    [InlineData(typeof(SdkSwapStatus.ClientFunded))]
    public async Task AFundingTheServerHasNotTakenByTheVhtlcLocktimeNeedsARefund(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, paymentRecorded: true);
        Pay(100_000, confirmed: status == typeof(SdkSwapStatus.ClientFunded));
        _worker.Clock = Row.VhtlcRefundLocktime;

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "unaccount swap", "deactivate swap"], _events);
        Assert.True(Row is { CompletedAt: null, NeedsRefundAt: not null });
        Assert.Equal(status.Name, Row.NeedsRefundReason);
    }

    // The funding the server saw never confirmed and has left the mempool: nothing to give back, once a
    // second read RefundCheckInterval later agrees.
    [Fact]
    public async Task AFundingThatLeftTheMempoolByTheVhtlcLocktimeClosesTheSwap()
    {
        Open(new SdkSwapStatus.ClientFundingSeen(), paymentRecorded: true);
        _worker.Clock = Row.VhtlcRefundLocktime;
        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval - TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);
        Assert.Null(Row.CompletedAt);

        _worker.Clock += TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "read htlc-swap", "read htlc-swap", "unaccount swap"], _events);
        Assert.True(Row is { CompletedAt: not null, NeedsRefundAt: null });
        Assert.Empty(_worker.Sent);
    }

    // The swap server's node saw a funding this esplora had not yet.
    [Fact]
    public async Task AFundingThisEsploraSeesOnlyAfterTheVhtlcLocktimeStillNeedsARefund()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        _worker.Clock = Row.VhtlcRefundLocktime;
        await _worker.TickAsync(Store, Ct);

        Pay(100_000);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { CompletedAt: null, NeedsRefundAt: not null });
        Assert.Equal(nameof(SatoraSwapStatus.ClientFundingSeen), Row.NeedsRefundReason);

        // Replaced later: the empty read before the funding showed does not count toward closing.
        _wallets.Outputs.Clear();
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);

        Assert.Null(Row.CompletedAt);
    }

    // An empty read from before the swap needed a refund says nothing of the outputs that put it there.
    [Fact]
    public async Task AnEmptyReadFromBeforeTheSwapNeededARefundDoesNotCountTowardClosingIt()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        _worker.Clock = Row.VhtlcRefundLocktime;
        await _worker.TickAsync(Store, Ct);

        _wallets.Statuses["swap"] = new SdkSwapStatus.Expired();
        Pay(100_000, confirmed: true);
        _worker.Clock += SatoraChainWorker.Interval;
        await _worker.TickAsync(Store, Ct);
        Assert.Equal(SatoraChainActions.LeftoverReason, Row.NeedsRefundReason);

        _wallets.Outputs.Clear();
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);

        Assert.Null(Row.CompletedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnExpiredSwapClosesOnlyOnceItsAddressIsEmpty(bool paidLate)
    {
        Open(new SdkSwapStatus.Expired());
        if (paidLate)
            Pay(100_000);
        _worker.Clock = Row.InvoiceExpiresAt + SatoraChainActions.LatePaymentMargin;

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(paidLate ? ["read htlc-swap", "deactivate swap"] : ["read htlc-swap"], _events);
        Assert.Equal(paidLate, Row.NeedsRefundAt is not null);
        Assert.Equal(!paidLate, Row.CompletedAt is not null);
    }

    // A server with a short funding window may expire a swap while its invoice can still be paid.
    [Fact]
    public async Task AnExpiredSwapStaysOpenForAPaymentMadeByTheInvoicesExpiry()
    {
        Open(new SdkSwapStatus.Expired());
        _worker.Clock = Row.InvoiceExpiresAt;

        await _worker.TickAsync(Store, Ct);
        Assert.Equal(["read htlc-swap", "deactivate swap"], _events);
        Assert.True(Row is { CompletedAt: null, NeedsRefundAt: null });

        _events.Clear();
        Pay(100_000);
        _worker.Clock = Row.InvoiceExpiresAt + SatoraChainActions.LatePaymentMargin - TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "deactivate swap"], _events);
        Assert.Equal(SatoraChainActions.LeftoverReason, Row.NeedsRefundReason);
    }

    [Theory]
    [InlineData(typeof(SdkSwapStatus.ClientRedeemed))]
    [InlineData(typeof(SdkSwapStatus.ServerRedeemed))]
    public async Task ClaimsARedeemedSwapAgainForItsReceiptBeforeCrediting(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, paymentRecorded: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim swap own-address", "record swap Settled"], _events);
        Assert.True(Row is { ClaimTxId: "ark-swap", Credited: true });
    }

    // The claim failed before the cutoff, and after it the SDK has no journaled receipt to return.
    [Fact]
    public async Task NeverCreditsARedeemedSwapPastItsCutoffWithoutAReceipt()
    {
        Open(new SdkSwapStatus.ClientRedeemed(), paymentRecorded: true);
        _wallets.ClaimError = new InvalidOperationException("arkd unreachable");
        _worker.Clock = Row.ClaimCutoff;

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim swap own-address", "claim swap own-address"], _events);
        Assert.True(Row is { Credited: false, CompletedAt: null });
        Assert.Equal([Problems.ClaimUnverified], _worker.Sent.Select(n => n.Problem));

        // Once the server has swept the customer's output, the swap closes with its payment taken off,
        // but only once the SDK says no claim can complete. A query that fails moves nothing.
        _events.Clear();
        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerRedeemed();
        _wallets.ClaimPossible = true;
        await _worker.TickAsync(Store, Ct);
        _wallets.ClaimPossibleError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        await _worker.TickAsync(Store, Ct);
        Assert.Equal(["claim swap own-address", "claim swap own-address"], _events);
        Assert.True(Row is { PaymentRecorded: true, CompletedAt: null });

        _events.Clear();
        _wallets.ClaimPossibleError = null;
        _wallets.ClaimPossible = false;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim swap own-address", "read htlc-swap", "unaccount swap"], _events);
        Assert.True(Row is { Credited: false, CompletedAt: not null });
    }

    [Theory]
    [InlineData(typeof(SdkSwapStatus.ClientFundedTooLate))]
    [InlineData(typeof(SdkSwapStatus.ClientRefunded))]
    public async Task AStatusTheTableDoesNotHandleMovesNothingAndAlertsOnce(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, paymentRecorded: true);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.True(Row is { CompletedAt: null, NeedsRefundAt: null });
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.UnexpectedStatus, status.Name), (alert.Problem, alert.Status));
    }

    [Fact]
    public async Task AlertsOnTheSecondFailedClaimAndNearTheCutoff()
    {
        Open(new SdkSwapStatus.ServerFunded());
        _wallets.ClaimError = new InvalidOperationException("arkd unreachable");

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        await _worker.TickAsync(Store, Ct);
        _worker.Clock = Row.ClaimCutoff - TimeSpan.FromMinutes(5);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([Problems.ClaimFailing, Problems.NearClaimCutoff], _worker.Sent.Select(n => n.Problem));
        Assert.True(Row is { ClaimTxId: null, Credited: false });
    }

    [Fact]
    public async Task RefundsOnlyToTheOwnersAddressAndOnlyOnceTheHtlcLocktimeHasPassed()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true);
        Pay(90_000, confirmed: true);
        _worker.Clock = Row.BtcRefundLocktime;
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events);

        Row.RefundAddress = Address;
        _worker.Clock = Row.BtcRefundLocktime.AddSeconds(-1);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events);

        _worker.Clock = Row.BtcRefundLocktime;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"refund swap {Address}"], _events);
        // Open until the refund confirms.
        Assert.True(Row is { RefundAttempted: true, RefundTxId: "refund-1", CompletedAt: null });

        _events.Clear();
        _wallets.Confirmed.Add("refund-1");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"refund swap {Address}", "confirmed refund-1", "read htlc-swap"], _events);
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: not null, ClosedReason: null });
        Assert.Empty(_worker.Sent);
    }

    // RefundBitcoinAsync left one output for later (over its fee cap); called again once its refund
    // confirmed, it sweeps the other.
    [Fact]
    public async Task FollowsAPartialRefundUntilWhatIsLeftIsSweptAndConfirmed()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(60_000, confirmed: true, txId: "first");
        Pay(50_000, confirmed: true, txId: "second");
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);

        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null });

        // Not due again yet, and still unconfirmed when it is.
        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null });

        _wallets.Confirmed.Add("refund-1");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { RefundTxId: "refund-2", CompletedAt: null });

        // The address is empty now, but the sweep can still leave the mempool.
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { RefundTxId: "refund-2", CompletedAt: null });

        _wallets.Confirmed.Add("refund-2");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-2", CompletedAt: not null });
        Assert.Equal(5, _events.Count(e => e == $"refund swap {Address}"));
        Assert.Empty(_wallets.Outputs);
    }

    // The SDK leaves an output its own refund fee would eat. The swap closes once only such outputs
    // have been left for a day, counted again after a read that finds one worth a refund, and the store
    // is told how to reopen it.
    [Fact]
    public async Task ARefundClosesTheSwapOnceOnlyDustHasBeenLeftBehindForADay()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(60_000, confirmed: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);

        Pay(800, confirmed: true, txId: "dust");
        _wallets.Confirmed.Add("refund-1");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        var leftSince = _worker.Clock;
        // At 1 sat/vB a one-input refund of the dust is worth sending.
        _wallets.FeeRateSatVb = 1;
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        _wallets.FeeRateSatVb = 10;
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        var leftAgainSince = _worker.Clock;
        _worker.Clock = leftSince + SatoraChainWorker.UneconomicCloseAfter;
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null });
        Assert.Empty(_worker.Sent);

        _worker.Clock = leftAgainSince + SatoraChainWorker.UneconomicCloseAfter;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: not null, ClosedReason: null });
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.NothingToRefund, SatoraChainRefunds.LeftBehindStatus), (alert.Problem, alert.Status));
        Assert.Contains("confirmed, but its address now holds less than the network fee to refund it", SatoraChainNotification.Body(alert, _worker.Clock));
        Assert.Contains(SatoraChainRefunds.ReopenStatement("swap"), SatoraChainNotification.Body(alert, _worker.Clock));
        Assert.EndsWith("The swap is closed, and its address holds less than the network fee to refund it.", _credit.Notes[^1].Message);
    }

    // A payment reaching the address after the refund, during a fee spike: the SDK leaves it while each
    // follow asks again, and sweeps it once fees fall, rather than the swap closing on the spike.
    [Fact]
    public async Task APaymentLeftAfterARefundDuringAFeeSpikeIsSweptOnceFeesFall()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(60_000, confirmed: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);

        Pay(10_000, confirmed: true, txId: "later");
        _wallets.Confirmed.Add("refund-1");
        _wallets.FeeRateSatVb = 80;
        _wallets.SpendsFromSats = 80 * SatoraChainWallets.RefundVbytes + SatoraChainActions.DustSats;
        for (var tick = 0; tick < 12 * 120; tick++)
        {
            _worker.Clock += SatoraChainWorker.Interval;
            await _worker.TickAsync(Store, Ct);
        }
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null });

        _wallets.FeeRateSatVb = 5;
        _wallets.SpendsFromSats = 5 * SatoraChainWallets.RefundVbytes + SatoraChainActions.DustSats;
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-2", CompletedAt: null });
        Assert.Empty(_worker.Sent);
    }

    // The SDK refuses until median time past, an hour or more behind the clock, passes the locktime;
    // only refusals from when it should have caught up count toward the alert.
    [Fact]
    public async Task KeepsRetryingARefundAndAlertsOnlyOnceMedianTimeShouldHaveCaughtUp()
    {
        Open(new SdkSwapStatus.ServerWontFund(), needsRefund: true, refundAddress: Address);
        Pay(100_000, confirmed: true);
        _wallets.RefundError = new SdkException(SdkErrorKind.InvalidSwap, "Bitcoin refund key mismatch or timelock not mature");
        _worker.Clock = Row.BtcRefundLocktime;

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);

        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        Assert.Equal([Problems.RefundFailing], _worker.Sent.Select(n => n.Problem));

        _wallets.RefundError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(6, _events.Count(e => e == $"refund swap {Address}"));
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null });
    }

    // As a refund on a client BTCPay disposed while the worker stopped.
    [Fact]
    public async Task ARefundFailingAsTheWorkerStopsIsNotCountedAsFailing()
    {
        Open(new SdkSwapStatus.ServerWontFund(), needsRefund: true, refundAddress: Address);
        Pay(100_000, confirmed: true);
        _wallets.RefundError = new SdkException(SdkErrorKind.Transport, "esplora unreachable");
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        using var stopping = new CancellationTokenSource();
        _wallets.DuringRefund = stopping.Cancel;
        _wallets.RefundError = new ObjectDisposedException("client");

        await Assert.ThrowsAsync<ObjectDisposedException>(() => _worker.TickAsync(Store, stopping.Token));

        Assert.Empty(_worker.Sent);
        _wallets.DuringRefund = null;
        _wallets.RefundError = new SdkException(SdkErrorKind.Transport, "esplora unreachable");
        await _worker.TickAsync(Store, Ct);
        Assert.Equal(3, _events.Count(e => e == $"refund swap {Address}"));
        Assert.Equal([Problems.RefundFailing], _worker.Sent.Select(n => n.Problem));
    }

    // Past the cutoff a failed claim alerts and, once the server swept the customer's output, can close
    // the swap with its payment taken off. The worker starts stopping while the claim address is read.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClaimPastItsCutoffTheWorkerStopsDuringNeitherClosesTheSwapNorAlerts(bool addressReadFails)
    {
        Open(new SdkSwapStatus.ServerRedeemed(), paymentRecorded: true);
        _worker.Clock = Row.ClaimCutoff;
        using var stopping = new CancellationTokenSource();
        _wallets.Address = () =>
        {
            stopping.Cancel();
            return addressReadFails ? throw new OperationCanceledException(stopping.Token) : "own-address";
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.TickAsync(Store, stopping.Token));

        Assert.Empty(_events);
        Assert.True(Row is { PaymentRecorded: true, Credited: false, CompletedAt: null });
        Assert.Empty(_worker.Sent);
    }

    // The worker starts stopping while it asks whether a claim can still complete, a call it cannot
    // cancel: the owner can still change the refund address.
    [Fact]
    public async Task ARefundTheWorkerStopsBeforeLeavesItsAddressOpenToChange()
    {
        Open(new SdkSwapStatus.ServerFunded(), needsRefund: true, refundAddress: Address);
        Row.NeedsRefundReason = SatoraChainActions.UnclaimedReason;
        Pay(100_000, confirmed: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        using var stopping = new CancellationTokenSource();
        _wallets.DuringClaimPossibleRead = stopping.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.TickAsync(Store, stopping.Token));

        Assert.Equal(1, _wallets.ClaimPossibleReads);
        Assert.DoesNotContain(_events, e => e.StartsWith("refund", StringComparison.Ordinal));
        Assert.True(Row is { RefundAttempted: false, RefundTxId: null, CompletedAt: null });
    }

    // A dropped underpayment, or one RBF'd elsewhere: once the refund could go out, there is nothing to send.
    [Fact]
    public async Task ASwapWaitingForARefundWithAnEmptyAddressClosesAndStopsCountingAsOpen()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row).AddSeconds(-1);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events);

        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        Assert.Null(Row.CompletedAt);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "read htlc-swap"], _events);
        Assert.True(Row is { CompletedAt: not null, RefundTxId: null, ClosedReason: SatoraChainRefunds.EmptyReason });
        Assert.Empty(await _swaps.ListOpenAsync(Store, Ct));
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.NothingToRefund, SatoraChainRefunds.EmptyReason), (alert.Problem, alert.Status));
    }

    // The store dismissed the swap while the worker read its empty address: the store's close stands,
    // and the store is not told the swap closed without a refund.
    [Fact]
    public async Task AnEmptyAddressDoesNotCloseOverTheStoresDismissal()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        var dismissedAt = _worker.Clock;
        _wallets.DuringRead = () => Dismiss(dismissedAt);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;

        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { ClosedReason: SatoraChainRefunds.DismissedReason } row && row.CompletedAt == dismissedAt);
        Assert.Empty(_worker.Sent);
        Assert.Empty(_credit.Notes);
    }

    // The SDK refuses every refund from an empty address, and the swap closes on its own, so the store is
    // told only that.
    [Fact]
    public async Task AnEmptyAddressWithARefundAddressIsReportedOnlyAsNothingToRefund()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        for (var tick = 0; tick < 20; tick++)
        {
            _worker.Clock += SatoraChainWorker.Interval;
            await _worker.TickAsync(Store, Ct);
        }

        Assert.Equal(20, _events.Count(e => e == $"refund swap {Address}"));
        Assert.True(Row is { CompletedAt: not null, RefundTxId: null, ClosedReason: SatoraChainRefunds.EmptyReason });
        Assert.Equal([Problems.NothingToRefund], _worker.Sent.Select(n => n.Problem));
    }

    // Two empty reads in a row: an output seen in between starts the count again.
    [Fact]
    public async Task OnlyAnAddressReadEmptyTwiceInARowClosesItsSwap()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        Pay(100_000, confirmed: true);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        _wallets.Outputs.Clear();
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.Null(Row.CompletedAt);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(SatoraChainRefunds.EmptyReason, Row.ClosedReason);
    }

    // At the esplora estimate the refund would pay 1 400 sats in fees, more than the output is worth;
    // the swap closes once that has held for a day, and the store is told how to reopen it.
    [Theory]
    [InlineData(1_000L, true)]
    [InlineData(5_000L, false)]
    public async Task ASwapWaitingForARefundWorthLessThanItsFeeForADayCloses(long value, bool closed)
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true);
        Pay(value, confirmed: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        // Read once, and not again for RefundCheckInterval.
        Assert.Equal(["read htlc-swap", "estimate fee"], _events);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.Null(Row.CompletedAt);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row) + SatoraChainWorker.UneconomicCloseAfter;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(closed, Row.CompletedAt is not null);
        Assert.Equal(closed ? SatoraChainRefunds.UneconomicReason : null, Row.ClosedReason);
        Assert.Equal(closed ? [Problems.NothingToRefund] : [], _worker.Sent.Select(n => n.Problem));
        Assert.All(_worker.Sent, alert => Assert.Contains(
            "closed without a refund because its address holds less than the network fee to refund it. To refund it after all, " +
            "a server admin can list the swap on the On-chain refunds page again by running this in BTCPay's database: " +
            "UPDATE \"BTCPayServer.Plugins.Satora\".\"SatoraChainSwaps\" SET \"CompletedAt\" = NULL, \"ClosedReason\" = NULL WHERE \"SwapId\" = 'swap';",
            SatoraChainNotification.Body(alert, _worker.Clock)));
    }

    // While a fee spike makes the output worth less than its refund the SDK is still asked, once per
    // read of the address, and its refusals are expected rather than failures. Once fees fall the
    // refund goes out instead of the swap closing.
    [Fact]
    public async Task ARefundWorthLessThanItsFeeOnlyDuringAFeeSpikeStillGoesOut()
    {
        var logs = new RecordingLogger<SatoraChainWorker>();
        var worker = new TestWorker(_swaps, _wallets, _credit, logs);
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(10_000, confirmed: true);
        _wallets.FeeRateSatVb = 80;
        _wallets.SpendsFromSats = 80 * SatoraChainWallets.RefundVbytes + SatoraChainActions.DustSats;
        worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        for (var tick = 0; tick < 2 * 120; tick++)
        {
            await worker.TickAsync(Store, Ct);
            worker.Clock += SatoraChainWorker.Interval;
        }
        Assert.True(Row is { CompletedAt: null, RefundTxId: null });
        Assert.Equal(12, _events.Count(e => e == "read htlc-swap"));
        Assert.Equal(12, _events.Count(e => e == $"refund swap {Address}"));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Warning);

        _wallets.FeeRateSatVb = 5;
        _wallets.SpendsFromSats = 5 * SatoraChainWallets.RefundVbytes + SatoraChainActions.DustSats;
        await worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null, ClosedReason: null });
        Assert.Empty(worker.Sent);
    }

    // Three outputs of 1 800 sats each fall short of a one-input refund at 10 sat/vB, 1 946 sats with
    // dust, but the SDK spends them together: each further input costs about 0.8k, and about 2.6k goes
    // back. The estimate does not decide whether the SDK is asked.
    [Fact]
    public async Task OutputsEachWorthLessThanARefundsFeeAreStillOfferedToTheSdk()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(1_800, confirmed: true, txId: "first");
        Pay(1_800, confirmed: true, txId: "second");
        Pay(1_800, confirmed: true, txId: "third");
        Assert.True(SatoraChainActions.NothingToRefund(_wallets.Outputs.Select(o => o.Output), 10 * SatoraChainWallets.RefundVbytes));
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "estimate fee", $"refund swap {Address}"], _events);
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null });
    }

    // The SDK agrees all day that the output is worth less than its fee: the swap closes, and the store
    // hears only that.
    [Fact]
    public async Task ARefundTheSdkRefusesForADayAsWorthLessThanItsFeeClosesTheSwap()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(1_000, confirmed: true);
        _wallets.SpendsFromSats = 1_946;
        var from = SatoraChainActions.RefundPossibleFrom(Row);
        for (var after = TimeSpan.Zero; after <= SatoraChainWorker.UneconomicCloseAfter; after += SatoraChainWorker.Interval)
        {
            _worker.Clock = from + after;
            await _worker.TickAsync(Store, Ct);
        }

        Assert.Equal(144, _events.Count(e => e == $"refund swap {Address}"));
        Assert.True(Row is { RefundTxId: null, ClosedReason: SatoraChainRefunds.UneconomicReason } row && row.CompletedAt == from + SatoraChainWorker.UneconomicCloseAfter);
        Assert.Equal([Problems.NothingToRefund], _worker.Sent.Select(n => n.Problem));
    }

    // The node took the refund but its answer never arrived: the HTLC output is spent in the mempool,
    // missing from esplora's utxo list, until an attempt returns the refund from the SDK's journal. The
    // attempts keep failing for longer than two address reads, as when arkd cannot be reached to open
    // the wallet after a restart.
    [Fact]
    public async Task ARefundThatFailedAfterTheNodeTookItIsNeverTakenForAnEmptyAddress()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(100_000, confirmed: true);
        _wallets.BroadcastThenFail = new HttpRequestException("esplora POST /tx timed out");
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_wallets.Outputs);

        _wallets.RefundError = new HttpRequestException("arkd unreachable");
        for (var tick = 0; tick < 3 * 20; tick++)
        {
            _worker.Clock += SatoraChainWorker.Interval;
            await _worker.TickAsync(Store, Ct);
        }
        Assert.True(Row is { CompletedAt: null, RefundTxId: null });
        _wallets.RefundError = null;
        _worker.Clock += SatoraChainWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null, ClosedReason: null });
        Assert.Contains("mempool htlc-swap", _events);
        Assert.Equal([Problems.RefundFailing], _worker.Sent.Select(n => n.Problem));
    }

    // The refund the node took confirms while every attempt to learn of it fails: the address's history
    // shows a spend paying the refund address, so the store is never told nothing was refunded.
    [Fact]
    public async Task AConfirmedRefundNobodyRecordedKeepsTheSwapOpenUntilItIsRecorded()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(100_000, confirmed: true);
        _wallets.BroadcastThenFail = new HttpRequestException("esplora POST /tx timed out");
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        _wallets.Confirmed.Add("refund-1");

        _wallets.RefundError = new HttpRequestException("arkd unreachable");
        for (var tick = 0; tick < 3 * 20; tick++)
        {
            _worker.Clock += SatoraChainWorker.Interval;
            await _worker.TickAsync(Store, Ct);
        }
        Assert.True(Row is { CompletedAt: null, RefundTxId: null, ClosedReason: null });
        _wallets.RefundError = null;
        _worker.Clock += SatoraChainWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal("refund-1", Row.RefundTxId);
        Assert.DoesNotContain(Problems.NothingToRefund, _worker.Sent.Select(n => n.Problem));
    }

    // The address read empty just before the SDK returned a refund from its journal; once recorded, that
    // refund is followed like any other, and failing to follow it is reported.
    [Fact]
    public async Task ARefundReturnedFromTheJournalAfterAnEmptyReadIsReportedWhenFollowingItFails()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(100_000, confirmed: true);
        _wallets.BroadcastThenFail = new HttpRequestException("esplora POST /tx timed out");
        _worker.Clock = Row.BtcRefundLocktime;
        await _worker.TickAsync(Store, Ct);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        Assert.Equal("refund-1", Row.RefundTxId);

        _wallets.RefundError = new HttpRequestException("arkd unreachable");
        for (var check = 0; check < 2; check++)
        {
            _worker.Clock += SatoraChainWorker.RefundCheckInterval;
            await _worker.TickAsync(Store, Ct);
        }

        Assert.Equal([Problems.RefundFailing], _worker.Sent.Select(n => n.Problem));
    }

    // A refund broadcast before the store dismissed its swap still confirms: the invoice records it, and
    // the store's close stands.
    [Fact]
    public async Task ARefundConfirmingAfterTheStoresDismissalKeepsTheStoresClose()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(60_000, confirmed: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        _wallets.Confirmed.Add("refund-1");
        var dismissedAt = _worker.Clock;
        _wallets.DuringConfirmationRead = () => Dismiss(dismissedAt);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;

        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { ClosedReason: SatoraChainRefunds.DismissedReason, RefundTxId: "refund-1" } row && row.CompletedAt == dismissedAt);
        Assert.Equal((InvoiceEventData.EventSeverity.Success, "The refund of Satora on-chain swap swap confirmed in refund-1. The swap had already been dismissed."),
            (_credit.Notes[^1].Severity, _credit.Notes[^1].Message));
    }

    // Any refund call may broadcast, so the store's dismissal is refused while one runs, first or following.
    [Fact]
    public async Task HoldsTheSwapAgainstADismissalWhileARefundCallRuns()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(60_000, confirmed: true);
        var dismissible = new List<bool>();
        _wallets.DuringRefund = () =>
        {
            using var dismissal = _swaps.TryHoldForDismissal("swap");
            dismissible.Add(dismissal is not null);
        };
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([false, false], dismissible);
        using var after = _swaps.TryHoldForDismissal("swap");
        Assert.NotNull(after);
    }

    // The store dismissed the swap after this tick read it: the refund it broadcast is neither rebuilt
    // nor followed by a sweep of what the store chose to leave.
    [Fact]
    public async Task DoesNotFollowTheRefundOfASwapTheStoreDismissedSinceTheTickReadIt()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(60_000, confirmed: true);
        Pay(50_000, confirmed: true, txId: "second");
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        _wallets.Confirmed.Add("refund-1");
        _events.Clear();
        var dismissedAt = _worker.Clock;
        _swaps.AfterListOpen = () => Dismiss(dismissedAt);
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.True(Row is { RefundTxId: "refund-1", ClosedReason: SatoraChainRefunds.DismissedReason } row && row.CompletedAt == dismissedAt);
        Assert.Single(_wallets.Outputs);
    }

    // The receipt's fee covers every input a refund spent; one more output is worth sweeping once it is
    // worth more than one input's refund fee.
    [Fact]
    public async Task AfterARefundConfirmsALeftoverWorthMoreThanOneInputsFeeIsStillSwept()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        Pay(60_000, confirmed: true);
        _wallets.RefundFeeSats = 5_000;
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);

        Pay(3_000, txId: "late");
        _wallets.Confirmed.Add("refund-1");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: null });

        _wallets.Outputs.Clear();
        Pay(3_000, confirmed: true, txId: "late");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-2", CompletedAt: null });
    }

    // An underpayment never reaches the invoice, and the refunds page drops a swap once it is refunded,
    // so the invoice's Events keep the refund on record.
    [Fact]
    public async Task NotesOnTheInvoiceThatItsSwapNeedsARefundAndWhenTheRefundGoesOutAndConfirms()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), refundAddress: Address);
        Pay(16_000, confirmed: true);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        _wallets.Confirmed.Add("refund-1");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: not null });
        Assert.Equal([
            ("invoice-swap", InvoiceEventData.EventSeverity.Warning,
                "Satora on-chain swap swap needs a refund (Underpaid). Refund the bitcoin at htlc-swap to the customer from Satora's On-chain refunds page."),
            ("invoice-swap", InvoiceEventData.EventSeverity.Info, $"The refund of Satora on-chain swap swap to {Address} was broadcast in refund-1."),
            ("invoice-swap", InvoiceEventData.EventSeverity.Success, "The refund of Satora on-chain swap swap confirmed in refund-1. The swap is closed.")
        ], _credit.Notes);
    }

    [Theory]
    [InlineData(0L, "holds nothing")]
    [InlineData(1_000L, "holds less than the network fee to refund it")]
    public async Task NotesOnTheInvoiceASwapThatClosedWithoutARefund(long value, string held)
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true);
        if (value > 0)
            Pay(value, confirmed: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraChainWorker.UneconomicCloseAfter;
        await _worker.TickAsync(Store, Ct);

        Assert.NotNull(Row.ClosedReason);
        Assert.Equal([("invoice-swap", InvoiceEventData.EventSeverity.Info, $"Satora on-chain swap swap closed without a refund because its address {held}.")],
            _credit.Notes);
    }

    [Fact]
    public async Task DoesNotRefundToAnAddressTheOwnerChangedSinceItWasRead()
    {
        Open(new SdkSwapStatus.ClientInvalidFunded(), needsRefund: true, refundAddress: Address);
        _swaps.BeforeStartRefund = () => Row.RefundAddress = "bcrt1pother";
        _worker.Clock = Row.BtcRefundLocktime;

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.True(Row is { RefundAttempted: false, CompletedAt: null });
    }

    [Fact]
    public async Task AlertsOnceWhenStatusesCannotBeReadForHalfAnHour()
    {
        Open(new SdkSwapStatus.ServerFunded());
        _wallets.StatusError = new InvalidOperationException("swap server down");

        await _worker.TickAsync(Store, Ct);
        _worker.Clock = Now + SatoraChainWorker.StatusFailureAlertAfter - TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        _worker.Clock = Now + SatoraChainWorker.StatusFailureAlertAfter;
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.StatusUnavailable, null, Row.ClaimCutoff), (alert.Problem, alert.SwapId, alert.Deadline));
        Assert.Empty(_events);
    }

    // The alert asks the store to pay the customer back, so not while the funding is only recorded, but
    // once the claim is credited, also by a BTCPay restarted since the funding was read, and only once:
    // a restart after the alert went out but before the swap was marked credited credits it again.
    [Fact]
    public async Task AnOverpaymentIsAlertedOnceItsClaimIsCreditedAcrossRestarts()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(150_000);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        Assert.Empty(_credit.Notes);

        var restarted = new TestWorker(_swaps, _wallets, _credit, sentAlerts: _sentAlerts);
        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerFunded();
        _swaps.CreditError = new InvalidOperationException("database unavailable");
        await restarted.TickAsync(Store, Ct);
        Assert.False(Row.Credited);
        _swaps.CreditError = null;
        var again = new TestWorker(_swaps, _wallets, _credit, sentAlerts: _sentAlerts);
        await again.TickAsync(Store, Ct);

        Assert.True(Row.Credited);
        var alert = Assert.Single(restarted.Sent);
        Assert.Equal((Problems.Overpaid, "swap", 50_000L), (alert.Problem, alert.SwapId, alert.ExcessSats));
        Assert.Contains("paid 50000 sats more than Satora on-chain swap swap", Assert.Single(_credit.Notes).Message);
        Assert.Empty(again.Sent);
    }

    // The refund returns all the output paid, the excess with it, so the store must not pay it back too.
    [Fact]
    public async Task AnOverpaidSwapThatIsRefundedNeverAlertsTheOverpayment()
    {
        Open(new SdkSwapStatus.ClientFundingSeen(), refundAddress: Address);
        Pay(150_000);
        await _worker.TickAsync(Store, Ct);
        Assert.Equal(50_000, Row.OverpaidSats);

        _wallets.Outputs.Clear();
        Pay(150_000, confirmed: true);
        _wallets.Statuses["swap"] = new SdkSwapStatus.ClientFundedServerRefunded();
        await _worker.TickAsync(Store, Ct);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        _wallets.Confirmed.Add("refund-1");
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { RefundTxId: "refund-1", CompletedAt: not null, Credited: false });
        Assert.Equal([Problems.NeedsRefund], _worker.Sent.Select(n => n.Problem));
        Assert.DoesNotContain(_credit.Notes, n => n.Message.Contains("more than", StringComparison.Ordinal));
    }

    // A replacement can pay the address another amount, and the claim takes the funding the last read
    // found.
    [Fact]
    public async Task AnOverpaymentReplacedByAnExactPaymentBeforeTheClaimIsNeverAlerted()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(150_000);
        await _worker.TickAsync(Store, Ct);
        _wallets.Outputs.Clear();
        Pay(100_000, txId: "replacement");
        await _worker.TickAsync(Store, Ct);

        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerFunded();
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { Credited: true, OverpaidSats: 0 });
        Assert.Empty(_worker.Sent);
        Assert.Empty(_credit.Notes);
    }

    // A replacement mined without ever reaching the mempool the worker reads, as one sent straight to a
    // pool. The funding is not read while it waits for the server once confirmed, so the claim reads it.
    [Theory]
    [InlineData(100_000L, 0L)]
    [InlineData(120_000L, 20_000L)]
    public async Task AnOverpaymentReplacedByAnOutputMinedUnseenIsAlertedAsTheClaimedOutputPays(long replacement, long excess)
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(150_000);
        await _worker.TickAsync(Store, Ct);
        _wallets.Outputs.Clear();
        Pay(replacement, confirmed: true, txId: "replacement");
        _wallets.Statuses["swap"] = new SdkSwapStatus.ClientFunded();
        await _worker.TickAsync(Store, Ct);
        Assert.Equal(50_000, Row.OverpaidSats);

        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerFunded();
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { Credited: true });
        Assert.Equal(excess > 0 ? [(Problems.Overpaid, excess)] : [], _worker.Sent.Select(n => (n.Problem, n.ExcessSats ?? 0)));
        Assert.Equal(excess > 0 ? 1 : 0, _credit.Notes.Count);
    }

    // Its excess goes with an output that left the address, so a claim that cannot read the next
    // funding does not alert it.
    [Fact]
    public async Task TheExcessOfAnOutputThatLeftTheAddressIsNotAlertedForTheNextFunding()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(150_000);
        await _worker.TickAsync(Store, Ct);
        _wallets.Outputs.Clear();
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        Assert.False(Row.PaymentRecorded);

        Pay(100_000, confirmed: true, txId: "next");
        _wallets.ReadError["htlc-swap"] = new HttpRequestException("esplora down");
        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerFunded();
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row.Credited);
        Assert.Empty(_worker.Sent);
        Assert.Empty(_credit.Notes);
    }

    // As at the address of a swap a renewal replaced. Not while the funding is only recorded, as it can
    // still be replaced or the swap refunded, but once the claim settled it, and only once: a restart
    // after the alert went out but before the swap was marked credited credits it again, silently.
    [Fact]
    public async Task APaymentSettledAfterTheInvoiceWasPaidInFullIsAlertedOnceItsClaimIsCredited()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(100_000, confirmed: true);
        _credit.SettledAfterPaidInFull = true;

        await _worker.TickAsync(Store, Ct);
        Assert.True(Row.PaymentRecorded);
        Assert.Empty(_worker.Sent);
        Assert.Empty(_credit.Notes);
        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerFunded();
        _swaps.CreditError = new InvalidOperationException("database unavailable");
        await _worker.TickAsync(Store, Ct);
        Assert.False(Row.Credited);
        _swaps.CreditError = null;
        var restarted = new TestWorker(_swaps, _wallets, _credit, sentAlerts: _sentAlerts);
        await restarted.TickAsync(Store, Ct);

        Assert.True(Row.Credited);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.PaidAfterPaidInFull, "swap", "invoice-swap", 99_500L), (alert.Problem, alert.SwapId, alert.InvoiceId, alert.ExcessSats));
        Assert.Contains("swap swap of invoice invoice-swap was claimed into the Satora wallet after the invoice's other payments had paid it in full",
            SatoraChainNotification.Body(alert, _worker.Clock));
        Assert.Contains("was claimed after the invoice's other payments had paid it in full", Assert.Single(_credit.Notes).Message);
        Assert.Empty(restarted.Sent);
    }

    // The swap is never decided again once marked credited, so an alert that could not be recorded
    // holds the credit back until it can be.
    [Fact]
    public async Task AnAlertThatCannotBeRecordedHoldsTheCreditBackUntilItCan()
    {
        Open(new SdkSwapStatus.ServerFunded(), paymentRecorded: true);
        _credit.SettledAfterPaidInFull = true;
        _sentAlerts.AddError = new InvalidOperationException("database unavailable");

        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { ClaimTxId: "ark-swap", Credited: false });
        Assert.Empty(_worker.Sent);
        _sentAlerts.AddError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.True(Row.Credited);
        Assert.Equal([Problems.PaidAfterPaidInFull], _worker.Sent.Select(n => n.Problem));
        Assert.Single(_credit.Notes);
    }

    [Fact]
    public async Task AnOverpaymentIsNotedOnTheInvoiceOnce()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(150_000);
        await _worker.TickAsync(Store, Ct);

        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerFunded();
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        var note = Assert.Single(_credit.Notes);
        Assert.Equal(InvoiceEventData.EventSeverity.Warning, note.Severity);
        Assert.Contains("paid 50000 sats more than Satora on-chain swap swap", note.Message);
    }

    // The registry has logged the locked wallet once already, as an error.
    [Fact]
    public async Task LogsStatusesALockedWalletCannotReadAtDebugWithoutAStackTrace()
    {
        var logs = new RecordingLogger<SatoraChainWorker>();
        var worker = new TestWorker(_swaps, _wallets, _credit, logs);
        Open(new SdkSwapStatus.ServerFunded());
        _wallets.StatusError = new SatoraWalletUnavailableException("The store's Satora wallet is not usable (Locked).");

        await worker.TickAsync(Store, Ct);

        Assert.Equal((LogLevel.Debug, "Could not read the Satora on-chain swaps of store store; retrying (The store's Satora wallet is not usable (Locked).)"),
            Assert.Single(logs.Entries));
        Assert.Null(Assert.Single(logs.Errors));
    }

    // The store hears once per outage, also when BTCPay restarts during it.
    [Fact]
    public async Task AStatusOutageIsAlertedOnceAcrossARestartAndANewOneAgain()
    {
        Open(new SdkSwapStatus.ServerFunded());
        _wallets.StatusError = new InvalidOperationException("swap server down");
        await _worker.TickAsync(Store, Ct);
        _worker.Clock = Now + SatoraChainWorker.StatusFailureAlertAfter;
        await _worker.TickAsync(Store, Ct);

        var restarted = new TestWorker(_swaps, _wallets, _credit, sentAlerts: _sentAlerts) { Clock = _worker.Clock };
        await restarted.TickAsync(Store, Ct);
        restarted.Clock += SatoraChainWorker.StatusFailureAlertAfter;
        await restarted.TickAsync(Store, Ct);
        Assert.Empty(restarted.Sent);
        _wallets.StatusError = null;
        await restarted.TickAsync(Store, Ct);
        _wallets.StatusError = new InvalidOperationException("swap server down again");
        await restarted.TickAsync(Store, Ct);
        restarted.Clock += SatoraChainWorker.StatusFailureAlertAfter;
        await restarted.TickAsync(Store, Ct);

        Assert.Equal([Problems.StatusUnavailable], _worker.Sent.Select(n => n.Problem));
        Assert.Equal([Problems.StatusUnavailable], restarted.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task ASwapTheServerDoesNotKnowIsOfferedForARefundOnceItCouldNoLongerBeFunded()
    {
        Open(null);
        _wallets.NotFound.Add("swap");
        Pay(100_000);

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events);
        _worker.Clock = Row.VhtlcRefundLocktime;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "deactivate swap"], _events);
        Assert.Equal(SatoraChainActions.LeftoverReason, Row.NeedsRefundReason);
        Assert.Equal([Problems.UnknownToServer, Problems.LeftoverOutputs], _worker.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task AFailingSwapDoesNotHoldUpTheNextOne()
    {
        Open(new SdkSwapStatus.ClientFundingSeen(), swapId: "failing");
        Open(new SdkSwapStatus.ClientFundingSeen(), swapId: "swap");
        _wallets.ReadError["htlc-failing"] = new HttpRequestException("esplora down");
        Pay(100_000);

        await _worker.TickAsync(Store, Ct);

        Assert.Contains("record swap Processing", _events);
        Assert.False(_swaps.Rows["failing"].PaymentRecorded);
    }

    // Replaced by a fee bump to another address, or dropped: twice in a row, as one esplora answer can lag.
    [Fact]
    public async Task TakesOffAPaymentWhoseMempoolFundingDisappearsAndRecordsItAgainIfItReturns()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(100_000);
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row.PaymentRecorded);

        _wallets.Outputs.Clear();
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row.PaymentRecorded);
        await _worker.TickAsync(Store, Ct);
        Assert.False(Row.PaymentRecorded);

        Pay(100_000);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["record swap Processing", "unaccount swap", "record swap Processing"], _events.Where(e => !e.StartsWith("read", StringComparison.Ordinal)));
        Assert.True(Row.PaymentRecorded);
    }

    // The worker was down while the funding confirmed: the invoice shows it as processing before the claim.
    [Fact]
    public async Task RecordsAFundingItNeverSawBeforeClaimingAndClaimsAlsoWhenTheChainCannotBeRead()
    {
        Open(new SdkSwapStatus.ServerFunded(), swapId: "swap");
        Pay(100_000, confirmed: true);
        Open(new SdkSwapStatus.ServerFunded(), swapId: "blind");
        _wallets.ReadError["htlc-blind"] = new HttpRequestException("esplora down");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["read htlc-swap", "record swap Processing", "claim swap own-address", "record swap Settled",
            "read htlc-blind", "claim blind own-address", "record blind Settled"], _events);
    }

    // Past the cutoff the SDK only finishes a claim it journaled, which a transport error can interrupt,
    // so it is asked again every tick until the VHTLC locktime.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerFunded))]
    [InlineData(typeof(SdkSwapStatus.ClientRedeeming))]
    public async Task KeepsTryingToFinishAJournaledClaimPastTheCutoffAndAlertsOnce(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, paymentRecorded: true);
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd connection reset");
        _worker.Clock = Row.ClaimCutoff;

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        Assert.False(Row.Credited);
        _wallets.ClaimError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim swap own-address", "claim swap own-address", "claim swap own-address", "record swap Settled"], _events);
        Assert.True(Row.Credited);
        Assert.Equal([Problems.NearClaimCutoff], _worker.Sent.Select(n => n.Problem));
    }

    // Past the cutoff the claim is retried every tick, so only its first failure there is a warning.
    [Fact]
    public async Task LogsAClaimThatKeepsFailingPastTheCutoffAsAWarningOnce()
    {
        var logs = new RecordingLogger<SatoraChainWorker>();
        var worker = new TestWorker(_swaps, _wallets, new RecordingCredit(_events), logs);
        Open(new SdkSwapStatus.ServerFunded(), paymentRecorded: true);
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd connection reset");
        worker.Clock = Row.ClaimCutoff - SatoraChainWorker.Interval;
        await worker.TickAsync(Store, Ct);

        worker.Clock = Row.ClaimCutoff;
        for (var tick = 0; tick < 3; tick++)
            await worker.TickAsync(Store, Ct);

        Assert.Equal([LogLevel.Warning, LogLevel.Warning, LogLevel.Debug, LogLevel.Debug],
            logs.Entries.Where(e => e.Message.StartsWith("Claiming", StringComparison.Ordinal)).Select(e => e.Level));
    }

    // From then on the server can take the VHTLC back, and the SDK says whether a claim can still complete.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerFunded))]
    [InlineData(typeof(SdkSwapStatus.ClientRedeeming))]
    public async Task ASwapNoClaimCanCompleteByTheVhtlcLocktimeGoesBackToTheCustomerOnce(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, paymentRecorded: true);
        _worker.Clock = Row.VhtlcRefundLocktime - TimeSpan.FromSeconds(1);
        _wallets.ClaimError = new SdkException(SdkErrorKind.InvalidSwap, "insufficient time to claim");
        await _worker.TickAsync(Store, Ct);
        Assert.Equal(0, _wallets.ClaimPossibleReads);
        _events.Clear();

        _worker.Clock = Row.VhtlcRefundLocktime;
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["unaccount swap", "deactivate swap"], _events);
        Assert.True(Row is { NeedsRefundReason: SatoraChainActions.UnclaimedReason, NeedsRefundAt: not null, ClaimTxId: null, CompletedAt: null });
        Assert.Equal(1, _wallets.ClaimPossibleReads);
        var alert = _worker.Sent.Single(n => n.Problem == Problems.NeedsRefund);
        Assert.Equal((SatoraChainActions.UnclaimedReason, Row.BtcRefundLocktime), (alert.Status, alert.Deadline));
        Assert.Contains("has to be refunded (Not claimed in time)", SatoraChainNotification.Body(alert, _worker.Clock));
        // Waiting for a refund, the swap's status is no longer read.
        Assert.Equal(2, _wallets.StatusReads.Count);
    }

    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerFunded))]
    [InlineData(typeof(SdkSwapStatus.ClientRedeeming))]
    public async Task AClaimThatCanStillCompletePastTheVhtlcLocktimeIsFinishedAndNeverRefunded(Type status)
    {
        Open((SdkSwapStatus)Activator.CreateInstance(status)!, paymentRecorded: true);
        _wallets.ClaimPossible = true;
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd connection reset");
        _worker.Clock = Row.VhtlcRefundLocktime;

        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraChainWorker.Interval;
        await _worker.TickAsync(Store, Ct);
        Assert.True(Row is { NeedsRefundAt: null, Credited: false });
        _wallets.ClaimError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim swap own-address", "claim swap own-address", "claim swap own-address", "record swap Settled"], _events);
        Assert.True(Row is { NeedsRefundAt: null, ClaimTxId: "ark-swap", Credited: true });
        Assert.Equal([Problems.NearClaimCutoff], _worker.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task AClaimQueryThatFailsMovesNothing()
    {
        Open(new SdkSwapStatus.ServerFunded(), paymentRecorded: true);
        _wallets.ClaimPossibleError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        _worker.Clock = Row.VhtlcRefundLocktime;

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events);
        Assert.Empty(_worker.Sent);
        Assert.True(Row is { NeedsRefundAt: null, PaymentRecorded: true });

        _wallets.ClaimPossibleError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["unaccount swap", "deactivate swap"], _events);
        Assert.Equal(SatoraChainActions.UnclaimedReason, Row.NeedsRefundReason);
    }

    // A refund is asked for only from the row as the tick read it: if it changed meanwhile, the next tick decides again.
    [Fact]
    public async Task ASwapThatChangedSinceItWasReadIsNotSentToRefunds()
    {
        Open(new SdkSwapStatus.ServerFunded(), paymentRecorded: true);
        _worker.Clock = Row.VhtlcRefundLocktime;
        _swaps.BeforeNeedsRefund = () => Row.ClaimTxId = "ark-swap";

        await _worker.TickAsync(Store, Ct);

        Assert.True(Row is { NeedsRefundAt: null, NeedsRefundReason: null });
        Assert.Empty(_worker.Sent);
        Assert.Empty(_credit.Notes);
    }

    // The SDK's false is final, but a refund that could meet a landing claim is held all the same.
    [Fact]
    public async Task HoldsTheRefundOfASwapThatWentBackUnclaimedWhileAClaimCanComplete()
    {
        Open(new SdkSwapStatus.ServerFunded(), needsRefund: true, refundAddress: Address);
        Row.NeedsRefundReason = SatoraChainActions.UnclaimedReason;
        Pay(100_000, confirmed: true);
        _wallets.ClaimPossible = true;
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);

        await _worker.TickAsync(Store, Ct);
        _worker.Clock += SatoraChainWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        Assert.DoesNotContain(_events, e => e.StartsWith("refund", StringComparison.Ordinal));
        Assert.True(Row is { RefundAttempted: false, RefundTxId: null, CompletedAt: null });
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.RefundHeld, "swap"), (alert.Problem, alert.SwapId));
        Assert.Contains("contact Satora support at support@satora.io", SatoraChainNotification.Body(alert, _worker.Clock));

        _wallets.ClaimPossible = false;
        _worker.Clock += SatoraChainWorker.Interval;
        await _worker.TickAsync(Store, Ct);

        Assert.Contains($"refund swap {Address}", _events);
        Assert.True(Row is { RefundAttempted: true, RefundTxId: "refund-1" });
        Assert.Equal(3, _wallets.ClaimPossibleReads);
    }

    // Each later refund call may broadcast too: one rebuilt after leaving the mempool, or a sweep.
    [Fact]
    public async Task AsksAgainBeforeEachLaterRefundCallOfASwapThatWentBackUnclaimed()
    {
        Open(new SdkSwapStatus.ServerFunded(), needsRefund: true, refundAddress: Address);
        Row.NeedsRefundReason = SatoraChainActions.UnclaimedReason;
        Pay(100_000, confirmed: true);
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);
        await _worker.TickAsync(Store, Ct);
        Assert.Equal("refund-1", Row.RefundTxId);
        _events.Clear();

        _wallets.ClaimPossible = true;
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events);

        _wallets.ClaimPossible = false;
        _worker.Clock += SatoraChainWorker.RefundCheckInterval;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([$"refund swap {Address}", "confirmed refund-1"], _events);
        Assert.Equal([Problems.RefundHeld], _worker.Sent.Select(n => n.Problem));
    }

    // Only a swap that went back because no claim could complete is asked about.
    [Fact]
    public async Task RefundsAnyOtherSwapWithoutAskingWhetherAClaimCanComplete()
    {
        Open(new SdkSwapStatus.ClientFundedServerRefunded(), needsRefund: true, refundAddress: Address);
        Pay(100_000, confirmed: true);
        _wallets.ClaimPossible = true;
        _worker.Clock = SatoraChainActions.RefundPossibleFrom(Row);

        await _worker.TickAsync(Store, Ct);

        Assert.Contains($"refund swap {Address}", _events);
        Assert.Equal(0, _wallets.ClaimPossibleReads);
    }

    // The server funds the VHTLC with the amount due and keeps the rest.
    [Fact]
    public async Task TellsTheStoreOnceAboutAnOverpayment()
    {
        Open(new SdkSwapStatus.ClientFundingSeen());
        Pay(100_750);
        await _worker.TickAsync(Store, Ct);

        _wallets.Statuses["swap"] = new SdkSwapStatus.ServerFunded();
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.Overpaid, "swap", "invoice-swap", 750L), (alert.Problem, alert.SwapId, alert.InvoiceId, alert.ExcessSats));
        Assert.Contains("750 sats more than Satora on-chain swap swap of invoice invoice-swap asked for", SatoraChainNotification.Body(alert, _worker.Clock));
        Assert.Equal(1, _events.Count(e => e == "record swap Processing"));
    }

    private void Open(SdkSwapStatus? status, string swapId = "swap", bool paymentRecorded = false, string? claimTxId = null,
        bool credited = false, bool needsRefund = false, string? refundAddress = null)
    {
        _swaps.Rows[swapId] = SatoraChainActionsTests.Swap(swapId: swapId, storeId: Store, paymentRecorded: paymentRecorded, claimTxId: claimTxId,
            credited: credited, needsRefund: needsRefund, refundAddress: refundAddress);
        if (status is not null)
            _wallets.Statuses[swapId] = status;
    }

    private void Pay(long value, bool confirmed = false, string swapId = "swap", string txId = "funding") =>
        _wallets.Outputs.Add(("htlc-" + swapId, new SatoraHtlcOutput(txId, 0, value, confirmed)));

    // As the refunds page's Dismiss writes it.
    private void Dismiss(DateTimeOffset at)
    {
        Row.ClosedReason = SatoraChainRefunds.DismissedReason;
        Row.CompletedAt = at;
    }

    private sealed class MemorySwaps() : SatoraChainSwaps(null!)
    {
        public Dictionary<string, SatoraChainSwap> Rows { get; } = [];
        public Action? BeforeStartRefund { get; set; }

        public Action? AfterListOpen { get; set; }

        // Copies, so only what the worker writes through these methods reaches the rows.
        public override Task<List<SatoraChainSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken)
        {
            var open = Rows.Values.Where(s => s.StoreId == storeId && s.CompletedAt == null).Select(Copy).ToList();
            AfterListOpen?.Invoke();
            return Task.FromResult(open);
        }

        public override Task<SatoraChainSwap?> FindAsync(string storeId, string swapId) =>
            Task.FromResult(Rows.TryGetValue(swapId, out var row) && row.StoreId == storeId ? Copy(row) : null);

        private static SatoraChainSwap Copy(SatoraChainSwap row) => JsonSerializer.Deserialize<SatoraChainSwap>(JsonSerializer.Serialize(row))!;

        public override Task SetStatusAsync(string swapId, SatoraSwapStatus status) => Set(swapId, row => row.Status = status);

        public override Task SetPaymentRecordedAsync(string swapId, bool recorded) => Set(swapId, row => row.PaymentRecorded = recorded);

        public override Task<bool> SetOverpaidAsync(string swapId, long overpaidSats)
        {
            var row = Rows[swapId];
            if (row.ClaimTxId is not null)
                return Task.FromResult(false);
            row.OverpaidSats = overpaidSats;
            return Task.FromResult(true);
        }

        public override Task SetClaimedAsync(string swapId, string claimTxId) => Set(swapId, row => row.ClaimTxId = claimTxId);

        public Exception? CreditError { get; set; }

        public override Task SetCreditedAsync(string swapId) =>
            CreditError is not null ? throw CreditError : Set(swapId, row => row.Credited = row.PaymentRecorded = true);

        public Action? BeforeNeedsRefund { get; set; }

        public override Task<bool> SetNeedsRefundAsync(string swapId, string? claimTxId, string reason, DateTimeOffset now)
        {
            BeforeNeedsRefund?.Invoke();
            var row = Rows[swapId];
            if (row.NeedsRefundAt is not null || row.CompletedAt is not null || row.ClaimTxId != claimTxId)
                return Task.FromResult(false);
            row.NeedsRefundAt = now;
            row.NeedsRefundReason = reason;
            return Task.FromResult(true);
        }

        public override Task<bool> SetCompletedAsync(string swapId, DateTimeOffset completedAt) => Close(swapId, row => row.CompletedAt = completedAt);

        public override Task<bool> TryStartRefundAsync(string swapId, string address)
        {
            BeforeStartRefund?.Invoke();
            var row = Rows[swapId];
            if (row.RefundAddress != address || row.RefundTxId is not null || row.CompletedAt is not null)
                return Task.FromResult(false);
            row.RefundAttempted = true;
            return Task.FromResult(true);
        }

        public override Task SetRefundTxIdAsync(string swapId, string refundTxId) => Set(swapId, row => row.RefundTxId = refundTxId);

        public override Task<bool> SetClosedAsync(string swapId, string reason, DateTimeOffset now) => Close(swapId, row =>
        {
            row.ClosedReason = reason;
            row.CompletedAt = now;
        });

        private Task Set(string swapId, Action<SatoraChainSwap> update)
        {
            update(Rows[swapId]);
            return Task.CompletedTask;
        }

        private Task<bool> Close(string swapId, Action<SatoraChainSwap> update)
        {
            if (Rows[swapId].CompletedAt is not null)
                return Task.FromResult(false);
            update(Rows[swapId]);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeWallets(List<string> events) : SatoraChainWallets(null!, null!, null!)
    {
        public Dictionary<string, SdkSwapStatus> Statuses { get; } = [];
        public List<string> NotFound { get; } = [];
        public List<(string Address, SatoraHtlcOutput Output)> Outputs { get; } = [];
        public Dictionary<string, Exception> ReadError { get; } = [];
        public List<List<string>> StatusReads { get; } = [];
        public Exception? StatusError { get; set; }
        public Func<string> Address { get; set; } = () => "own-address";
        public Exception? ClaimError { get; set; }
        public bool ClaimPossible { get; set; }
        public Exception? ClaimPossibleError { get; set; }
        // Kept apart from the events, whose order the other tests pin.
        public int ClaimPossibleReads { get; private set; }
        public Action? DuringClaimPossibleRead { get; set; }
        public Exception? RefundError { get; set; }
        public Action? DuringRefund { get; set; }
        // Thrown once after the refund was built and its output spent, as when the node took it but its
        // answer was lost.
        public Exception? BroadcastThenFail { get; set; }
        public ulong RefundFeeSats { get; set; } = 300;
        // The smallest output the SDK spends at its own fee rate.
        public long SpendsFromSats { get; set; } = 300 + SatoraChainActions.DustSats;
        public HashSet<string> Confirmed { get; } = [];
        // Run while the address, or a refund's confirmation, is being read.
        public Action? DuringRead { get; set; }
        public Action? DuringConfirmationRead { get; set; }
        private (string TxId, ulong Fee)? _refund;
        private string? _refundTo;

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

        public override Task<string> GetClaimAddressAsync(string storeId, CancellationToken cancellationToken) => Task.FromResult(Address());

        public override Task<SdkArkadeSettlement> ClaimAsync(string storeId, string swapId, string destination, CancellationToken cancellationToken)
        {
            events.Add($"claim {swapId} {destination}");
            if (ClaimError is not null)
                throw ClaimError;
            return Task.FromResult<SdkArkadeSettlement>(new SdkArkadeSettlement.Offchain("ark-" + swapId, 99_500));
        }

        public override Task<bool> ClaimPossibleAsync(string storeId, string swapId)
        {
            ClaimPossibleReads++;
            DuringClaimPossibleRead?.Invoke();
            return ClaimPossibleError is not null ? throw ClaimPossibleError : Task.FromResult(ClaimPossible);
        }

        // As RefundBitcoinAsync: a pending refund comes back as it is; with none pending it sweeps one
        // confirmed output it would spend at a time (as if its fee cap fit no more), and with nothing
        // left returns the last.
        public override Task<BitcoinRefundReceipt> RefundAsync(string storeId, string swapId, string destination, CancellationToken cancellationToken)
        {
            events.Add($"refund {swapId} {destination}");
            DuringRefund?.Invoke();
            if (RefundError is not null)
                throw RefundError;
            var left = Outputs.FindIndex(o => o.Address == "htlc-" + swapId && o.Output.Confirmed && o.Output.ValueSats >= SpendsFromSats);
            if ((_refund is { } pending && !Confirmed.Contains(pending.TxId)) || left < 0)
                return _refund is { } last
                    ? Task.FromResult(new BitcoinRefundReceipt(last.TxId, last.Fee))
                    : throw new SdkException(SdkErrorKind.InvalidSwap, "missing confirmed Bitcoin outputs or duplicate outpoint");
            Outputs.RemoveAt(left);
            _refundTo = destination;
            _refund = ($"refund-{(_refund is null ? 1 : int.Parse(_refund.Value.TxId[7..]) + 1)}", RefundFeeSats);
            if (BroadcastThenFail is { } lost)
            {
                BroadcastThenFail = null;
                throw lost;
            }
            return Task.FromResult(new BitcoinRefundReceipt(_refund.Value.TxId, _refund.Value.Fee));
        }

        // The latest refund spends the address's outputs in the mempool until it confirms, and pays the
        // destination it was made for.
        public override Task<bool> HasPossibleRefundAsync(string address, string? refundAddress, CancellationToken cancellationToken)
        {
            events.Add($"mempool {address}");
            return Task.FromResult(_refund is { } refund && (!Confirmed.Contains(refund.TxId) || refundAddress == _refundTo));
        }

        public override Task<bool> IsConfirmedAsync(string txId, CancellationToken cancellationToken)
        {
            events.Add($"confirmed {txId}");
            DuringConfirmationRead?.Invoke();
            return Task.FromResult(Confirmed.Contains(txId));
        }

        public long FeeRateSatVb { get; set; } = 10;

        public override Task<long> EstimateRefundFeeSatsAsync(CancellationToken cancellationToken)
        {
            events.Add("estimate fee");
            return Task.FromResult(FeeRateSatVb * RefundVbytes);
        }

        public override Task<IReadOnlyList<SatoraHtlcOutput>> ListUnspentAsync(string address, CancellationToken cancellationToken)
        {
            events.Add($"read {address}");
            DuringRead?.Invoke();
            if (ReadError.TryGetValue(address, out var error))
                throw error;
            return Task.FromResult<IReadOnlyList<SatoraHtlcOutput>>(Outputs.Where(o => o.Address == address).Select(o => o.Output).ToList());
        }
    }

    private sealed class RecordingCredit(List<string> events)
        : SatoraChainCredit(null!, null!, null!, null!, NullLogger<SatoraChainCredit>.Instance)
    {
        // Whether each swap's payment settled on an invoice its other payments had already paid in full.
        public bool SettledAfterPaidInFull { get; set; }

        public override Task<SatoraCreditOutcome> RecordAsync(SatoraChainSwap swap, PaymentStatus status, DateTimeOffset at)
        {
            events.Add($"record {swap.SwapId} {status}");
            return Task.FromResult(SatoraCreditOutcome.Recorded);
        }

        public override Task<bool> SettledAfterPaidInFullAsync(SatoraChainSwap swap) => Task.FromResult(SettledAfterPaidInFull);

        public override Task UnaccountAsync(SatoraChainSwap swap)
        {
            events.Add($"unaccount {swap.SwapId}");
            return Task.CompletedTask;
        }

        public override Task DeactivatePromptAsync(string invoiceId, string swapId)
        {
            events.Add($"deactivate {swapId}");
            return Task.CompletedTask;
        }

        // Kept apart from the events, whose order the other tests pin.
        public List<(string InvoiceId, InvoiceEventData.EventSeverity Severity, string Message)> Notes { get; } = [];

        public override Task NoteAsync(SatoraChainSwap swap, string message, InvoiceEventData.EventSeverity severity)
        {
            Notes.Add((swap.InvoiceId, severity, message));
            return Task.CompletedTask;
        }
    }

    private sealed class TestWorker(SatoraChainSwaps swaps, SatoraChainWallets wallets, SatoraChainCredit credit, ILogger<SatoraChainWorker>? logger = null,
        SatoraSentAlerts? sentAlerts = null)
        : SatoraChainWorker(swaps, wallets, credit, null!, sentAlerts ?? new MemorySentAlerts(), logger ?? NullLogger<SatoraChainWorker>.Instance)
    {
        public DateTimeOffset Clock { get; set; } = Now;
        public List<SatoraChainNotification> Sent { get; } = [];

        protected override DateTimeOffset UtcNow => Clock;

        protected override Task SendAsync(SatoraChainNotification notification)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }
}
