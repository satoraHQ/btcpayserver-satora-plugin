using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Satora.Sdk;
using Xunit;
using Problems = BTCPayServer.Plugins.Satora.Lightning.SatoraLightningNotification.Problems;
using SdkArkadeSpend = uniffi.satora_sdk_ffi.ArkadeSpend;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;

namespace BTCPayServer.Plugins.Satora.Tests;

// One worker tick per call, over rows kept in memory and an SDK that answers as each test sets it up.
public class SatoraOutgoingWorkerTests
{
    private const string Store = "store";
    // The texts the SDK's FundArkadeSwapAsync refuses with (rust-sdk arkade/funding.rs).
    private const string Unresolved = "an Ark transaction that spent inputs of a previous VHTLC funding has no visible output at the VHTLC; it may be that payment, so retry later and never pay the VHTLC by other means";
    private const string NotAwaiting = "swap is not awaiting funding (status Expired)";
    // The texts the SDK's SettleAsync refuses a refund without the server with (rust-sdk arkade/execution.rs).
    private const string NotMature = "Arkade refund timelock has not matured";
    private const string NothingSpendable = "no spendable Arkade HTLC outputs";

    private readonly OutgoingEvents _events = new();
    private readonly MemoryOutgoingSwaps _swaps;
    private readonly FakeWallet _wallet;
    private readonly MemoryWithdrawals _withdrawals;
    private readonly FakeWalletAccess _wallets;
    private readonly RecordingLogger _log = new();
    private readonly MemorySentAlerts _sentAlerts = new();
    private readonly RecordingRenewals _renewals = new();
    private readonly TestWorker _worker;

