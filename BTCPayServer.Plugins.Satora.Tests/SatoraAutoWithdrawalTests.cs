using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Satora.Sdk;
using Xunit;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using SdkLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;

namespace BTCPayServer.Plugins.Satora.Tests;

// One automatic run per ExecuteAutoAsync call, over the fakes the manual withdrawal tests use.
public class SatoraAutoWithdrawalTests
{
    private const string Store = "store";
    private const string Ark = "ark1qq4hfssprtcgnjzf8qlw2f78yvjau5kldfugg29k34y7j96q2w4t567uy9ukgfl2ntulwyyn8ky0yv26dmjpp6kfp6rcpm6t5wljw2es9qrsghuzz";
    private const string Alice = "alice@example.com";
    private static readonly string OnChain = new Key().GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
    // Pays 12,345 sats for 12,400 from the wallet, whatever the wallet offers.
    private static readonly LightningSendQuote Quote = new("1", 1_000_000, 1_000, 10, 0.003, 45, "12400", 12_400, 12_345);
    // A mainnet invoice for 1 sat (BTCPay's invoice migration test vectors).
    private const string OneSatBolt11 = "lnbc10n1pjlhc90pp5smwdey0c5skr758lpm0w7tf56cafmdd0hey2nylwfl9q398jgf5shp5qra0y2q5w98at2vv0upux3sn2p0efrxs4h2nyzghqcj7009nqpsqcqzzsxqyz5vqsp5y8fvj3dxnwdaavx89nc5vykuywcmzdefh7x7z62q873hmawh7pws9qyyssq8f7ptu037y2vcclwst6nj8cy8ndhrcxj729ea4ntwdmpgtrwfqun5jnh4zul9s7vvtqd58wurdzk9e3xpzs3ykm0umjdwcttfussaxqqayadfs";

    private readonly OutgoingEvents _events = new();
    private readonly MemoryOutgoingSwaps _swaps;
    private readonly FakeWallet _wallet;
    private readonly MemoryWithdrawals _withdrawals;
    private readonly FakeWalletAccess _wallets;
    private readonly MemoryAutoWithdrawRules _rules = new();
    private readonly RecordingRenewals _renewals = new();
    private readonly TestWithdrawalService _service;

