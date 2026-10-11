using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Satora.Sdk;
using Xunit;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using Kinds = BTCPayServer.Plugins.Satora.Services.SatoraWalletNotification.Kinds;

namespace BTCPayServer.Plugins.Satora.Tests;

// One worker tick per call, through the real withdrawal service over the fakes its tests use.
public class SatoraWalletAutomationWorkerTests
{
    private const string Store = "store";
    private const string Ark = "ark1qq4hfssprtcgnjzf8qlw2f78yvjau5kldfugg29k34y7j96q2w4t567uy9ukgfl2ntulwyyn8ky0yv26dmjpp6kfp6rcpm6t5wljw2es9qrsghuzz";
    private const string Alice = "alice@example.com";
    // Pays 12,345 sats for 12,400 from the wallet, whatever the wallet offers.
    private static readonly LightningSendQuote Quote = new("1", 1_000_000, 1_000, 10, 0.003, 45, "12400", 12_400, 12_345);
    private const string Refunded = "The Lightning payment failed (Expired); its funds were refunded to the wallet.";
    private static readonly string OnChain = new Key().GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
    // As ark-client 0.11 words it, behind the SDK's prefix.
    private const string SubDust = "settle: Failed to join batch: cannot settle into sub-dust VTXO: 0.00000200 BTC < 0.00000330 BTC";
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(500);

    private readonly OutgoingEvents _events = new();
    private readonly FakeWallet _wallet;
    private readonly MemoryWithdrawals _withdrawals;
    private readonly FakeWalletAccess _wallets;
    private readonly MemoryAutoWithdrawRules _rules = new();
    private readonly RecordingLogger<SatoraWalletAutomationWorker> _log = new();
    private readonly MemorySentAlerts _sentAlerts = new();
    private readonly RecordingLogger<SatoraWalletRenewals> _renewalLog = new();
    private readonly TestWithdrawalService _service;
    private readonly TestWorker _worker;

    public SatoraWalletAutomationWorkerTests()
    {
        var swaps = new MemoryOutgoingSwaps(_events);
        _wallet = new FakeWallet(_events);
        _withdrawals = new MemoryWithdrawals(_events, swaps);
        _wallets = new FakeWalletAccess(_wallet, _events);
        var renewals = new RecordingRenewals();
        _service = new TestWithdrawalService(_events, _wallets, _withdrawals, _rules,
            new SatoraLightningPayments(swaps, _wallets, renewals, NullLogger<SatoraLightningPayments>.Instance), renewals, swaps);
        _worker = new TestWorker(_wallets, new SatoraWalletRenewals(_wallets, _renewalLog), _service, _withdrawals, _rules, _log, _sentAlerts);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int Settles => _events.All.Count(e => e == "settle (gated)");

    [Fact]
    public async Task SettlesRecoverableFundsAsSoonAsItSeesThem()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_000);

        await Tick();
        await Tick();