    public SatoraOutgoingWorkerTests()
    {
        _swaps = new MemoryOutgoingSwaps(_events);
        _wallet = new FakeWallet(_events);
        _withdrawals = new MemoryWithdrawals(_events, _swaps);
        _wallets = new FakeWalletAccess(_wallet, _events);
        _worker = new TestWorker(_swaps, _wallets, _renewals, _withdrawals, _log, _sentAlerts);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false, false, SatoraSwapStatus.Pending, SatoraOutgoingAction.Wait)]
    [InlineData(false, false, SatoraSwapStatus.Expired, SatoraOutgoingAction.Close)]
    [InlineData(false, false, SatoraSwapStatus.ServerWontFund, SatoraOutgoingAction.Close)]
    [InlineData(false, false, SatoraSwapStatus.ServerRedeemed, SatoraOutgoingAction.Close)]
    [InlineData(false, false, SatoraSwapStatus.ClientFunded, SatoraOutgoingAction.Close)]
    [InlineData(false, false, SatoraSwapStatus.Unknown, SatoraOutgoingAction.Close)]
    [InlineData(true, false, SatoraSwapStatus.Pending, SatoraOutgoingAction.Fund)]
    [InlineData(true, true, SatoraSwapStatus.Pending, SatoraOutgoingAction.Wait)]
    [InlineData(true, true, SatoraSwapStatus.ClientFundingSeen, SatoraOutgoingAction.Wait)]
    [InlineData(true, true, SatoraSwapStatus.ClientFunded, SatoraOutgoingAction.Wait)]
    [InlineData(true, true, SatoraSwapStatus.ServerFunded, SatoraOutgoingAction.Wait)]
    [InlineData(true, false, SatoraSwapStatus.ServerFunded, SatoraOutgoingAction.Wait)]
    [InlineData(true, true, SatoraSwapStatus.ServerRedeemed, SatoraOutgoingAction.Paid)]
    [InlineData(true, false, SatoraSwapStatus.ServerRedeemed, SatoraOutgoingAction.Paid)]
    [InlineData(true, true, SatoraSwapStatus.ServerWontFund, SatoraOutgoingAction.Refund)]
    [InlineData(true, true, SatoraSwapStatus.Expired, SatoraOutgoingAction.Refund)]
    [InlineData(true, true, SatoraSwapStatus.ClientFundedServerRefunded, SatoraOutgoingAction.Refund)]
    [InlineData(true, true, SatoraSwapStatus.ClientInvalidFunded, SatoraOutgoingAction.Refund)]
    [InlineData(true, true, SatoraSwapStatus.ClientFundedTooLate, SatoraOutgoingAction.Refund)]
    [InlineData(true, false, SatoraSwapStatus.Expired, SatoraOutgoingAction.Refund)]
    [InlineData(true, true, SatoraSwapStatus.ClientRefunded, SatoraOutgoingAction.Alert)]
    [InlineData(true, true, SatoraSwapStatus.ClientRedeemed, SatoraOutgoingAction.Alert)]
    [InlineData(true, true, SatoraSwapStatus.Unknown, SatoraOutgoingAction.Alert)]
    public void DecidesEachStatus(bool fundingAttempted, bool funded, SatoraSwapStatus status, SatoraOutgoingAction expected)
    {
        var swap = Row("swap", fundingAttempted, funded ? "ark-swap" : null);

        Assert.Equal(expected, SatoraOutgoingActions.Decide(swap, status));
    }

    // The wallet refunded alone earlier; the server reports the refund as its own.
    [Fact]
    public void SettlesAClientRefundedSwapAsRefundedOnlyIfTheWalletRefundedIt()
    {
        var swap = Row("swap", true, "ark-swap");
        Assert.Equal(SatoraOutgoingAction.Alert, SatoraOutgoingActions.Decide(swap, SatoraSwapStatus.ClientRefunded));

        swap.RefundTxId = "refund-alone";
        Assert.Equal(SatoraOutgoingAction.Refund, SatoraOutgoingActions.Decide(swap, SatoraSwapStatus.ClientRefunded));
    }

    [Fact]
    public void RefundsWithoutTheServerOnlyFromTheRefundLocktime()
    {
        var swap = Row("swap", true, "ark-swap");

        Assert.Equal([SdkArkadeSpend.CooperativeRefund], SatoraOutgoingActions.RefundOperations(swap, FakeWallet.Locktime.AddSeconds(-1)));
        Assert.Equal([SdkArkadeSpend.CooperativeRefund, SdkArkadeSpend.Refund], SatoraOutgoingActions.RefundOperations(swap, FakeWallet.Locktime));
    }

    [Fact]
    public void TellsAnUnresolvedFundingFromOneProvenUnpaid()
    {
        Assert.True(SatoraOutgoingActions.IsUnresolvedFunding(new SdkException(SdkErrorKind.UnresolvedFunding, Unresolved)));
        Assert.False(SatoraOutgoingActions.IsUnresolvedFunding(new SdkException(SdkErrorKind.InvalidSwap, Unresolved)));
        Assert.False(SatoraOutgoingActions.IsUnresolvedFunding(new SdkException(SdkErrorKind.InvalidSwap, NotAwaiting)));

        Assert.True(SatoraOutgoingActions.ProvesNothingFunded(new SdkException(SdkErrorKind.InvalidSwap, NotAwaiting)));
        Assert.False(SatoraOutgoingActions.ProvesNothingFunded(new SdkException(SdkErrorKind.UnresolvedFunding, Unresolved)));
        Assert.False(SatoraOutgoingActions.ProvesNothingFunded(new SdkException(SdkErrorKind.InvalidSwap, "insufficient VTXO amount")));
        Assert.False(SatoraOutgoingActions.ProvesNothingFunded(new SdkException(SdkErrorKind.Api, NotAwaiting)));
    }

    [Fact]
    public async Task FundsAnAttemptedSwapAgainInsideTheGate()
    {
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "fund swap (gated)"], _events.All);
        Assert.Equal("ark-swap", _swaps.Rows["swap"].FundingTxId);
        Assert.Null(_swaps.Rows["swap"].CompletedAt);
        Assert.Equal([LogLevel.Information], _log.Levels);
    }

    [Theory]
    [InlineData(SdkErrorKind.Transport)]
    [InlineData(SdkErrorKind.Timeout)]
    [InlineData(SdkErrorKind.Storage)]
    public async Task RetriesAFundingWithAnUnknownOutcomeOnTheNextTick(SdkErrorKind kind)
    {
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        _wallet.FundError = new SdkException(kind, "unreachable");

        await _worker.TickAsync(Store, Ct);
        _wallet.FundError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "fund swap (gated)", "attempted swap (gated)", "fund swap (gated)"], _events.All);
        Assert.Equal("ark-swap", _swaps.Rows["swap"].FundingTxId);
        Assert.Empty(_worker.Sent);
    }

    // A VHTLC locked to a key arkd no longer signs with is refused on every attempt until the server
    // expires the swap; one in a refund state refused before its journal is read, with nothing in its
    // VHTLC to refund, never closes on its own. Neither proves the wallet paid nothing, and only the
    // second has been reported failed to BTCPay.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.Pending), "the VHTLC's server key is not arkd's current signer; create a new swap", "BTCPay is not told the payment failed")]
    [InlineData(typeof(SdkSwapStatus.Expired), "server swap details differ from the stored swap", "BTCPay was told it failed")]
    public async Task AlertsOnceWithTheSdksRefusalWhenAFundingKeepsFailingAndNeverClosesThePayment(Type status, string refusal, string says)
    {
        Open("swap", (SdkSwapStatus)Activator.CreateInstance(status)!, fundingAttempted: true);
        Withdrawal("swap");
        _wallet.FundError = new SdkException(SdkErrorKind.InvalidSwap, refusal);

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(Enumerable.Repeat<string[]>(["attempted swap (gated)", "fund swap (gated)"], 3).SelectMany(e => e), _events.All);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.FundingFailing, refusal, FakeWallet.Locktime), (alert.Problem, alert.Error, alert.ClaimCutoff));
        Assert.Contains($"keeps failing ({refusal})", SatoraLightningNotification.Body(alert, _worker.Now));
        Assert.Contains(says, SatoraLightningNotification.Body(alert, _worker.Now));
        Assert.True(_swaps.Rows["swap"] is { FundingTxId: null, RefundTxId: null, CompletedAt: null });
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Pending });
    }

    // The alert sent while the server awaited funding says the payment is not reported failed, which
    // stops being true once the server gives it up.
    [Fact]
    public async Task AlertsAgainWhenTheServerGivesUpAPaymentWhoseFundingKeepsFailing()
    {
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        _wallet.FundError = new SdkException(SdkErrorKind.InvalidSwap, "the VHTLC's server key is not arkd's current signer; create a new swap");

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        _wallet.Statuses["swap"] = new SdkSwapStatus.Expired();
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal([(Problems.FundingFailing, "Pending"), (Problems.FundingFailing, "Expired")], _worker.Sent.Select(n => (n.Problem, n.Status)));
        Assert.Contains("BTCPay is not told the payment failed", SatoraLightningNotification.Body(_worker.Sent[0], _worker.Now));
        Assert.Contains("BTCPay was told it failed", SatoraLightningNotification.Body(_worker.Sent[1], _worker.Now));
        Assert.True(_swaps.Rows["swap"] is { FundingTxId: null, CompletedAt: null });
    }

    // Both refusals for funds about to expire clear once a renewal rolls them into a new batch, so the
    // worker starts one and retries without counting the attempt as failing.
    [Theory]
    [InlineData(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.ShortOfExpiring)]
    [InlineData(SdkErrorKind.Transport, SatoraWalletRenewalsTests.JournaledInputExpiring)]
    public async Task RenewsFundsAboutToExpireAndRetriesTheFundingWithoutAlerting(SdkErrorKind kind, string refusal)
    {
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        _wallet.FundError = new SdkException(kind, refusal);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        _wallet.FundError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_worker.Sent);
        Assert.Equal([Store, Store], _renewals.Started);
        Assert.Equal("ark-swap", _swaps.Rows["swap"].FundingTxId);
    }

    // A payment undid its refused first attempt after the worker read the swap attempted. BTCPay was
    // told the payment failed and may pay a fresh invoice instead, so the worker never funds this one.
    [Fact]
    public async Task NeverFundsASwapWhoseFirstAttemptAPaymentUndid()
    {
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        _swaps.AfterListOpen = () => _swaps.Rows["swap"].FundingAttempted = false;

        await _worker.TickAsync(Store, Ct);

        Assert.DoesNotContain(_events.All, e => e.StartsWith("fund", StringComparison.Ordinal));
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: false, FundingTxId: null, CompletedAt: null });
    }

    // The store may close the payment while the worker waits for the spend lock behind a withdrawal;
    // the close is decided under that lock, so the funding sees it, and the VHTLC of a payment the
    // server gave up is not refunded after it either.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.Pending))]
    [InlineData(typeof(SdkSwapStatus.Expired))]
    public async Task NeitherFundsNorRefundsASwapTheStoreClosedWhileTheFundingWaitedForTheWallet(Type status)
    {
        Open("swap", (SdkSwapStatus)Activator.CreateInstance(status)!, fundingAttempted: true);
        Withdrawal("swap");
        _wallet.Amounts = new VhtlcAmounts(12_400, 0, 0, []);
        var closedAt = _worker.Now;
        _swaps.BeforeFundingAttempted = () => _withdrawals.CloseSwapAsync(Store, "swap", paid: false, closedAt).Wait();

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "close swap (gated)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { FundingTxId: null, PaidAt: null } row && row.CompletedAt == closedAt);
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
        Assert.Empty(_worker.Sent);
    }

    // The worker decides to fund before the refund locktime and may get the spend lock only after it,
    // as on signet or regtest where the server still awaits funding then; from the locktime on the
    // wallet refunds the swap without the server instead.
    [Fact]
    public async Task DoesNotFundASwapWhoseRefundLocktimePassedWhileTheFundingWaitedForTheWallet()
    {
        _worker.Now = FakeWallet.Locktime.AddSeconds(-1);
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        var release = new TaskCompletionSource<bool>();
        var busy = _wallets.SpendAsync(Store, _ => release.Task, Ct);

        var tick = _worker.TickAsync(Store, Ct);
        _worker.Now = FakeWallet.Locktime;
        release.SetResult(true);
        await busy;
        await tick;

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { FundingTxId: null, CompletedAt: null });
    }

    [Fact]
    public async Task WaitsOnAnUnresolvedFundingAndAlertsOnce()
    {
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        _wallet.FundError = new SdkException(SdkErrorKind.UnresolvedFunding, Unresolved);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "fund swap (gated)", "attempted swap (gated)", "fund swap (gated)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { FundingTxId: null, CompletedAt: null });
        Assert.Equal([(Problems.FundingUnresolved, "swap")], _worker.Sent.Select(n => (n.Problem, n.SwapId)));
    }

    // ServerFunded can last days while the Lightning payment is tried.
    [Fact]
    public async Task WaitsWhileTheServerPaysTheInvoice()
    {
        Open("swap", new SdkSwapStatus.ServerFunded(), fundingAttempted: true, fundingTxId: "ark-swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { Status: SatoraSwapStatus.ServerFunded, CompletedAt: null });
    }

    [Fact]
    public async Task CompletesAPaidSwapAndMarksItsWithdrawalSent()
    {
        Open("swap", new SdkSwapStatus.ServerRedeemed(), fundingAttempted: true, fundingTxId: "ark-swap");
        Withdrawal("swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { Status: SatoraSwapStatus.ServerRedeemed, PaidAt: not null, CompletedAt: not null, RefundTxId: null });
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Sent, TxId: "ark-swap" });
    }

    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerWontFund))]
    [InlineData(typeof(SdkSwapStatus.Expired))]
    [InlineData(typeof(SdkSwapStatus.ClientFundedServerRefunded))]
    [InlineData(typeof(SdkSwapStatus.ClientInvalidFunded))]
    [InlineData(typeof(SdkSwapStatus.ClientFundedTooLate))]
    public async Task RefundsAFailedPaymentCooperativelyIntoTheWallet(Type status)
    {
        Open("swap", (SdkSwapStatus)Activator.CreateInstance(status)!, fundingAttempted: true, fundingTxId: "ark-swap");
        Withdrawal("swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["refund swap CooperativeRefund (in use)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: "refund-swap", CompletedAt: not null });
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
        Assert.Empty(_worker.Sent);
    }

    [Fact]
    public async Task KeepsTryingTheCooperativeRefundBeforeTheLocktimeAndAlertsOnTheSecondFailure()
    {
        _worker.Now = FakeWallet.Locktime.AddDays(-1);
        Open("swap", new SdkSwapStatus.ServerWontFund(), fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.RefundErrors[SdkArkadeSpend.CooperativeRefund] = new SdkException(SdkErrorKind.Api, "server unavailable", 503);

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(Enumerable.Repeat("refund swap CooperativeRefund (in use)", 3), _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: null, CompletedAt: null });
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.RefundFailing, FakeWallet.Locktime), (alert.Problem, alert.ClaimCutoff));
    }

    // Past the locktime the refund no longer needs the server; the SDK refuses it until median time
    // past reaches the locktime, so a failure is retried.
    [Fact]
    public async Task RefundsWithoutTheServerOncePastTheLocktimeAndRetriesUntilItGoesThrough()
    {
        _worker.Now = FakeWallet.Locktime.AddMinutes(10);
        Open("swap", new SdkSwapStatus.ServerWontFund(), fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.RefundErrors[SdkArkadeSpend.CooperativeRefund] = new SdkException(SdkErrorKind.Api, "server unavailable", 503);
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.InvalidSwap, NotMature);

        await _worker.TickAsync(Store, Ct);
        _wallet.RefundErrors.Remove(SdkArkadeSpend.Refund);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["refund swap CooperativeRefund (in use)", "refund swap Refund (in use)", "refund swap CooperativeRefund (in use)", "refund swap Refund (in use)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: "refund-swap", CompletedAt: not null });
    }

    // As a refund on a client BTCPay disposed while the worker stopped: neither counted nor followed
    // by the next way to refund.
    [Fact]
    public async Task ARefundFailingAsTheWorkerStopsIsNotCountedAsFailing()
    {
        _worker.Now = FakeWallet.Locktime.AddMinutes(10);
        Open("swap", new SdkSwapStatus.ServerWontFund(), fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.RefundErrors[SdkArkadeSpend.CooperativeRefund] = new SdkException(SdkErrorKind.Api, "server unavailable", 503);
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.InvalidSwap, NothingSpendable);
        await _worker.TickAsync(Store, Ct);
        _events.All.Clear();
        using var stopping = new CancellationTokenSource();
        _wallet.DuringRefund = stopping.Cancel;
        _wallet.RefundErrors[SdkArkadeSpend.CooperativeRefund] = new ObjectDisposedException("client");

        await Assert.ThrowsAsync<ObjectDisposedException>(() => _worker.TickAsync(Store, stopping.Token));

        Assert.Equal(["refund swap CooperativeRefund (in use)"], _events.All);
        Assert.Empty(_worker.Sent);
    }

    // A funding is a native call that nothing can interrupt, and BTCPay disposes the wallets once
    // every hosted service has stopped.
    [Fact]
    public async Task StoppingWaitsForAFundingInFlight()
    {
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        var finish = new TaskCompletionSource();
        _wallet.FundGate = finish.Task;
        await _worker.StartAsync(Ct);
        await _wallet.FundStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var stopped = _worker.StopAsync(Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
        Assert.False(stopped.IsCompleted);
        finish.SetResult();

        await stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.Equal("ark-swap", _swaps.Rows["swap"].FundingTxId);
    }

    // Refunding an empty VHTLC only fails: the funding's outcome is read first.
    [Fact]
    public async Task ClosesWithoutRefundingWhenTheJournalProvesNothingWasFunded()
    {
        Open("swap", new SdkSwapStatus.Expired(), fundingAttempted: true);
        Withdrawal("swap");
        _wallet.FundError = new SdkException(SdkErrorKind.InvalidSwap, NotAwaiting);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "fund swap (gated)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: null, CompletedAt: not null });
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
    }

    [Fact]
    public async Task RefundsOnceTheJournalShowsTheFundingWentOut()
    {
        Open("swap", new SdkSwapStatus.Expired(), fundingAttempted: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "fund swap (gated)", "refund swap CooperativeRefund (in use)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { FundingTxId: "ark-swap", RefundTxId: "refund-swap", CompletedAt: not null });
    }

    // An unresolved funding may still land in an empty VHTLC, and refunding one only fails.
    [Theory]
    [InlineData(Unresolved, true, false)]
    [InlineData(Unresolved, true, true)]
    [InlineData("connection reset", false, false)]
    public async Task NeitherRefundsNorClosesWhileTheFundingIsNotResolvedAndTheVhtlcHoldsNothing(string message, bool unresolved, bool unreadable)
    {
        Open("swap", new SdkSwapStatus.Expired(), fundingAttempted: true);
        _wallet.FundError = new SdkException(unresolved ? SdkErrorKind.UnresolvedFunding : SdkErrorKind.Transport, message);
        if (unreadable)
            _wallet.AmountsError = new SdkException(SdkErrorKind.Transport, "indexer unreachable");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "fund swap (gated)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: null, CompletedAt: null });
        Assert.Equal(unresolved ? [Problems.FundingUnresolved] : [], _worker.Sent.Select(n => n.Problem));
    }

    // Some unresolved fundings never resolve on their own, such as an input arkd reports spent
    // without naming the spender, and some refusals come before the journal is read on every attempt;
    // a refund only returns the VHTLC to the wallet.
    [Theory]
    [InlineData(SdkErrorKind.UnresolvedFunding, Unresolved, 12_400UL, 0UL)]
    [InlineData(SdkErrorKind.UnresolvedFunding, Unresolved, 0UL, 12_400UL)]
    [InlineData(SdkErrorKind.InvalidSwap, "server swap details differ from the stored swap", 12_400UL, 0UL)]
    [InlineData(SdkErrorKind.Transport, "connection reset", 0UL, 12_400UL)]
    public async Task RefundsAFailedPaymentWhoseFundingIsNotResolvedOnceItsVhtlcHoldsSats(SdkErrorKind kind, string message, ulong spendable,
        ulong recoverable)
    {
        Open("swap", new SdkSwapStatus.Expired(), fundingAttempted: true);
        Withdrawal("swap");
        _wallet.FundError = new SdkException(kind, message);
        _wallet.Amounts = new VhtlcAmounts(spendable, recoverable, 0, []);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["attempted swap (gated)", "fund swap (gated)", "refund swap CooperativeRefund (in use)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { FundingTxId: null, RefundTxId: "refund-swap", CompletedAt: not null });
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
        Assert.Equal(kind == SdkErrorKind.UnresolvedFunding ? [Problems.FundingUnresolved] : [], _worker.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task ClosesAnExpiredSwapThatWasNeverFundedWithoutCallingTheWallet()
    {
        Open("swap", new SdkSwapStatus.Expired(), fundingAttempted: false);
        Withdrawal("swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { CompletedAt: not null, RefundTxId: null });
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
    }

    [Fact]
    public async Task MovesNothingForAStatusItDoesNotHandleAndAlertsOnce()
    {
        Open("swap", new SdkSwapStatus.ClientRefunded(), fundingAttempted: true, fundingTxId: "ark-swap");

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.Null(_swaps.Rows["swap"].CompletedAt);
        Assert.Equal([(Problems.UnexpectedStatus, "ClientRefunded")], _worker.Sent.Select(n => (n.Problem, n.Status)));
    }

    [Fact]
    public async Task MovesNothingForASwapTheServerDoesNotKnowOrWhileStatusesCannotBeRead()
    {
        Open("unknown", null, fundingAttempted: true);
        _wallet.NotFound.Add("unknown");
        await _worker.TickAsync(Store, Ct);
        _wallet.StatusError = new TimeoutException();
        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.Null(_swaps.Rows["unknown"].CompletedAt);
        Assert.Equal([Problems.UnknownToServer], _worker.Sent.Select(n => n.Problem));
    }

    // The registry has logged the locked wallet once already, as an error.
    [Fact]
    public async Task LogsStatusesALockedWalletCannotReadAtDebug()
    {
        Open("swap", new SdkSwapStatus.ServerFunded(), fundingAttempted: true);
        _wallet.StatusError = new SatoraWalletUnavailableException("The store's Satora wallet is not usable (Locked).");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([LogLevel.Debug], _log.Levels);
    }

    [Fact]
    public async Task ASwapTheServerDoesNotKnowIsAlertedOnceAcrossARestart()
    {
        Open("unknown", null, fundingAttempted: true);
        _wallet.NotFound.Add("unknown");
        await _worker.TickAsync(Store, Ct);

        var restarted = new TestWorker(_swaps, _wallets, _renewals, _withdrawals, _log, _sentAlerts);
        await restarted.TickAsync(Store, Ct);

        Assert.Equal([Problems.UnknownToServer], _worker.Sent.Select(n => n.Problem));
        Assert.Empty(restarted.Sent);
    }

    [Fact]
    public async Task ClosesASwapTheServerDoesNotKnowWhoseFundingWasNeverAttempted()
    {
        Open("swap", null, fundingAttempted: false);
        _wallet.NotFound.Add("swap");
        Withdrawal("swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { Status: SatoraSwapStatus.Expired, CompletedAt: not null });
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
        Assert.Empty(_worker.Sent);
    }

    // BTCPay retries a payout whose Pay returned an error with the same invoice.
    [Fact]
    public async Task APayoutRetriedAfterTheServerForgotItsUnattemptedSwapIsRefusedAndNeverFunded()
    {
        Open("swap", null, fundingAttempted: false);
        _swaps.Rows["swap"].PaymentHash = SatoraLightningCreditTests.PaymentHash;
        _wallet.NotFound.Add("swap");
        await _worker.TickAsync(Store, Ct);
        var payments = new SatoraLightningPayments(_swaps, new FakeWalletAccess(_wallet, _events), _renewals, NullLogger<SatoraLightningPayments>.Instance)
        {
            Clock = () => BOLT11PaymentRequest.Parse(SatoraLightningCreditTests.Bolt11, Network.Main).Timestamp.AddHours(1)
        };

        var response = await payments.PayAsync(Store, () => Task.CompletedTask, SatoraLightningCreditTests.Bolt11, null, Network.Main, Ct);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Empty(_events.All);
        Assert.False(_swaps.Rows["swap"].FundingAttempted);
        Assert.Equal(LightningPaymentStatus.Failed, (await payments.GetPaymentAsync(Store, SatoraLightningCreditTests.PaymentHash))!.Status);
    }

    // A payment may mark the swap attempted after the worker read it, and fund it next.
    [Fact]
    public async Task ClosesAnUnattemptedSwapOnlyIfNothingMarkedItAttemptedSinceItWasRead()
    {
        Open("swap", new SdkSwapStatus.ClientFundingSeen(), fundingAttempted: false);
        Withdrawal("swap");
        _swaps.AfterListOpen = () => _swaps.Rows["swap"].FundingAttempted = true;

        await _worker.TickAsync(Store, Ct);

        Assert.Null(_swaps.Rows["swap"].CompletedAt);
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Pending });
    }

    // A completed swap is never read again, so its withdrawal takes the outcome only with the close:
    // a close that fails leaves both to the next tick.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerRedeemed), "ark-swap", SatoraWithdrawalStatus.Sent)]
    [InlineData(typeof(SdkSwapStatus.Expired), "ark-swap", SatoraWithdrawalStatus.Failed)]
    [InlineData(typeof(SdkSwapStatus.Expired), null, SatoraWithdrawalStatus.Failed)]
    public async Task ClosesTheSwapAndItsWithdrawalTogether(Type status, string? fundingTxId, SatoraWithdrawalStatus expected)
    {
        Open("swap", (SdkSwapStatus)Activator.CreateInstance(status)!, fundingAttempted: true, fundingTxId);
        Withdrawal("swap");
        _wallet.FundError = new SdkException(SdkErrorKind.InvalidSwap, NotAwaiting);
        _swaps.CompletionError = new InvalidOperationException("connection lost");

        await _worker.TickAsync(Store, Ct);
        Assert.Equal(SatoraWithdrawalStatus.Pending, _withdrawals.Rows["w"].Status);
        Assert.Null(_swaps.Rows["swap"].CompletedAt);

        _swaps.CompletionError = null;
        await _worker.TickAsync(Store, Ct);
        Assert.NotNull(_swaps.Rows["swap"].CompletedAt);
        Assert.Equal(expected, _withdrawals.Rows["w"].Status);
    }

    // The store closed the payment as failed after the worker read it open. The worker's close that
    // follows leaves the store's answer in place; a refund it made is still recorded.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerRedeemed), null)]
    [InlineData(typeof(SdkSwapStatus.Expired), "refund-swap")]
    public async Task NeverOverwritesTheStoresClose(Type status, string? refundTxId)
    {
        Open("swap", (SdkSwapStatus)Activator.CreateInstance(status)!, fundingAttempted: true, fundingTxId: "ark-swap");
        Withdrawal("swap");
        var closedAt = _worker.Now;
        _swaps.AfterListOpen = () => _withdrawals.CloseSwapAsync(Store, "swap", paid: false, closedAt).Wait();
        _worker.Now += TimeSpan.FromMinutes(1);

        await _worker.TickAsync(Store, Ct);

        Assert.True(_swaps.Rows["swap"] is { PaidAt: null } row && row.CompletedAt == closedAt && row.RefundTxId == refundTxId);
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed, TxId: null });
    }

    [Fact]
    public async Task AlertsOnceWhenStatusesStayUnreadableWhileAnAttemptedPaymentIsOpen()
    {
        Open("later", null, fundingAttempted: true);
        _swaps.Rows["later"].ClaimCutoff = FakeWallet.Locktime.AddDays(1);
        Open("swap", null, fundingAttempted: true);
        Open("unattempted", null, fundingAttempted: false);
        _swaps.Rows["unattempted"].ClaimCutoff = FakeWallet.Locktime.AddDays(-2);
        _wallet.StatusError = new TimeoutException();

        await _worker.TickAsync(Store, Ct);
        _worker.Now += SatoraLightningWorker.StatusFailureAlertAfter - TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        _worker.Now += TimeSpan.FromSeconds(1);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.OutgoingStatusUnavailable, null, FakeWallet.Locktime), (alert.Problem, alert.SwapId, alert.ClaimCutoff));
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task DoesNotAlertOnUnreadableStatusesWithoutAnAttemptedPayment()
    {
        Open("unattempted", null, fundingAttempted: false);
        _wallet.StatusError = new TimeoutException();

        await _worker.TickAsync(Store, Ct);
        _worker.Now += TimeSpan.FromHours(1);
        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_worker.Sent);
    }

    // The server may have paid the invoice before it went away, so only a refund state it confirms
    // fails the payment.
    [Fact]
    public async Task RefundsWithoutTheServerOncePastTheLocktimeAndKeepsThePaymentPending()
    {
        _worker.Now = FakeWallet.Locktime.AddMinutes(10);
        Open("swap", null, fundingAttempted: true, fundingTxId: "ark-swap");
        Open("early", null, fundingAttempted: true, fundingTxId: "ark-early");
        _swaps.Rows["early"].ClaimCutoff = FakeWallet.Locktime.AddDays(1);
        Withdrawal("swap");
        _wallet.StatusError = new TimeoutException();

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events.All);
        _worker.Now += SatoraLightningWorker.StatusFailureAlertAfter;
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["refund swap Refund (in use)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: "refund-swap", CompletedAt: null });
        Assert.Equal(LightningPaymentStatus.Pending, SatoraOutgoingActions.ToPayment(_swaps.Rows["swap"]).Status);
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Pending });
        Assert.Equal([(Problems.OutgoingStatusUnavailable, null), (Problems.RefundedWithoutServer, "swap")], _worker.Sent.Select(n => (n.Problem, n.SwapId)));
    }

    // The server may be stuck, or report what the plugin does not handle; the VHTLC is the wallet's
    // to take back once its refund locktime has passed, and a payment it may have paid stays pending.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.ServerFunded), "ark-swap", false)]
    [InlineData(typeof(SdkSwapStatus.ClientFunded), "ark-swap", false)]
    [InlineData(typeof(SdkSwapStatus.Pending), "ark-swap", false)]
    [InlineData(typeof(SdkSwapStatus.Pending), null, false)]
    [InlineData(typeof(SdkSwapStatus.ClientRedeemed), "ark-swap", true)]
    public async Task RefundsWithoutTheServerPastTheLocktimeWhateverItReportsButPaidAndKeepsThePaymentPending(Type status, string? fundingTxId, bool unexpected)
    {
        _worker.Now = FakeWallet.Locktime;
        Open("swap", (SdkSwapStatus)Activator.CreateInstance(status)!, fundingAttempted: true, fundingTxId);
        Withdrawal("swap");

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["refund swap Refund (in use)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: "refund-swap", CompletedAt: null });
        Assert.Equal(LightningPaymentStatus.Pending, SatoraOutgoingActions.ToPayment(_swaps.Rows["swap"]).Status);
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Pending });
        Assert.Equal(unexpected ? [Problems.UnexpectedStatus, Problems.RefundedWithoutServer] : [Problems.RefundedWithoutServer],
            _worker.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task RecordsAPaymentTheServerReportsPaidPastTheLocktimeWithoutRefunding()
    {
        _worker.Now = FakeWallet.Locktime.AddDays(1);
        Open("swap", new SdkSwapStatus.ServerRedeemed(), fundingAttempted: true, fundingTxId: "ark-swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { PaidAt: not null, RefundTxId: null });
    }

    [Fact]
    public async Task SettlesAPaymentRefundedWithoutTheServerOnceTheServerReportsTheRefund()
    {
        _worker.Now = FakeWallet.Locktime.AddDays(1);
        Open("swap", new SdkSwapStatus.ClientRefunded(), fundingAttempted: true, fundingTxId: "ark-swap");
        _swaps.Rows["swap"].RefundTxId = "refund-alone";
        Withdrawal("swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: "refund-alone", CompletedAt: not null });
        Assert.Equal(LightningPaymentStatus.Failed, SatoraOutgoingActions.ToPayment(_swaps.Rows["swap"]).Status);
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
        Assert.Empty(_worker.Sent);
    }

    [Fact]
    public async Task RefundsASwapTheServerDoesNotKnowWithoutItOncePastTheLocktimeAndRetriesAFailure()
    {
        Open("swap", null, fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.NotFound.Add("swap");
        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_events.All);

        _worker.Now = FakeWallet.Locktime;
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.InvalidSwap, NotMature);
        await _worker.TickAsync(Store, Ct);
        Assert.Null(_swaps.Rows["swap"].RefundTxId);
        _wallet.RefundErrors.Clear();
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["refund swap Refund (in use)", "refund swap Refund (in use)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: "refund-swap", CompletedAt: null });
    }

    // Median time past trails the clock by about an hour, often more: the SDK refuses the refund
    // until it passes the locktime, as long as the chain worker allows for the same lag.
    [Fact]
    public async Task ARefundWithoutTheServerRefusedAsNotMatureIsExpectedWithinTheMedianTimeLag()
    {
        _worker.Now = FakeWallet.Locktime.AddMinutes(-1) + SatoraChainActions.MedianTimeLag;
        Open("swap", null, fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.NotFound.Add("swap");
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.InvalidSwap, NotMature);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(Enumerable.Repeat("refund swap Refund (in use)", 3), _events.All);
        Assert.Equal(Enumerable.Repeat(LogLevel.Debug, 3), _log.Levels);
        Assert.Equal([Problems.UnknownToServer], _worker.Sent.Select(n => n.Problem));
    }

    // Past that allowance the chain the SDK reads is stuck or behind; the funds are still in the
    // VHTLC, so the store must not close the payment.
    [Fact]
    public async Task SaysTheFundsAreStillInTheVhtlcWhenTheRefundStaysNotMaturePastTheMedianTimeLag()
    {
        _worker.Now = FakeWallet.Locktime + SatoraChainActions.MedianTimeLag;
        Open("swap", new SdkSwapStatus.ServerFunded(), fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.InvalidSwap, NotMature);
        _wallet.Amounts = new VhtlcAmounts(12_400, 0, 0, []);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.RefundLocktimeNotMature, 12_400L, FakeWallet.Locktime), (alert.Problem, alert.HeldSats, alert.ClaimCutoff));
        var body = SatoraLightningNotification.Body(alert, _worker.Now);
        Assert.Contains("still refused 2 hours after its refund locktime, 2026-04-30 00:00:00Z", body);
        Assert.Contains("still holds 12400 sats", body);
        Assert.DoesNotContain("close the payment", body);
        Assert.Null(_swaps.Rows["swap"].CompletedAt);
    }

    // Nothing ever reached the VHTLC of an attempted payment whose funding the server never saw, so
    // only the store can close it.
    [Fact]
    public async Task AsksTheStoreToCloseAPaymentWhoseRefundWithoutTheServerKeepsFailingOnAnEmptyVhtlc()
    {
        _worker.Now = FakeWallet.Locktime;
        Open("swap", new SdkSwapStatus.Pending(), fundingAttempted: true);
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.InvalidSwap, NothingSpendable);

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(Enumerable.Repeat("refund swap Refund (in use)", 3), _events.All);
        Assert.Equal(Enumerable.Repeat(LogLevel.Warning, 3), _log.Levels);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.RefundWithoutServerFailing, "swap", 0L), (alert.Problem, alert.SwapId, alert.HeldSats));
        Assert.True(SatoraLightningNotification.ClosedOnWithdrawalsPage(alert.Problem));
        Assert.Contains("The swap holds nothing", SatoraLightningNotification.Body(alert, _worker.Now));
        Assert.Contains("close the payment on the withdrawals page", SatoraLightningNotification.Body(alert, _worker.Now));
    }

    // A refund without the server can fail while the VHTLC holds the funds, as when arkd is out of
    // reach; closing the payment as failed then would leave them there.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TellsTheStoreToLeaveThePaymentOpenUnlessTheVhtlcIsKnownToBeEmpty(bool unreadable)
    {
        _worker.Now = FakeWallet.Locktime;
        Open("swap", null, fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.NotFound.Add("swap");
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        _wallet.Amounts = new VhtlcAmounts(0, 12_400, 0, []);
        if (unreadable)
            _wallet.AmountsError = new SdkException(SdkErrorKind.Transport, "indexer unreachable");

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        var alert = _worker.Sent.Single(n => n.Problem == Problems.RefundWithoutServerFailing);
        Assert.Equal(unreadable ? null : 12_400L, alert.HeldSats);
        var body = SatoraLightningNotification.Body(alert, _worker.Now);
        Assert.Contains(unreadable ? "could not be read, so leave the payment open" : "still holds 12400 sats", body);
        Assert.DoesNotContain("never reached", body);
        Assert.DoesNotContain("close the payment on the withdrawals page", body);
    }

    // A cooperative refund failing once and a refund without the server failing once are not two
    // failures of either.
    [Fact]
    public async Task CountsTheCooperativeRefundsFailuresApartFromThoseWithoutTheServer()
    {
        _worker.Now = FakeWallet.Locktime;
        Open("swap", new SdkSwapStatus.Expired(), fundingAttempted: true, fundingTxId: "ark-swap");
        _wallet.RefundErrors[SdkArkadeSpend.CooperativeRefund] = new SdkException(SdkErrorKind.Api, "server unavailable", 503);
        _wallet.RefundErrors[SdkArkadeSpend.Refund] = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        await _worker.TickAsync(Store, Ct);
        _wallet.Statuses.Remove("swap");
        _wallet.NotFound.Add("swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal([Problems.UnknownToServer], _worker.Sent.Select(n => n.Problem));
    }

    [Theory]
    [InlineData(Problems.RefundWithoutServerFailing, "SatoraWithdraw")]
    [InlineData(Problems.RefundLocktimeNotMature, "SatoraWithdraw")]
    [InlineData(Problems.FundingFailing, "SatoraWithdraw")]
    [InlineData(Problems.RefundedWithoutServer, "SatoraWithdraw")]
    [InlineData(Problems.RefundFailing, "SatoraPlugin")]
    public void LinksAPaymentOnlyTheStoreCanCloseToTheWithdrawalsPage(Problems problem, string controller)
    {
        INotificationHandler handler = new SatoraLightningNotification.Handler(new RouteLinks(), new BTCPayServerOptions { RootPath = "/" });
        var vm = new NotificationViewModel();

        handler.FillViewModel(new SatoraLightningNotification { StoreId = Store, SwapId = "swap", Problem = problem }, vm);

        Assert.Equal($"{controller}/Index/{Store}", vm.ActionLink);
    }

    [Fact]
    public async Task FailsAPaymentRefundedWithoutTheServerOnceTheServerConfirmsARefundState()
    {
        Open("swap", new SdkSwapStatus.Expired(), fundingAttempted: true, fundingTxId: "ark-swap");
        _swaps.Rows["swap"].RefundTxId = "refund-alone";
        Withdrawal("swap");

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events.All);
        Assert.True(_swaps.Rows["swap"] is { RefundTxId: "refund-alone", CompletedAt: not null });
        Assert.Equal(LightningPaymentStatus.Failed, SatoraOutgoingActions.ToPayment(_swaps.Rows["swap"]).Status);
        Assert.True(_withdrawals.Rows["w"] is { Status: SatoraWithdrawalStatus.Failed });
    }

    [Fact]
    public void TheStoreMayCloseOnlyAnAttemptedSwapTheWorkerCannotFinish()
    {
        var before = FakeWallet.Locktime.AddSeconds(-1);
        var waiting = Row("swap", true, "ark-swap");
        waiting.Status = SatoraSwapStatus.ServerFunded;
        var unexpected = Row("swap", true, "ark-swap");
        unexpected.Status = SatoraSwapStatus.ClientRefunded;
        var closed = Row("swap", true, "ark-swap");
        closed.CompletedAt = before;

        Assert.False(SatoraOutgoingActions.OwnerCanClose(waiting, before));
        Assert.True(SatoraOutgoingActions.OwnerCanClose(waiting, FakeWallet.Locktime));
        Assert.True(SatoraOutgoingActions.OwnerCanClose(unexpected, before));
        Assert.False(SatoraOutgoingActions.OwnerCanClose(Row("swap", false, null), FakeWallet.Locktime));
        Assert.False(SatoraOutgoingActions.OwnerCanClose(closed, FakeWallet.Locktime));
    }

    private void Open(string swapId, SdkSwapStatus? status, bool fundingAttempted, string? fundingTxId = null)
    {
        _swaps.Rows[swapId] = Row(swapId, fundingAttempted, fundingTxId);
        if (status is not null)
            _wallet.Statuses[swapId] = status;
    }

    private void Withdrawal(string swapId) => _withdrawals.Rows["w"] = new SatoraWithdrawal
    {
        Id = "w",
        StoreId = Store,
        Type = SatoraWithdrawalType.LightningAddress,
        Destination = "alice@example.com",
        Status = SatoraWithdrawalStatus.Pending,
        SwapId = swapId
    };

    private static SatoraLightningSwap Row(string swapId, bool fundingAttempted, string? fundingTxId) => new()
    {
        SwapId = swapId,
        StoreId = Store,
        Direction = SatoraLightningDirection.ArkadeToLightning,
        Status = SatoraSwapStatus.Pending,
        PaymentHash = swapId,
        Bolt11 = SatoraLightningCreditTests.Bolt11,
        AmountMsat = 12_345_000,
        FundingSats = 12_400,
        FundingAttempted = fundingAttempted,
        FundingTxId = fundingTxId,
        ClaimCutoff = FakeWallet.Locktime,
        ExpiresAt = FakeWallet.Locktime,
        CreatedAt = FakeWallet.Locktime.AddDays(-20)
    };

    private sealed class TestWorker(SatoraOutgoingSwaps swaps, SatoraWalletAccess wallets, SatoraWalletRenewals renewals, SatoraWithdrawals withdrawals,
        ILogger<SatoraOutgoingWorker> logger, SatoraSentAlerts? sentAlerts = null)
        : SatoraOutgoingWorker(swaps, wallets, renewals, withdrawals, null!, sentAlerts ?? new MemorySentAlerts(), logger)
    {
        public List<SatoraLightningNotification> Sent { get; } = [];

        public DateTimeOffset Now { get; set; } = FakeWallet.Locktime.AddDays(-1);

        protected override DateTimeOffset UtcNow => Now;

        protected override Task SendAsync(SatoraLightningNotification notification)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger : ILogger<SatoraOutgoingWorker>
    {
        public List<LogLevel> Levels { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Levels.Add(logLevel);
    }
}