    public SatoraAutoWithdrawalTests()
    {
        _swaps = new MemoryOutgoingSwaps(_events);
        _wallet = new FakeWallet(_events) { Create = _ => FakeWallet.ArkadeToLightning("swap"), LightningQuote = Quote };
        _withdrawals = new MemoryWithdrawals(_events, _swaps);
        _wallets = new FakeWalletAccess(_wallet, _events);
        _service = new TestWithdrawalService(_events, _wallets, _withdrawals, _rules,
            new SatoraLightningPayments(_swaps, _wallets, _renewals, NullLogger<SatoraLightningPayments>.Instance), _renewals, _swaps);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0L, 90_000L)]
    [InlineData(10_000L, 80_000L)]
    public async Task SendsWhatTheWalletCanSpendAboveTheReserveToAnArkadeAddress(long reserveSats, long sentSats)
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, reserveSats);
        _wallet.Balance = new ArkadeBalance(80_000, 10_000, 500);

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal(SatoraAutoWithdrawOutcome.Sent, result.Outcome);
        var row = Assert.Single(_withdrawals.Rows.Values);
        Assert.True(row is { Origin: SatoraWithdrawalOrigin.Auto, Status: SatoraWithdrawalStatus.Sent, Type: SatoraWithdrawalType.Arkade, FeeSats: 0 });
        Assert.Equal(sentSats, row.AmountSats);
        Assert.Matches("^[0-9a-f]{32}$", row.Id);
        Assert.Equal([$"withdrawal {row.Id} (gated)", $"send {Ark} {sentSats} (gated)"], _events.All);
    }

    [Fact]
    public async Task WaitsWhileTheWalletIsBelowTheThresholdOrTheRuleIsOff()
    {
        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, (await _service.ExecuteAutoAsync(Store, Ct)).Outcome);
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 100_001, 0);
        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, (await _service.ExecuteAutoAsync(Store, Ct)).Outcome);
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 100_000, 0).Enabled = false;
        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, (await _service.ExecuteAutoAsync(Store, Ct)).Outcome);

        Assert.Empty(_withdrawals.Rows);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("send", StringComparison.Ordinal));
    }

    // The send-everything quote leaves out VTXOs about to expire and the dust kept as change, so a
    // reserve comes off what it would send, and the exit of that amount is quoted again.
    [Theory]
    [InlineData(0L, null, 50_000L)]
    [InlineData(5_000L, 45_000L, 45_000L)]
    public async Task ExitsEverythingWithoutAReserveAndLeavesTheReserveOtherwise(long reserveSats, long? exitSats, long recordedSats)
    {
        _rules.Set(Store, SatoraWithdrawalType.Bitcoin, OnChain, 50_000, reserveSats);

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal(SatoraAutoWithdrawOutcome.Sent, result.Outcome);
        var row = Assert.Single(_withdrawals.Rows.Values);
        Assert.True(row is { Origin: SatoraWithdrawalOrigin.Auto, Status: SatoraWithdrawalStatus.Sent, TxId: "commitment-offboard", FeeSats: 200 });
        Assert.Equal(recordedSats, row.AmountSats);
        var amount = exitSats?.ToString() ?? "all";
        Assert.Equal(exitSats is null
                ? [$"offboard quote {OnChain} all (gated)", $"withdrawal {row.Id} (gated)", $"offboard {OnChain} all (gated)"]
                : [$"offboard quote {OnChain} all (gated)", $"offboard quote {OnChain} {amount} (gated)", $"withdrawal {row.Id} (gated)", $"offboard {OnChain} {amount} (gated)"],
            _events.All);
    }

    // On regtest the exit's expiry margin is half a VTXO's life, so a quote is refused while VTXOs
    // sit inside it; a later tick finds them settled or the margin past. That wait is expected, and
    // can repeat every tick, so it is logged without a stack trace.
    [Fact]
    public async Task AnExitTheSdkRefusesToQuoteIsTriedAgainLaterAndAnyOtherQuoteFailureCounts()
    {
        var log = new RecordingLogger<SatoraWithdrawalService>();
        var service = new TestWithdrawalService(_events, _wallets, _withdrawals, _rules,
            new SatoraLightningPayments(_swaps, _wallets, _renewals, NullLogger<SatoraLightningPayments>.Instance), _renewals, _swaps, log);
        _rules.Set(Store, SatoraWithdrawalType.Bitcoin, OnChain, 50_000, 5_000);
        _wallet.OffboardQuoteError = new SdkException(SdkErrorKind.InvalidSwap, "spendable balance falls short: 60000 sats are expiring");

        var refused = await service.ExecuteAutoAsync(Store, Ct);
        _wallet.OffboardQuoteError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        var unreachable = await service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, refused.Outcome);
        Assert.Contains("expiring", refused.Message);
        Assert.Equal((SatoraAutoWithdrawOutcome.Failed, "arkd unreachable"), (unreachable.Outcome, unreachable.Message));
        Assert.Empty(_withdrawals.Rows);
        Assert.Equal((LogLevel.Information, "The automatic exit of store store cannot be quoted yet (spendable balance falls short: 60000 sats are expiring); trying again later"),
            log.Entries[0]);
        Assert.Null(log.Errors[0]);
    }

    // Each send, exit and swap funding leaves out funds about to expire. A run refused for them
    // waits for their renewal, as an exit that cannot be quoted yet does, and its row says why.
    [Theory]
    [InlineData(SatoraWithdrawalType.Arkade)]
    [InlineData(SatoraWithdrawalType.Bitcoin)]
    [InlineData(SatoraWithdrawalType.LightningAddress)]
    public async Task ARunRefusedForFundsAboutToExpireWaitsForTheirRenewal(SatoraWithdrawalType type)
    {
        var refusal = new SdkException(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.ShortOfExpiring);
        _rules.Set(Store, type, type switch { SatoraWithdrawalType.Arkade => Ark, SatoraWithdrawalType.Bitcoin => OnChain, _ => Alice }, 50_000, 0);
        if (type == SatoraWithdrawalType.LightningAddress)
            _wallet.FundError = refusal;
        else
            _wallet.SendError = refusal;

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal((SatoraAutoWithdrawOutcome.Waiting, "some of the wallet's funds are about to expire and are being renewed"), (result.Outcome, result.Message));
        Assert.True(Assert.Single(_withdrawals.Rows.Values) is
            { Origin: SatoraWithdrawalOrigin.Auto, Status: SatoraWithdrawalStatus.Failed, Error: SatoraWithdrawalService.AutoExpiringFundsMessage });
        Assert.Equal([Store], _renewals.Started);
    }

    [Theory]
    [InlineData(SatoraWithdrawalType.Bitcoin, "the fee of 200 sats is more than the maximum fee of 5 sats, 0.01% of 50000 sats")]
    [InlineData(SatoraWithdrawalType.LightningAddress, "the fee of 55 sats is more than the maximum fee of 1 sats, 0.01% of 12345 sats")]
    public async Task RefusesAFeeOverTheRulesCapBeforeWritingAnything(SatoraWithdrawalType type, string message)
    {
        _rules.Set(Store, type, type == SatoraWithdrawalType.Bitcoin ? OnChain : Alice, 50_000, 0, maxFeeBps: 1);

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal((SatoraAutoWithdrawOutcome.Failed, message), (result.Outcome, result.Message));
        Assert.Empty(_withdrawals.Rows);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("offboard bc1", StringComparison.Ordinal) || e.StartsWith("create", StringComparison.Ordinal));
    }

    // The swap pays the invoice the payee's endpoint issued, while the run was planned, for what the
    // swap server's amount-only quote says the wallet's sats above the reserve pay.
    [Fact]
    public async Task PaysALightningAddressWhatTheWalletCanSpendAboveTheReserve()
    {
        _rules.Set(Store, SatoraWithdrawalType.LightningAddress, Alice, 50_000, 20_000);

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal(SatoraAutoWithdrawOutcome.Pending, result.Outcome);
        var row = Assert.Single(_withdrawals.Rows.Values);
        Assert.True(row is { Origin: SatoraWithdrawalOrigin.Auto, Status: SatoraWithdrawalStatus.Pending, SwapId: "swap", TxId: "ark-swap", AmountSats: 12_345, FeeSats: 55 });
        Assert.Equal(["send-max quote 80000 (gated)", "lnurl https://example.com/.well-known/lnurlp/alice (gated)", "lnurl invoice 12345 (gated)",
                $"quote {new SdkLightningDestination.Invoice(SatoraLightningCreditTests.Bolt11)} (gated)", $"withdrawal {row.Id} (gated)",
                $"create {new SdkLightningDestination.Invoice(SatoraLightningCreditTests.Bolt11)} (gated)", "add swap (gated)", "attempted swap (gated)", "fund swap (gated)"],
            _events.All);
    }

    // The swap server's swap may take more than it quoted.
    [Fact]
    public async Task NeverFundsALightningPaymentThatWouldEatIntoTheReserve()
    {
        _rules.Set(Store, SatoraWithdrawalType.LightningAddress, Alice, 50_000, 87_600);
        _wallet.Create = _ => FakeWallet.ArkadeToLightning("swap", fundingSats: 12_401);

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal(SatoraAutoWithdrawOutcome.Failed, result.Outcome);
        Assert.Contains("more than the 12400 sats accepted", result.Message);
        Assert.True(Assert.Single(_withdrawals.Rows.Values) is { Status: SatoraWithdrawalStatus.Failed, SwapId: "swap" });
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: false });
    }

    [Fact]
    public async Task PlansNoLightningPaymentWhoseQuoteWouldEatIntoTheReserve()
    {
        _rules.Set(Store, SatoraWithdrawalType.LightningAddress, Alice, 50_000, 90_000);
        _service.InvoiceFor = sats => TestBolt11.Create(sats, Network.Main);

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal(SatoraAutoWithdrawOutcome.Failed, result.Outcome);
        Assert.Contains("more than the 10000 sats the wallet can send", result.Message);
        Assert.Empty(_withdrawals.Rows);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("create", StringComparison.Ordinal));
    }

    // A manual withdrawal of an amount the payee does not take is refused; an automatic one sends
    // what the payee takes.
    [Fact]
    public async Task AsksALightningAddressForNoMoreThanItTakes()
    {
        _rules.Set(Store, SatoraWithdrawalType.LightningAddress, Alice, 50_000, 0, maxFeeBps: 10_000);
        _service.PayRequest.MaxSendable = LightMoney.Satoshis(1);
        _service.Invoice = OneSatBolt11;
        _wallet.LightningQuote = Quote with { NetworkFeeSats = 0, ProtocolFeeSats = 1, SourceAmount = "2", SourceAmountSats = 2, TargetAmountSats = 1 };
        _wallet.Create = _ => FakeWallet.ArkadeToLightning("swap", fundingSats: 2, bolt11: OneSatBolt11);

        var result = await _service.ExecuteAutoAsync(Store, Ct);

        Assert.Equal(SatoraAutoWithdrawOutcome.Pending, result.Outcome);
        Assert.Contains("lnurl invoice 1 (gated)", _events.All);
        Assert.True(Assert.Single(_withdrawals.Rows.Values) is { AmountSats: 1, FeeSats: 1 });
    }

    public static TheoryData<string> Open => ["lightning withdrawal", "unknown withdrawal", "unresolved funding"];

    // A Lightning withdrawal the worker still follows may still pay its VHTLC, and a funding the SDK
    // cannot resolve still counts its inputs as spendable.
    [Theory]
    [MemberData(nameof(Open))]
    public async Task WaitsForEveryOpenWithdrawalAndEveryUnresolvedFunding(string open)
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        switch (open)
        {
            case "lightning withdrawal":
                _withdrawals.Rows["lightning"] = Withdrawal("lightning", SatoraWithdrawalStatus.Pending, swapId: "swap");
                break;
            case "unknown withdrawal":
                _withdrawals.Rows["unknown"] = Withdrawal("unknown", SatoraWithdrawalStatus.Unknown);
                break;
            default:
                var swap = SatoraLightningPayments.ToRow(FakeWallet.ArkadeToLightning("payout"), Store, Network.Main, SatoraLightningCreditTests.PaymentHash, DateTimeOffset.UtcNow);
                swap.FundingAttempted = true;
                _swaps.Rows["payout"] = swap;
                break;
        }

        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, (await _service.ExecuteAutoAsync(Store, Ct)).Outcome);
        Assert.Empty(_events.All);
    }

    // Each send is a native call nothing can interrupt, and a row whose send a restart cut short holds
    // up every withdrawal until the owner marks it.
    [Fact]
    public async Task WritesNoRowOnceBTCPayIsStoppingWhileTheRunIsPlanned()
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        using var stopping = new CancellationTokenSource();
        _wallet.DuringBalanceRead = stopping.Cancel;

        var result = await _service.ExecuteAutoAsync(Store, stopping.Token);

        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, result.Outcome);
        Assert.Empty(_withdrawals.Rows);
        Assert.Empty(_events.All);
    }

    [Fact]
    public async Task WritesNoRowOnceBTCPayIsStoppingWhileTheRunWaitsForTheSpendLock()
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        var release = new TaskCompletionSource<bool>();
        var payout = _wallets.SpendAsync(Store, _ => release.Task, Ct);
        using var stopping = new CancellationTokenSource();

        var run = _service.ExecuteAutoAsync(Store, stopping.Token);
        await stopping.CancelAsync();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        release.SetResult(true);
        await payout;

        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, result.Outcome);
        Assert.Empty(_withdrawals.Rows);
        Assert.Empty(_events.All);
    }

    // The second run waits for the first's spend lock and then finds its row unresolved.
    [Fact]
    public async Task ASendWithAnUnknownOutcomeIsNotifiedAndHoldsUpTheNextRun()
    {
        _rules.Set(Store, SatoraWithdrawalType.Arkade, Ark, 50_000, 0);
        var send = new TaskCompletionSource();
        _wallet.SendGate = send.Task;
        _wallet.SendError = new SdkException(SdkErrorKind.Transport, "connection reset");

        var first = _service.ExecuteAutoAsync(Store, Ct);
        var second = _service.ExecuteAutoAsync(Store, Ct);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        Assert.False(second.IsCompleted);
        send.SetResult();

        Assert.Equal(SatoraAutoWithdrawOutcome.Unknown, (await first).Outcome);
        Assert.Equal(SatoraAutoWithdrawOutcome.Waiting, (await second).Outcome);
        var row = Assert.Single(_withdrawals.Rows.Values);
        Assert.Equal(SatoraWithdrawalStatus.Unknown, row.Status);
        Assert.Equal([row.Id], _service.Notified);
        Assert.Single(_events.All, e => e.StartsWith("send", StringComparison.Ordinal));
    }

    // The wallet is often empty when the rule is set up, and an exit's quote needs the funds it would spend.
    [Fact]
    public async Task SavesARuleOnAnEmptyWalletWithoutQuotingAnExit()
    {
        _wallet.Balance = new ArkadeBalance(0, 0, 0);

        var (saved, _) = await Save(OnChain, "0.001", "0.0002", "0.5");

        Assert.True(saved);
        Assert.True(_rules.Rows[Store] is
            { Enabled: true, DestinationType: SatoraWithdrawalType.Bitcoin, ThresholdSats: 100_000, ReserveSats: 20_000, MaxFeeBps: 50, Version: 1 });
        Assert.Equal(OnChain, _rules.Rows[Store].Destination);
        Assert.Equal([OnChain], _service.DestinationsNotified);
        Assert.Empty(_events.All);
    }

    public static TheoryData<string, string, string?, string, string> Unsendable => new()
    {
        { SatoraLightningCreditTests.Bolt11, "0.001", null, "1", "can be paid only once" },
        { "bitcoin:{0}?amount=0.001", "0.001", null, "1", "without an amount" },
        // An exit sends at least dust, keeps dust as change and pays a flat fee.
        { "{0}", "0.00001", "0.000005", "1", "at least 860 sats above the reserve" },
        { Ark, "0.000003", null, "1", "at least 330 sats above the reserve" },
        { Ark, "0.001", "0.002", "1", "at least 330 sats above the reserve" },
        // The change a send leaves would be below dust, and recoverable rather than spendable.
        { Ark, "0.001", "0.000001", "1", "reserve must be at least 330 sats" },
        { Ark, "", null, "1", "Enter the threshold" },
        { Ark, "0", null, "1", "Enter the threshold" },
        { Ark, "0.001", "0,0005", "1", "Enter the reserve" },
        { Ark, "0.001", null, "10.01", "from 0 to 10, with at most 2 decimals" },
        { Ark, "0.001", null, "0.005", "from 0 to 10, with at most 2 decimals" },
        { Ark, "0.001", null, "1,5", "from 0 to 10, with at most 2 decimals" }
    };

    [Theory]
    [MemberData(nameof(Unsendable))]
    public async Task RefusesARuleThatCouldNeverSend(string destination, string thresholdBtc, string? reserveBtc, string maxFeePercent, string says)
    {
        var (saved, message) = await Save(string.Format(destination, OnChain), thresholdBtc, reserveBtc, maxFeePercent);

        Assert.False(saved);
        Assert.Contains(says, message);
        Assert.Empty(_rules.Rows);
        Assert.Empty(_service.DestinationsNotified);
    }

    // Each run would send the wallet's funds back to itself.
    [Fact]
    public async Task RefusesTheWalletsOwnArkadeAddress()
    {
        _wallet.ArkadeAddress = Ark.ToUpperInvariant();

        var (saved, message) = await Save(Ark, "0.001", null, "1");

        Assert.False(saved);
        Assert.Contains("own Arkade address", message);
    }

    // Neither the payee's invoice nor the swap server's quote depends on the wallet's balance.
    [Fact]
    public async Task ChecksALightningAddressAgainstItsEndpointAndTheSwapServersQuoteForWhatTheThresholdLeaves()
    {
        _wallet.Balance = new ArkadeBalance(0, 0, 0);
        Assert.True((await Save(Alice, "0.001", "0.0002", "1")).Saved);
        Assert.Equal(["send-max quote 80000", "lnurl https://example.com/.well-known/lnurlp/alice", "lnurl invoice 12345",
            $"quote {new SdkLightningDestination.Invoice(SatoraLightningCreditTests.Bolt11)}"], _events.All);

        _wallet.SendMaxQuote = _wallet.SendMaxQuote with { MinAmount = 20_000 };
        var (belowMinimum, message) = await Save(Alice, "0.001", "0.0002", "1", version: 1);
        _wallet.SendMaxQuote = _wallet.SendMaxQuote with { MinAmount = 1_000 };
        _service.PayRequest.MinSendable = LightMoney.Satoshis(20_000);
        var (refusedByPayee, payeeMessage) = await Save(Alice, "0.002", null, "1", version: 1);

        Assert.False(belowMinimum);
        Assert.Contains("minimum of 20000 sats", message);
        Assert.False(refusedByPayee);
        Assert.Contains("takes from 20000 to 1000000 sats, not 12345 sats", payeeMessage);
        Assert.Equal(1, _rules.Rows[Store].Version);
    }

    // A new destination, the first included, could be a thief's; other changes cannot redirect funds.
    [Fact]
    public async Task TellsTheStoreOfEveryNewDestinationAndOfNothingElse()
    {
        Assert.True((await Save(Ark, "0.001", null, "1")).Saved);
        Assert.True((await Save(Ark, "0.002", "0.0005", "2", version: 1)).Saved);
        Assert.True((await Save(OnChain, "0.002", "0.0005", "2", version: 2)).Saved);
        var (stale, message) = await Save(Ark, "0.002", "0.0005", "2", version: 2);
        Assert.True(await _service.DisableAutoWithdrawAsync(Store));
        Assert.False(await _service.DisableAutoWithdrawAsync(Store));
        Assert.True(_rules.Rows[Store] is { Enabled: false, Version: 4 });
        Assert.True((await Save(OnChain, "0.002", "0.0005", "2", version: 4)).Saved);

        Assert.False(stale);
        Assert.Contains("changed since this page was loaded", message);
        Assert.Equal([Ark, OnChain], _service.DestinationsNotified);
        Assert.True(_rules.Rows[Store] is { Enabled: true, Version: 5, ThresholdSats: 200_000, ReserveSats: 50_000, MaxFeeBps: 200 });
    }

    private Task<(bool Saved, string Message)> Save(string destination, string thresholdBtc, string? reserveBtc, string maxFeePercent, int? version = null) =>
        _service.SaveAutoWithdrawAsync(Store, new SatoraAutoWithdrawRequest
        {
            Destination = destination,
            ThresholdBtc = thresholdBtc,
            ReserveBtc = reserveBtc,
            MaxFeePercent = maxFeePercent,
            Version = version
        });

    private static SatoraWithdrawal Withdrawal(string id, SatoraWithdrawalStatus status, string? swapId = null) => new()
    {
        Id = id,
        StoreId = Store,
        Type = SatoraWithdrawalType.Arkade,
        Destination = Ark,
        Status = status,
        SwapId = swapId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}