        Assert.Equal(1, Settles);
        Assert.Equal(new ArkadeBalance(100_000, 0, 0), _wallet.Balance);
        Assert.Empty(_worker.Sent);
    }

    // ark-client refuses a total below dust before it joins a batch; it is worth trying again only
    // once more funds turn recoverable.
    [Fact]
    public async Task LeavesARecoverableTotalBelowDustUntilItChanges()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 200);
        _wallet.SettleError = new SdkException(SdkErrorKind.Transport, SubDust);

        await Tick();
        await Tick();
        Assert.Equal(1, Settles);
        _wallet.Balance = new ArkadeBalance(90_000, 0, 300);
        await Tick();

        Assert.Equal(2, Settles);
        Assert.Empty(_worker.Sent);
    }

    // Left alone, recoverable funds cross expiry while the owner works out the withdrawal.
    [Fact]
    public async Task SettlesWhileAWithdrawalIsUnresolved()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_000);
        _withdrawals.Rows["unknown"] = Withdrawal("unknown", SatoraWithdrawalOrigin.Manual, SatoraWithdrawalStatus.Unknown, _worker.Now);

        await Tick();

        Assert.Equal(1, Settles);
    }

    // Each settle can hold the store's spend lock for minutes, which payouts and withdrawals wait for.
    [Fact]
    public async Task BacksOffAFailingSettleAndTellsTheStoreOncePerRunOfFailures()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_000);
        _wallet.SettleError = new SdkException(SdkErrorKind.Timeout, "batch timed out");

        await TicksAt(0, 1, 5, 6, 20, 21, 80, 81);
        Assert.Equal(4, Settles);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Kinds.SettleFailing, "batch timed out"), (alert.Kind, alert.Error));

        _wallet.SettleError = null;
        await TicksAt(440);
        _wallet.Balance = new ArkadeBalance(90_000, 0, 5_000);
        _wallet.SettleError = new SdkException(SdkErrorKind.Timeout, "batch timed out");
        await TicksAt(441, 445, 446);

        Assert.Equal(7, Settles);
        Assert.Equal([Kinds.SettleFailing, Kinds.SettleFailing], _worker.Sent.Select(n => n.Kind));
    }

    // Funds inside the SDK's expiry margin are left out of every send and turn recoverable only once
    // expired, so each visit renews them, before it settles and withdraws, and logs each renewal.
    [Fact]
    public async Task RenewsFundsAboutToExpireOnEveryVisitBeforeSettling()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_000);
        _wallet.RenewedBatch = "commitment-renew";

        await Tick();
        _wallet.RenewedBatch = null;
        await Tick();

        Assert.Equal(["renew (gated)", "settle (gated)", "renew (gated)"], _events.All);
        var renewed = Assert.Single(_renewalLog.Entries);
        Assert.Equal(LogLevel.Information, renewed.Level);
        Assert.Contains("commitment-renew", renewed.Message, StringComparison.Ordinal);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task DoesNotRenewAWalletWithNothingToSpend()
    {
        _wallet.Balance = new ArkadeBalance(0, 0, 10_000);

        await Tick();

        Assert.Equal(["settle (gated)"], _events.All);
    }

    // As the settle does, it leaves the wallet to a running withdrawal, and the next visit renews.
    [Fact]
    public async Task LeavesTheWalletToARunningWithdrawalAndRenewsOnTheNextVisit()
    {
        var finish = new TaskCompletionSource();
        _wallet.SendGate = finish.Task;
        var withdrawal = _service.ExecuteAsync(Store, new SatoraWithdrawRequest { Destination = Ark, AmountBtc = "0.00001", Key = "key" });
        await _wallet.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // A renewal waiting for the spend lock would hold the visit up until the withdrawal ends.
        await Tick().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.DoesNotContain("renew (gated)", _events.All);
        finish.SetResult();
        await withdrawal;
        await Tick();

        Assert.Single(_events.All, e => e == "renew (gated)");
    }

    // A failed renewal holds up neither the settle nor the withdrawal, and the next visit tries again.
    [Fact]
    public async Task AFailedRenewalIsLoggedAndTheVisitGoesOn()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_000);
        _wallet.RenewError = new SdkException(SdkErrorKind.Transport, "renew: batch failed");

        await Tick();
        await Tick();

        Assert.Equal(["renew (gated)", "settle (gated)", "renew (gated)"], _events.All);
        Assert.Equal([LogLevel.Warning, LogLevel.Warning], _log.Entries.Select(e => e.Level));
        Assert.Empty(_worker.Sent);
    }

    // A run refused for funds about to expire is tried again on the next visit, after a renewal,
    // without the backoff or the alert of a failure.
    [Fact]
    public async Task AWithdrawalRefusedForFundsAboutToExpireIsTriedAgainOnTheNextVisitWithoutAlerting()
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        _wallet.SendError = new SdkException(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.ShortOfExpiring);

        await TicksAt(0, 1);
        _wallet.SendError = null;
        await TicksAt(2);

        Assert.Equal(3, _events.All.Count(e => e.StartsWith("send", StringComparison.Ordinal)));
        Assert.Equal(2, _withdrawals.Rows.Values.Count(w => w is { Status: SatoraWithdrawalStatus.Failed, Error: SatoraWithdrawalService.AutoExpiringFundsMessage }));
        Assert.Single(_withdrawals.Rows.Values, w => w.Status == SatoraWithdrawalStatus.Sent);
        Assert.Empty(_worker.Sent);
    }

    [Fact]
    public async Task WithdrawsOnlyOnceTheWalletReachesTheThreshold()
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 150_000, 10_000);

        await Tick();
        Assert.Empty(_withdrawals.Rows);
        _wallet.Balance = new ArkadeBalance(150_000, 0, 0);
        await Tick();

        Assert.True(Assert.Single(_withdrawals.Rows.Values) is { Origin: SatoraWithdrawalOrigin.Auto, Status: SatoraWithdrawalStatus.Sent, AmountSats: 140_000 });
    }

    [Fact]
    public async Task BacksOffAFailingWithdrawalAndTellsTheStoreOncePerRunOfFailures()
    {
        _rules.Set(Store, SatoraWithdrawalType.Bitcoin, OnChain, 50_000, 0, maxFeeBps: 1);

        await TicksAt(0, 1, 5, 6, 20, 21);

        Assert.Equal(3, _events.All.Count(e => e.StartsWith("offboard quote", StringComparison.Ordinal)));
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Kinds.AutoWithdrawFailing, OnChain), (alert.Kind, alert.Destination));
        Assert.Contains("the fee of 200 sats", alert.Error);
        Assert.Empty(_withdrawals.Rows);
    }

    // Visits are scheduled 5 minutes apart from when each was due, and a visit's reads take time.
    [Fact]
    public async Task CountsTheBackoffFromWhenTheVisitWasDue()
    {
        _rules.Set(Store, SatoraWithdrawalType.Bitcoin, OnChain, 50_000, 0, maxFeeBps: 1);
        _wallet.DuringBalanceRead = () => _worker.Now += TimeSpan.FromSeconds(2);
        await TicksAt(0);
        _wallet.DuringBalanceRead = null;

        await TicksAt(5);

        Assert.Equal(2, _events.All.Count(e => e.StartsWith("offboard quote", StringComparison.Ordinal)));
    }

    // A payee the swap server cannot pay, such as a small wallet asked for too much, would otherwise
    // have every visit fund a new payment and get its funds back.
    [Fact]
    public async Task BacksOffALightningWithdrawalRefundedAfterItWasHandedOver()
    {
        _rules.Set(Store, SatoraWithdrawalType.LightningAddress, Alice, 50_000, 0);
        _wallet.LightningQuote = Quote;
        var created = 0;
        _wallet.Create = _ => FakeWallet.ArkadeToLightning($"swap{++created}");

        await TicksAt(0);
        await RefundAsync("swap1");
        await TicksAt(1, 5);
        Assert.Single(_withdrawals.Rows);
        Assert.Empty(_worker.Sent);

        await TicksAt(6);
        await RefundAsync("swap2");
        await TicksAt(7, 8);

        Assert.Equal(2, _withdrawals.Rows.Count);
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Kinds.AutoWithdrawFailing, Alice, Refunded), (alert.Kind, alert.Destination, alert.Error));
    }

    [Fact]
    public async Task TriesARuleSavedSinceItsLastFailureAtOnce()
    {
        _rules.Set(Store, SatoraWithdrawalType.Bitcoin, OnChain, 50_000, 0, maxFeeBps: 1);
        await TicksAt(0);

        _rules.Set(Store, SatoraWithdrawalType.Bitcoin, OnChain, 50_000, 0).Version = 2;
        await TicksAt(1);

        Assert.True(Assert.Single(_withdrawals.Rows.Values) is { Status: SatoraWithdrawalStatus.Sent });
    }

    // A restart cut the send short, so the SDK reported nothing and no Unknown notification went out.
    [Fact]
    public async Task TellsTheStoreOnceAboutAnInterruptedAutomaticWithdrawal()
    {
        var now = _worker.Now;
        _withdrawals.Rows["interrupted"] = Withdrawal("interrupted", SatoraWithdrawalOrigin.Auto, SatoraWithdrawalStatus.Pending, now.AddHours(-1));
        _withdrawals.Rows["recent"] = Withdrawal("recent", SatoraWithdrawalOrigin.Auto, SatoraWithdrawalStatus.Pending, now.AddMinutes(-10));
        _withdrawals.Rows["manual"] = Withdrawal("manual", SatoraWithdrawalOrigin.Manual, SatoraWithdrawalStatus.Pending, now.AddHours(-1));

        await Tick();
        await Tick();

        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Kinds.AutoWithdrawInterrupted, "tark1interrupted"), (alert.Kind, alert.Destination));
    }

    // The row stays interrupted until the owner marks it, so every BTCPay start would otherwise tell the store again.
    [Fact]
    public async Task TellsTheStoreAboutAnInterruptedAutomaticWithdrawalOnceAcrossARestart()
    {
        _withdrawals.Rows["interrupted"] = Withdrawal("interrupted", SatoraWithdrawalOrigin.Auto, SatoraWithdrawalStatus.Pending, _worker.Now.AddHours(-1));
        await Tick();

        var restarted = new TestWorker(_wallets, new SatoraWalletRenewals(_wallets, _renewalLog), _service, _withdrawals, _rules, _log, _sentAlerts) { Now = _worker.Now };
        await restarted.TickAsync(Store, restarted.Now, Ct);

        Assert.Single(_worker.Sent);
        Assert.Empty(restarted.Sent);
    }

    // The registry logs a locked wallet once, as an error, and tells the store, so the worker adds only
    // a Debug line; an Arkade server that is down is a warning. Either can come back.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SkipsAWalletItCannotOpenAndLogsItOnce(bool locked)
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_000);
        _wallets.OpenError = locked
            ? new SatoraWalletUnavailableException("The store's Satora wallet is not usable (Locked).")
            : new SdkException(SdkErrorKind.Transport, "arkd unreachable");

        await Tick();
        await Tick();

        Assert.Empty(_events.All);
        Assert.Empty(_worker.Sent);
        Assert.Equal(locked ? LogLevel.Debug : LogLevel.Warning, Assert.Single(_log.Entries).Level);
        Assert.Equal(locked, Assert.Single(_log.Errors) is null);
        _wallets.OpenError = null;
        await Tick();
        Assert.Equal(1, Settles);
        Assert.Single(_withdrawals.Rows);
    }

    // A send is a native call that nothing can interrupt, and BTCPay disposes the wallets once every
    // hosted service has stopped.
    [Fact]
    public async Task StoppingWaitsForAnAutomaticSendInFlight()
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        var finish = new TaskCompletionSource();
        _wallet.SendGate = finish.Task;
        await _worker.StartAsync(Ct);
        await _wallet.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var stopped = _worker.StopAsync(Ct);
        await Task.Delay(Blocked, Ct);
        Assert.False(stopped.IsCompleted);
        finish.SetResult();

        await stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        Assert.True(Assert.Single(_withdrawals.Rows.Values) is { Status: SatoraWithdrawalStatus.Sent });
    }

    // A row whose send a restart would cut short holds up every withdrawal until the owner marks it.
    [Fact]
    public async Task StoppingWhileAnAutomaticWithdrawalWaitsForTheSpendLockSendsNothing()
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        var release = new TaskCompletionSource<bool>();
        var payout = _wallets.SpendAsync(Store, _ => release.Task, Ct);
        var balanceRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wallet.DuringBalanceRead = balanceRead.SetResult;
        await _worker.StartAsync(Ct);
        await balanceRead.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        _wallet.DuringBalanceRead = null;
        await Task.Delay(Blocked, Ct);

        await _worker.StopAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.SetResult(true);
        await payout;

        Assert.Empty(_withdrawals.Rows);
        Assert.Empty(_events.All);
    }

    // Each visit reads the clock after listing the stores, which takes longer on some ticks than on
    // others, so the next visit can read a little less than a store interval later.
    [Theory]
    [InlineData(30)]
    [InlineData(300)]
    public async Task VisitsAStoreEveryStoreIntervalWhenATickReadsTheClockEarly(int intervalSeconds)
    {
        var interval = TimeSpan.FromSeconds(intervalSeconds);
        _worker.Every = interval;
        var visits = 0;
        _wallet.DuringBalanceRead = () => visits++;

        await VisitAt(TimeSpan.Zero);
        await VisitAt(interval / 3);
        await VisitAt(interval - TimeSpan.FromMilliseconds(1));
        await VisitAt(interval * 2 - TimeSpan.FromMilliseconds(5));

        Assert.Equal(3, visits);
    }

    // The visit a backoff ends on can read the clock a little early too.
    [Fact]
    public async Task RetriesAFailedSettleAndWithdrawalOnAVisitThatReadsTheClockJustBeforeTheBackoffEnds()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_000);
        _wallet.SettleError = new SdkException(SdkErrorKind.Timeout, "batch timed out");
        _rules.Set(Store, SatoraWithdrawalType.Bitcoin, OnChain, 50_000, 0, maxFeeBps: 1);

        _worker.Now = _worker.Start;
        await Tick();
        _worker.Now += SatoraWalletAutomationWorker.Backoff[0] - TimeSpan.FromMilliseconds(1);
        await Tick();

        Assert.Equal((2, 2), (Settles, _events.All.Count(e => e.StartsWith("offboard quote", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task DoesNothingOnANetworkSatoraDoesNotSupport()
    {
        _worker.NetworkError = "Satora does not support testnet.";

        await _worker.StartAsync(Ct);
        await _worker.StopAsync(Ct);

        Assert.Equal(0, _worker.Listed);
    }

    private Task Tick() => _worker.TickAsync(Store, _worker.Now, Ct);

    // The visits it schedules run on their own tasks, which StopAsync waits for.
    private async Task VisitAt(TimeSpan sinceStart)
    {
        _worker.Now = _worker.Start + sinceStart;
        _worker.ScheduleDueStores([Store], Ct);
        await _worker.StopAsync(Ct);
    }

    private Task RefundAsync(string swapId) => _withdrawals.CloseRefundedSwapAsync(swapId, "refund", Refunded, _worker.Now);

    private async Task TicksAt(params int[] minutes)
    {
        var start = _worker.Start;
        foreach (var minute in minutes)
        {
            _worker.Now = start.AddMinutes(minute);
            await Tick();
        }
    }

    private static SatoraWithdrawal Withdrawal(string id, SatoraWithdrawalOrigin origin, SatoraWithdrawalStatus status, DateTimeOffset createdAt) => new()
    {
        Id = id,
        StoreId = Store,
        Origin = origin,
        Type = SatoraWithdrawalType.Arkade,
        Destination = $"tark1{id}",
        Status = status,
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };

    private sealed class TestWorker(SatoraWalletAccess wallets, SatoraWalletRenewals renewals, SatoraWithdrawalService service, SatoraWithdrawals withdrawals,
        SatoraAutoWithdrawRules rules, ILogger<SatoraWalletAutomationWorker> logger, SatoraSentAlerts? sentAlerts = null)
        : SatoraWalletAutomationWorker(null!, null!, wallets, renewals, service, withdrawals, rules, null!, sentAlerts ?? new MemorySentAlerts(), logger)
    {
        public List<SatoraWalletNotification> Sent { get; } = [];

        public DateTimeOffset Start { get; } = DateTimeOffset.UtcNow;

        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public string? NetworkError { get; set; }

        public int Listed { get; private set; }

        protected override DateTimeOffset UtcNow => Now;

        public TimeSpan Every { get; set; } = MainnetStoreInterval;

        protected override TimeSpan StoreInterval => Every;

        protected override string? Unavailable => NetworkError;

        protected override Task<List<string>> ListStoresAsync(CancellationToken cancellationToken)
        {
            Listed++;
            return Task.FromResult<List<string>>([Store]);
        }

        protected override Task SendAsync(SatoraWalletNotification notification)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }
}
