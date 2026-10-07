using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging;
using NBitcoin;
using Satora.Sdk;
using Xunit;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using SdkLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraWithdrawalTests
{
    private const string Store = "store";
    private const string Ark = "ark1qq4hfssprtcgnjzf8qlw2f78yvjau5kldfugg29k34y7j96q2w4t567uy9ukgfl2ntulwyyn8ky0yv26dmjpp6kfp6rcpm6t5wljw2es9qrsghuzz";
    private static readonly string OnChain = new Key().GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
    private static readonly LightningSendQuote Quote = new("1", 1_000_000, 1_000, 10, 0.003, 45, "12400", 12_400, 12_345);
    // A mainnet invoice for 1 sat from another payee (BTCPay's invoice migration test vectors).
    private const string OtherBolt11 = "lnbc10n1pjlhc90pp5smwdey0c5skr758lpm0w7tf56cafmdd0hey2nylwfl9q398jgf5shp5qra0y2q5w98at2vv0upux3sn2p0efrxs4h2nyzghqcj7009nqpsqcqzzsxqyz5vqsp5y8fvj3dxnwdaavx89nc5vykuywcmzdefh7x7z62q873hmawh7pws9qyyssq8f7ptu037y2vcclwst6nj8cy8ndhrcxj729ea4ntwdmpgtrwfqun5jnh4zul9s7vvtqd58wurdzk9e3xpzs3ykm0umjdwcttfussaxqqayadfs";
    // As ark-client 0.11 words it, behind the SDK's prefix.
    private const string SubDust = "settle: Failed to join batch: cannot settle into sub-dust VTXO: 0.00000200 BTC < 0.00000330 BTC";

    private readonly OutgoingEvents _events = new();
    private readonly MemoryOutgoingSwaps _swaps;
    private readonly FakeWallet _wallet;
    private readonly MemoryWithdrawals _withdrawals;
    private readonly FakeWalletAccess _wallets;
    private readonly RecordingLogger<SatoraWithdrawalService> _log = new();
    private readonly RecordingLogger<SatoraLightningPayments> _paymentsLog = new();
    private readonly RecordingRenewals _renewals = new();
    private readonly TestWithdrawalService _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public SatoraWithdrawalTests()
    {
        _swaps = new MemoryOutgoingSwaps(_events);
        _wallet = new FakeWallet(_events) { Create = _ => FakeWallet.ArkadeToLightning("swap"), LightningQuote = Quote };
        _withdrawals = new MemoryWithdrawals(_events, _swaps);
        _wallets = new FakeWalletAccess(_wallet, _events);
        _service = new TestWithdrawalService(_events, _wallets, _withdrawals, new MemoryAutoWithdrawRules(),
            new SatoraLightningPayments(_swaps, _wallets, _renewals, _paymentsLog), _renewals, _swaps, _log);
    }

    [Theory]
    [InlineData(Ark, SatoraWithdrawalType.Arkade, null)]
    [InlineData("lightning:" + SatoraLightningCreditTests.Bolt11, SatoraWithdrawalType.LightningInvoice, 12_345L)]
    [InlineData(" " + SatoraLightningCreditTests.Bolt11 + " ", SatoraWithdrawalType.LightningInvoice, 12_345L)]
    [InlineData("LNURL1DP68GURN8GHJ7UM9WFMXJCM99E3K7MF0V9CXJ0M385EKVCENXC6R2C35XVUKXEFCV5MKVV34X5EKZD3EV56NYD3HXQURZEPEXEJXXEPNXSCRVWFNV9NXZCN9XQ6XYEFHVGCXXCMYXYMNSERXFQ5FNS", SatoraWithdrawalType.Lnurl, null)]
    [InlineData("Alice@Example.com", SatoraWithdrawalType.LightningAddress, null)]
    [InlineData("bitcoin:{0}?amount=0.001", SatoraWithdrawalType.Bitcoin, 100_000L)]
    [InlineData("{0}", SatoraWithdrawalType.Bitcoin, null)]
    public void DetectsTheDestinationType(string input, SatoraWithdrawalType type, long? amountSats)
    {
        var target = SatoraWithdrawDestinations.Detect(string.Format(input, OnChain), Network.Main, out var error);

        Assert.Null(error);
        Assert.Equal((type, amountSats), (target!.Type, target.AmountSats));
        if (type == SatoraWithdrawalType.LightningInvoice)
            Assert.Equal((SatoraLightningCreditTests.Bolt11, SatoraLightningCreditTests.PaymentHash), (target.Destination, target.PaymentHash));
        if (type == SatoraWithdrawalType.LightningAddress)
            Assert.Equal("alice@example.com", target.Destination);
        if (type == SatoraWithdrawalType.Bitcoin)
            Assert.Equal(OnChain, target.Destination);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tark1qq4hfssprtcgnjzf8qlw2f78yvjau5kldfugg29k34y7j96q2w4t567uy9ukgfl2ntulwyyn8ky0yv26dmjpp6kfp6rcpm6t5wljw2es9qrsghuzz")]
    [InlineData("alice@localhost")]
    [InlineData("al ice@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("not a destination")]
    public void RefusesWhatItCannotSendToOnMainnet(string input)
    {
        Assert.Null(SatoraWithdrawDestinations.Detect(input, Network.Main, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void ChecksAddressesAndInvoicesAgainstTheStoresNetwork()
    {
        var regtest = new Key().GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString();

        Assert.Equal(SatoraWithdrawalType.Arkade, SatoraWithdrawDestinations.Detect("t" + Ark, Network.RegTest, out _)!.Type);
        Assert.Null(SatoraWithdrawDestinations.Detect(Ark, Network.RegTest, out _));
        Assert.Equal(SatoraWithdrawalType.Bitcoin, SatoraWithdrawDestinations.Detect(regtest, Network.RegTest, out _)!.Type);
        Assert.Null(SatoraWithdrawDestinations.Detect(regtest, Network.Main, out _));
        Assert.Null(SatoraWithdrawDestinations.Detect(SatoraLightningCreditTests.Bolt11, Network.RegTest, out _));
    }

    [Theory]
    // An invoice fixes the amount.
    [InlineData(SatoraWithdrawalType.LightningInvoice, 12_345L, null, false, 12_345L, false)]
    [InlineData(SatoraWithdrawalType.LightningInvoice, 12_345L, "0.00012345", false, 12_345L, false)]
    [InlineData(SatoraWithdrawalType.LightningInvoice, 12_345L, "0.0001", false, null, true)]
    [InlineData(SatoraWithdrawalType.LightningInvoice, 12_345L, null, true, null, true)]
    [InlineData(SatoraWithdrawalType.LightningInvoice, null, "0.0001", false, null, true)]
    // A BIP21 amount is used unless another one is entered.
    [InlineData(SatoraWithdrawalType.Bitcoin, 100_000L, null, false, 100_000L, false)]
    [InlineData(SatoraWithdrawalType.Bitcoin, 100_000L, "0.002", false, null, true)]
    [InlineData(SatoraWithdrawalType.Bitcoin, null, null, true, null, false)]
    [InlineData(SatoraWithdrawalType.LightningAddress, null, "0.0005", false, 50_000L, false)]
    [InlineData(SatoraWithdrawalType.LightningAddress, null, null, true, null, false)]
    [InlineData(SatoraWithdrawalType.LightningAddress, null, "0.0005", true, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, null, false, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "0", false, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "0.000000001", false, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, " 0.001 ", false, 100_000L, false)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "", true, null, false)]
    // A comma is refused rather than read as a group separator (1 BTC) or a decimal point.
    [InlineData(SatoraWithdrawalType.Arkade, null, "0,001", false, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "0,5", false, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "-0.001", false, null, true)]
    // Refused before converting to sats, which would overflow a decimal or a long.
    [InlineData(SatoraWithdrawalType.Arkade, null, "21000000", false, 2_100_000_000_000_000L, false)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "21000000.00000001", false, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "92233720368.54775808", false, null, true)]
    [InlineData(SatoraWithdrawalType.Arkade, null, "79228162514264337593543950335", false, null, true)]
    public void DecidesTheAmount(SatoraWithdrawalType type, long? fixedSats, string? typedBtc, bool sendAll, long? expectedSats, bool refused)
    {
        var target = new SatoraWithdrawTarget(type, "destination", fixedSats);

        var error = SatoraWithdrawDestinations.Amount(target, typedBtc, sendAll, out var sats);

        Assert.Equal(refused, error is not null);
        Assert.Equal(expectedSats, sats);
    }

    [Theory]
    [InlineData(SdkErrorKind.Api, true)]
    [InlineData(SdkErrorKind.InvalidSwap, true)]
    [InlineData(SdkErrorKind.Decode, true)]
    [InlineData(SdkErrorKind.Transport, false)]
    [InlineData(SdkErrorKind.Timeout, false)]
    [InlineData(SdkErrorKind.Storage, false)]
    [InlineData(SdkErrorKind.Internal, false)]
    [InlineData(SdkErrorKind.Cancelled, false)]
    public void OnlyErrorsRaisedBeforeASendProveNothingLeft(SdkErrorKind kind, bool sentNothing)
    {
        Assert.Equal(sentNothing, SatoraWithdrawalService.SentNothing(new SdkException(kind, "error")));
        Assert.False(SatoraWithdrawalService.SentNothing(new InvalidOperationException()));
    }

    [Fact]
    public void OnlyTheOwnerResolvesAWithdrawalAndOnlyOnceNoRequestCanStillRunIt()
    {
        var now = DateTimeOffset.UtcNow;
        var unknown = Row("unknown", SatoraWithdrawalStatus.Unknown, now);
        var recent = Row("recent", SatoraWithdrawalStatus.Pending, now - SatoraWithdrawals.InterruptedAfter + TimeSpan.FromSeconds(1));
        var interrupted = Row("interrupted", SatoraWithdrawalStatus.Pending, now - SatoraWithdrawals.InterruptedAfter);
        var lightning = Row("lightning", SatoraWithdrawalStatus.Pending, now.AddHours(-1));
        lightning.SwapId = "swap";
        var rows = new[] { unknown, recent, interrupted, lightning };

        Assert.Equal([true, true, true, false], rows.Select(SatoraWithdrawals.IsUnresolved));
        Assert.Equal([true, false, true, false], rows.Select(w => SatoraWithdrawals.OwnerCanResolve(w, running: false, now)));
        Assert.All(rows, w => Assert.False(SatoraWithdrawals.OwnerCanResolve(w, running: true, now)));
        // The SDK caps an offboard's quote at 300 s and its exit at 600 s, each after up to 300 s waiting for its wallet lock.
        Assert.True(SatoraWithdrawals.InterruptedAfter > TimeSpan.FromSeconds(300 + 300 + 300 + 600));
        Assert.All(new[] { SatoraWithdrawalStatus.Sent, SatoraWithdrawalStatus.Failed },
            status => Assert.False(SatoraWithdrawals.IsUnresolved(Row("done", status, now.AddHours(-1)))));
    }

    // The swap server refuses an amount below its route's minimum before it quotes anything.
    [Fact]
    public async Task AReviewBelowTheServersMinimumSaysSoInTheStoresTerms()
    {
        _wallet.QuoteError = new SdkException(SdkErrorKind.Api, "Min amount is ₿ 0.00 010 000", 400);

        var review = await Review(SatoraLightningCreditTests.Bolt11, "0.00012345");

        Assert.Equal("Satora pays Lightning amounts of at least 10000 sats (0.00010000 BTC); this one is 12345 sats.", review.Error);
        Assert.DoesNotContain(_log.Entries, e => e.Level >= LogLevel.Warning);
    }

    // A settle writes no row, and the invoice a Lightning address issued is otherwise only on the swap's row.
    [Fact]
    public async Task LogsWhatLeavesTheWalletWithItsTransaction()
    {
        await Execute(Ark, "0.00001", key: "ark");
        await Execute(OnChain, null, sendAll: true, maxFeeSats: 200, key: "exit");
        _wallet.Balance = new ArkadeBalance(100_000, 0, 5_000);
        var settle = await _service.SettleAsync(Store, cancellationToken: Ct);
        await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400, key: "ln");

        Assert.Equal("The wallet's recoverable funds were settled in Arkade batch commitment.", settle.Message);
        Assert.Equal([
                $"Manual Arkade withdrawal ark from the Satora wallet of store store sent 1000 sats with a fee of 0 sats to {Ark} in ark-send",
                $"Manual Bitcoin on-chain withdrawal exit from the Satora wallet of store store sent 50000 sats with a fee of 200 sats to {OnChain} in commitment-offboard",
                "Settled the recoverable funds of the Satora wallet of store store in Arkade batch commitment, as asked on the withdrawals page",
                $"Manual Lightning address withdrawal ln from the Satora wallet of store store pays 12345 sats with fees of 55 sats to the invoice with payment hash {SatoraLightningCreditTests.PaymentHash}, which alice@example.com issued, through Satora swap swap"
            ],
            _log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message));
        Assert.Equal(["Funded Satora Lightning swap swap of store store in ark-swap"],
            _paymentsLog.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message));
    }

    [Fact]
    public async Task RecordsAnArkadeSendBeforeMakingItAndMarksItSent()
    {
        var result = await Execute(Ark, "0.00001");

        Assert.Equal(SatoraWithdrawResultKind.Sent, result.Kind);
        Assert.Equal(["withdrawal key (gated)", $"send {Ark} 1000 (gated)"], _events.All);
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Sent, TxId: "ark-send", AmountSats: 1_000, FeeSats: 0, Type: SatoraWithdrawalType.Arkade });
    }

    [Theory]
    [InlineData(SdkErrorKind.Transport, SatoraWithdrawalStatus.Unknown)]
    [InlineData(SdkErrorKind.Timeout, SatoraWithdrawalStatus.Unknown)]
    [InlineData(SdkErrorKind.Storage, SatoraWithdrawalStatus.Unknown)]
    [InlineData(SdkErrorKind.Internal, SatoraWithdrawalStatus.Unknown)]
    [InlineData(SdkErrorKind.InvalidSwap, SatoraWithdrawalStatus.Failed)]
    public async Task ASendWhoseOutcomeTheSdkCannotTellIsUnknownAndNotified(SdkErrorKind kind, SatoraWithdrawalStatus expected)
    {
        _wallet.SendError = new SdkException(kind, "send failed");

        var result = await Execute(Ark, "0.00001");

        Assert.Equal(expected, _withdrawals.Rows["key"].Status);
        Assert.Equal(expected == SatoraWithdrawalStatus.Unknown ? SatoraWithdrawResultKind.Unknown : SatoraWithdrawResultKind.Failed, result.Kind);
        Assert.Equal(expected == SatoraWithdrawalStatus.Unknown ? ["key"] : [], _service.Notified);
    }

    [Fact]
    public async Task RefusesASecondSubmitOfTheSameReview()
    {
        await Execute(Ark, "0.00001");

        var replay = await Execute(Ark, "0.00001");

        Assert.Equal(SatoraWithdrawResultKind.Replayed, replay.Kind);
        Assert.Single(_events.All, e => e.StartsWith("send", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("key with spaces")]
    public async Task RefusesASubmitWithoutAReviewKeyBeforeRecordingAnything(string? key)
    {
        var result = await _service.ExecuteAsync(Store, new SatoraWithdrawRequest { Destination = Ark, AmountBtc = "0.00001", Key = key });

        Assert.Equal(SatoraWithdrawResultKind.Refused, result.Kind);
        Assert.Empty(_events.All);
    }

    [Theory]
    [InlineData(SatoraWithdrawalStatus.Unknown, 0)]
    [InlineData(SatoraWithdrawalStatus.Pending, 0)]
    [InlineData(SatoraWithdrawalStatus.Pending, 20)]
    public async Task RefusesANewWithdrawalWhileAnotherOnesOutcomeIsUnknown(SatoraWithdrawalStatus status, int minutesAgo)
    {
        _withdrawals.Rows["earlier"] = Row("earlier", status, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

        var result = await Execute(Ark, "0.00001");
        var review = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = Ark, AmountBtc = "0.00001" });

        Assert.Equal(SatoraWithdrawResultKind.Refused, result.Kind);
        Assert.Equal(SatoraWithdrawalService.UnresolvedMessage, review.Error);
        Assert.Null(review.Key);
        Assert.Equal(["earlier"], _withdrawals.Rows.Keys);
        Assert.Empty(_events.All);
    }

    // A Lightning withdrawal in flight is resolved by the worker and does not hold up others.
    [Fact]
    public async Task ALightningWithdrawalInFlightDoesNotRefuseANewOne()
    {
        var lightning = Row("lightning", SatoraWithdrawalStatus.Pending, DateTimeOffset.UtcNow);
        lightning.SwapId = "swap";
        _withdrawals.Rows["lightning"] = lightning;

        Assert.Equal(SatoraWithdrawResultKind.Sent, (await Execute(Ark, "0.00001")).Kind);
    }

    [Fact]
    public async Task AWalletThatCannotBeOpenedRecordsAndSendsNothing()
    {
        _wallets.OpenError = new SatoraWalletUnavailableException("The store's Satora wallet is not usable (Locked).");

        var result = await Execute(Ark, "0.00001");
        var review = await Review(Ark, "0.00001");

        Assert.Equal(SatoraWithdrawResultKind.Unknown, result.Kind);
        Assert.Contains("Locked", result.Message);
        Assert.Null(review.Key);
        Assert.Empty(_withdrawals.Rows);
        Assert.Empty(_events.All);
    }

    // Withdrawals spend confirmed and pre-confirmed funds only and never settle, so recoverable funds
    // that cannot be settled, such as change below dust, block none of them.
    [Theory]
    [InlineData(Ark, "0.0009", false, null, null, "send {1} 90000", SatoraWithdrawResultKind.Sent)]
    [InlineData(Ark, null, true, null, null, "send {1} 90000", SatoraWithdrawResultKind.Sent)]
    [InlineData("{0}", "0.0005", false, 50_200L, null, "offboard {0} 50000", SatoraWithdrawResultKind.Sent)]
    [InlineData("{0}", null, true, null, 200L, "offboard {0} all", SatoraWithdrawResultKind.Sent)]
    [InlineData("alice@example.com", "0.00012345", false, 12_400L, null, "fund swap", SatoraWithdrawResultKind.Pending)]
    [InlineData("alice@example.com", null, true, null, 55L, "fund swap", SatoraWithdrawResultKind.Pending)]
    public async Task WithdrawsWhileTheWalletHoldsRecoverableFundsWithoutSettlingThem(string destination, string? amountBtc, bool sendAll,
        long? maxTotalSats, long? maxFeeSats, string spend, SatoraWithdrawResultKind expected)
    {
        _wallet.Balance = new ArkadeBalance(80_000, 10_000, 200);
        _wallet.SettleError = new SdkException(SdkErrorKind.Transport, SubDust);

        var result = await Execute(string.Format(destination, OnChain), amountBtc, sendAll, maxTotalSats, maxFeeSats: maxFeeSats);

        Assert.Equal(expected, result.Kind);
        Assert.Contains(string.Format(spend, OnChain, Ark) + " (gated)", _events.All);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("settle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendsNothingWhenTheWalletCannotSpendIt()
    {
        _wallet.Balance = new ArkadeBalance(1_000, 2_000, 5_000);

        var result = await Execute(Ark, "0.00004");

        Assert.Equal(SatoraWithdrawResultKind.Failed, result.Kind);
        Assert.Contains("can spend 3000 sats", result.Message);
        Assert.Equal(["withdrawal key (gated)"], _events.All);
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, TxId: null });
    }

    [Fact]
    public async Task OffboardsEverythingAndRecordsTheQuotedAmountAndFee()
    {
        var result = await Execute(OnChain, null, sendAll: true, maxFeeSats: 200);

        Assert.Equal(SatoraWithdrawResultKind.Sent, result.Kind);
        Assert.Equal(["withdrawal key (gated)", $"offboard quote {OnChain} all (gated)", $"offboard {OnChain} all (gated)"], _events.All);
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Sent, TxId: "commitment-offboard", AmountSats: 50_000, FeeSats: 200 });
    }

    [Fact]
    public async Task RefusesAnExitTakingMoreThanTheReviewShowed()
    {
        var capped = await Execute(OnChain, "0.0005", maxTotalSats: 50_199);
        var everything = await Execute(OnChain, null, sendAll: true, maxFeeSats: 199, key: "all");

        Assert.Equal((SatoraWithdrawResultKind.Failed, SatoraWithdrawResultKind.Failed), (capped.Kind, everything.Kind));
        Assert.All(_withdrawals.Rows.Values, w => Assert.Equal(SatoraWithdrawalStatus.Failed, w.Status));
        Assert.DoesNotContain(_events.All, e => e.StartsWith("offboard bc1", StringComparison.Ordinal));
        Assert.Equal(SatoraWithdrawResultKind.Sent, (await Execute(OnChain, "0.0005", maxTotalSats: 50_200, key: "exact")).Kind);
    }

    [Theory]
    [InlineData("{0}", "0.0005", false)]
    [InlineData("{0}", null, true)]
    [InlineData("alice@example.com", "0.00012345", false)]
    [InlineData("alice@example.com", null, true)]
    [InlineData(SatoraLightningCreditTests.Bolt11, null, false)]
    public async Task RefusesAnExitOrALightningPaymentWithoutTheReviewedCapBeforeRecordingAnything(string destination, string? amountBtc, bool sendAll)
    {
        var result = await Execute(string.Format(destination, OnChain), amountBtc, sendAll);

        Assert.Equal(SatoraWithdrawResultKind.Refused, result.Kind);
        Assert.Empty(_withdrawals.Rows);
        Assert.Empty(_events.All);
    }

    // After a restart nothing is running, so the label is exact; in this process a running request
    // is never handed to the owner, however long it takes.
    [Fact]
    public async Task AWithdrawalIsRunningUntilItsRequestEndsAndOnlyThenCanBeResolved()
    {
        var send = new TaskCompletionSource<bool>();
        _wallet.SendGate = send.Task;
        _wallet.SendError = new SdkException(SdkErrorKind.Transport, "connection reset");

        var execute = Execute(Ark, "0.00001");
        // As if the send had been running for an hour.
        _withdrawals.Rows["key"].CreatedAt -= TimeSpan.FromHours(1);
        Assert.Contains("key", (await _service.GetPageAsync(Store)).Running);
        Assert.False(await _service.ResolveAsync(Store, "key", sent: false));
        send.SetResult(true);

        Assert.Equal(SatoraWithdrawResultKind.Unknown, (await execute).Kind);
        Assert.DoesNotContain("key", (await _service.GetPageAsync(Store)).Running);
        Assert.True(await _service.ResolveAsync(Store, "key", sent: false));
        Assert.Equal(SatoraWithdrawalStatus.Failed, _withdrawals.Rows["key"].Status);
    }

    // BTCPay's payout listener completes the payout on Complete and cancels it on Failed.
    [Theory]
    [InlineData(true, SatoraWithdrawalStatus.Sent, LightningPaymentStatus.Complete)]
    [InlineData(false, SatoraWithdrawalStatus.Failed, LightningPaymentStatus.Failed)]
    public async Task TheStoreClosesOnlyALightningPaymentTheWorkerCannotFinishAndItAndItsWithdrawalTakeTheStoresAnswer(bool paid,
        SatoraWithdrawalStatus withdrawalStatus, LightningPaymentStatus paymentStatus)
    {
        UnfinishedSwap();
        _swaps.Rows["swap"].ClaimCutoff = DateTimeOffset.UtcNow.AddHours(1);

        Assert.Empty((await _service.GetPageAsync(Store)).Unfinished);
        Assert.False((await _service.CloseSwapAsync(Store, "swap", paid)).Closed);
        _swaps.Rows["swap"].ClaimCutoff = DateTimeOffset.UtcNow.AddMinutes(-1);
        Assert.Equal(["swap"], (await _service.GetPageAsync(Store)).Unfinished.Select(s => s.SwapId));
        Assert.False((await _service.CloseSwapAsync("other-store", "swap", paid)).Closed);
        Assert.True((await _service.CloseSwapAsync(Store, "swap", paid)).Closed);

        Assert.NotNull(_swaps.Rows["swap"].CompletedAt);
        Assert.Equal(paymentStatus, SatoraOutgoingActions.ToPayment(_swaps.Rows["swap"]).Status);
        Assert.Equal(withdrawalStatus, _withdrawals.Rows["lightning"].Status);
        Assert.False((await _service.CloseSwapAsync(Store, "swap", !paid)).Closed);
        Assert.Equal(withdrawalStatus, _withdrawals.Rows["lightning"].Status);
    }

    // Past the refund locktime the worker refunds the VHTLC once median time past allows, about an
    // hour later; until then the funds are not back in the wallet, whatever the store answers.
    [Fact]
    public async Task TheStoreCannotCloseALightningPaymentAsFailedWhileItsVhtlcHoldsTheFunds()
    {
        UnfinishedSwap();
        _wallet.Amounts = new VhtlcAmounts(12_000, 400, 0, []);

        var page = await _service.GetPageAsync(Store);
        var failed = await _service.CloseSwapAsync(Store, "swap", paid: false);

        Assert.Equal(new VhtlcAmounts(12_000, 400, 0, []), page.UnfinishedHeld["swap"]);
        Assert.False(failed.Closed);
        Assert.Contains("still holds 12400 sats", failed.Message);
        Assert.True(_swaps.Rows["swap"] is { CompletedAt: null });
        Assert.True(_withdrawals.Rows["lightning"] is { Status: SatoraWithdrawalStatus.Pending });

        _wallet.Amounts = new VhtlcAmounts(0, 0, 12_400, ["0xclaim"]);
        Assert.Equal((true, "Payment closed as failed."), await _service.CloseSwapAsync(Store, "swap", paid: false));
    }

    // Unfinished payments pile up while arkd or its indexer is unreachable, and each read then waits
    // for the SDK's network timeout: the page reads every VHTLC at once and gives up on each in time.
    [Fact]
    public async Task ThePageReadsEveryVhtlcAtOnceAndGivesUpOnOneThatDoesNotAnswer()
    {
        UnfinishedSwap();
        UnfinishedSwap("other", "other-lightning");
        _wallet.Amounts = new VhtlcAmounts(12_400, 0, 0, []);
        var started = 0;
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _wallet.AmountsGate = () =>
        {
            if (Interlocked.Increment(ref started) == 2)
                both.SetResult();
            return both.Task;
        };

        var answered = await _service.GetPageAsync(Store);
        _wallet.AmountsGate = () => new TaskCompletionSource().Task;
        var unanswered = await _service.GetPageAsync(Store);

        Assert.Equal(["other", "swap"], answered.UnfinishedHeld.Keys.Order());
        Assert.All(answered.UnfinishedHeld.Values, held => Assert.Equal(_wallet.Amounts, held));
        Assert.Equal(["other", "swap"], unanswered.Unfinished.Select(s => s.SwapId).Order());
        Assert.All(unanswered.UnfinishedHeld.Values, held => Assert.Null(held));
    }

    // The server may still claim a VHTLC it has paid the invoice for, and a refund without the server
    // has emptied it already.
    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "refund-alone")]
    public async Task OnlyAFailedCloseWithoutARefundIsHeldToTheVhtlc(bool paid, string? refundTxId)
    {
        UnfinishedSwap();
        _swaps.Rows["swap"].RefundTxId = refundTxId;
        _wallet.Amounts = new VhtlcAmounts(12_400, 0, 0, []);

        Assert.True((await _service.CloseSwapAsync(Store, "swap", paid)).Closed);
    }

    // The indexer may be down exactly when the plugin is stuck, and an Arkade server or VTXO that does
    // not match the swap, as from a misconfigured arkd URL, can clear later. A swap the SDK has no
    // record of can never be refunded, and must not block a seed replace for good.
    [Theory]
    [InlineData(SdkErrorKind.Transport, "indexer unreachable", false)]
    [InlineData(SdkErrorKind.Timeout, "swap record missing", false)]
    [InlineData(SdkErrorKind.InvalidSwap, "Arkade server identity or network mismatch", false)]
    [InlineData(SdkErrorKind.InvalidSwap, "Arkade VTXO script, amount or identity mismatch", false)]
    [InlineData(SdkErrorKind.InvalidSwap, "swap record missing", true)]
    public async Task AFailedCloseWhoseVhtlcCannotBeReadIsRefusedUnlessTheSdkHasNoSuchSwap(SdkErrorKind kind, string error, bool closes)
    {
        UnfinishedSwap();
        _wallet.AmountsError = new SdkException(kind, error);

        var page = await _service.GetPageAsync(Store);
        var (closed, message) = await _service.CloseSwapAsync(Store, "swap", paid: false);

        Assert.Null(page.UnfinishedHeld["swap"]);
        Assert.Equal(closes, closed);
        Assert.Equal(closes, _swaps.Rows["swap"].CompletedAt is not null);
        if (!closes)
            Assert.Contains($"could not be read ({error})", message);
    }

    // A funding takes the same lock, so none lands between the close's checks and its write.
    [Fact]
    public async Task TheStoreClosesALightningPaymentUnderTheSpendLock()
    {
        UnfinishedSwap();

        Assert.True((await _service.CloseSwapAsync(Store, "swap", paid: false)).Closed);
        Assert.Equal(["close swap (gated)"], _events.All);
    }

    [Fact]
    public async Task TheStoresCloseGivesUpWhileTheWalletStaysBusy()
    {
        UnfinishedSwap();
        var release = new TaskCompletionSource<bool>();
        var busy = _wallets.SpendAsync(Store, _ => release.Task, TestContext.Current.CancellationToken);

        var (closed, message) = await _service.CloseSwapAsync(Store, "swap", paid: true);
        release.SetResult(true);
        await busy;

        Assert.False(closed);
        Assert.Contains("The wallet is busy", message);
        Assert.Null(_swaps.Rows["swap"].CompletedAt);
        Assert.True((await _service.CloseSwapAsync(Store, "swap", paid: true)).Closed);
    }

    // The close reads the VHTLC with the spend lock held, which every funding of the store waits for.
    [Fact]
    public async Task AFailedCloseGivesUpOnAVhtlcReadThatDoesNotAnswer()
    {
        UnfinishedSwap();
        _wallet.AmountsGate = () => new TaskCompletionSource().Task;

        var (closed, message) = await _service.CloseSwapAsync(Store, "swap", paid: false).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.False(closed);
        Assert.Contains("could not be read", message);
        Assert.Null(_swaps.Rows["swap"].CompletedAt);
        Assert.True((await _service.CloseSwapAsync(Store, "swap", paid: true)).Closed);
    }

    // The request may still write the funding's txid; the owner's answer waits for it to end.
    [Fact]
    public async Task TheStoreCannotCloseALightningPaymentWhileItsWithdrawalIsRunning()
    {
        var fund = new TaskCompletionSource();
        _wallet.FundGate = fund.Task;
        var execute = Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);
        // FakeWallet.Locktime has passed, so the store may close the swap once nothing runs it.
        _swaps.Rows["swap"].Status = SatoraSwapStatus.ServerFunded;

        Assert.False((await _service.CloseSwapAsync(Store, "swap", paid: true)).Closed);
        fund.SetResult();
        Assert.Equal(SatoraWithdrawResultKind.Pending, (await execute).Kind);
        Assert.True((await _service.CloseSwapAsync(Store, "swap", paid: true)).Closed);
        Assert.Equal(SatoraWithdrawalStatus.Sent, _withdrawals.Rows["key"].Status);
    }

    // The worker closed the swap, unfunded, after the withdrawal attached it and before it marked the attempt.
    [Fact]
    public async Task ALightningWithdrawalWhoseSwapWasClosedBeforeItsAttemptSendsNothing()
    {
        _swaps.BeforeFundingAttempted = () =>
            _withdrawals.CloseUnattemptedSwapAsync("swap", SatoraSwapStatus.Expired, "unfunded", DateTimeOffset.UtcNow).GetAwaiter().GetResult();

        var result = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Equal(SatoraWithdrawResultKind.Failed, result.Kind);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("fund", StringComparison.Ordinal));
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: false, CompletedAt: not null });
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, TxId: null });
    }

    // The Lightning address is resolved once the withdrawal is recorded, and the swap pays the
    // invoice its endpoint issued.
    [Fact]
    public async Task HandsALightningWithdrawalToTheWorkerOnceItsSwapIsFunded()
    {
        var result = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Equal(SatoraWithdrawResultKind.Pending, result.Kind);
        Assert.Equal(["withdrawal key (gated)", "lnurl https://example.com/.well-known/lnurlp/alice (gated)", "lnurl invoice 12345 (gated)",
                $"create {new SdkLightningDestination.Invoice(SatoraLightningCreditTests.Bolt11)} (gated)", "add swap (gated)", "attempted swap (gated)", "fund swap (gated)"],
            _events.All);
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Pending, SwapId: "swap", TxId: "ark-swap", AmountSats: 12_345, FeeSats: 55 });
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: true, FundingTxId: "ark-swap" });
    }

    // A swap server answering with a swap for an invoice of its own would be paid by the VHTLC.
    [Theory]
    [InlineData("alice@example.com", "0.00012345", false)]
    [InlineData("alice@example.com", null, true)]
    [InlineData("LNURL1DP68GURN8GHJ7UM9WFMXJCM99E3K7MF0V9CXJ0M385EKVCENXC6R2C35XVUKXEFCV5MKVV34X5EKZD3EV56NYD3HXQURZEPEXEJXXEPNXSCRVWFNV9NXZCN9XQ6XYEFHVGCXXCMYXYMNSERXFQ5FNS", "0.00012345", false)]
    public async Task PaysALightningAddressOrLnurlOnlyThroughTheInvoiceItsEndpointIssued(string destination, string? amountBtc, bool sendAll)
    {
        _wallet.Create = _ => FakeWallet.ArkadeToLightning("swap", bolt11: OtherBolt11);

        var result = await Execute(destination, amountBtc, sendAll, maxTotalSats: 12_400, maxFeeSats: 55);

        Assert.Equal(SatoraWithdrawResultKind.Failed, result.Kind);
        Assert.Contains("lnurl invoice 12345 (gated)", _events.All);
        Assert.Contains($"create {new SdkLightningDestination.Invoice(SatoraLightningCreditTests.Bolt11)} (gated)", _events.All);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("attempted", StringComparison.Ordinal) || e.StartsWith("fund", StringComparison.Ordinal));
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, SwapId: null, TxId: null });
    }

    [Fact]
    public async Task SendsNothingToALightningAddressThatRefusesTheAmountOrAnswersWithAnotherInvoice()
    {
        _service.PayRequest.MinSendable = LightMoney.Satoshis(12_346);
        var belowMin = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400, key: "min");
        _service.PayRequest.MinSendable = LightMoney.Satoshis(1);
        _service.PayRequest.MaxSendable = LightMoney.Satoshis(12_344);
        var aboveMax = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400, key: "max");
        _service.PayRequest.MaxSendable = LightMoney.Satoshis(1_000_000);
        _service.Invoice = OtherBolt11;
        var otherAmount = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400, key: "amount");

        Assert.All([belowMin, aboveMax, otherAmount], r => Assert.Equal(SatoraWithdrawResultKind.Failed, r.Kind));
        Assert.Contains("from 12346 to 1000000 sats, not 12345 sats", belowMin.Message);
        Assert.Contains("from 1 to 12344 sats, not 12345 sats", aboveMax.Message);
        Assert.Contains("not for 12345 sats", otherAmount.Message);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("create", StringComparison.Ordinal));
        Assert.All(_withdrawals.Rows.Values, w => Assert.True(w is { Status: SatoraWithdrawalStatus.Failed, SwapId: null }));
    }

    [Fact]
    public async Task ALightningAddressThatDoesNotAnswerInTimeSendsNothingAndSaysSo()
    {
        _service.PayRequestGate = new TaskCompletionSource().Task;

        var result = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Equal(SatoraWithdrawResultKind.Failed, result.Kind);
        Assert.Contains("The Lightning address did not answer in time", result.Message);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("create", StringComparison.Ordinal));
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, SwapId: null });
    }

    // The payee's server is the payee's to fix: the store is told whose server it was, and a review logs
    // no warning for it.
    [Fact]
    public async Task ALightningAddressWhoseServerCannotBeReachedSaysSo()
    {
        const string unreachable = "The Lightning address's server could not be reached: Connection refused (example.com:443).";
        _service.PayeeError = new HttpRequestException("Connection refused (example.com:443)");

        var review = await Review("alice@example.com", "0.00012345");
        var warnings = _log.Entries.Count(e => e.Level >= LogLevel.Warning);
        var result = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Equal(unreachable, review.Error);
        Assert.Equal(0, warnings);
        Assert.Equal((SatoraWithdrawResultKind.Failed, "Nothing was sent: " + unreachable), (result.Kind, result.Message));
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, SwapId: null });
    }

    // An automatic run asks the payee holding the store's spend lock, so the payee gets one timeout for
    // all the requests of a lookup. A pay request answered just as that timeout runs out leaves the
    // invoice request no time of its own.
    [Fact]
    public async Task ALightningAddressGetsOneTimeoutForAllTheRequestsOfALookup()
    {
        _service.PayRequestAnswersAtTimeout = true;

        var review = await Review("alice@example.com", "0.00012345");
        var result = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Contains("The Lightning address did not answer in time", review.Error);
        Assert.Equal(SatoraWithdrawResultKind.Failed, result.Kind);
        Assert.Contains("The Lightning address did not answer in time", result.Message);
        Assert.Equal(2, _events.All.Count(e => e.StartsWith("lnurl invoice 12345", StringComparison.Ordinal)));
        Assert.DoesNotContain(_events.All, e => e.StartsWith("create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALightningFundingWithAnUnknownOutcomeStaysPendingForTheWorker()
    {
        _wallet.FundError = new SdkException(SdkErrorKind.Transport, "connection reset");

        var result = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Equal(SatoraWithdrawResultKind.Pending, result.Kind);
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Pending, SwapId: "swap", TxId: null });
        Assert.False(SatoraWithdrawals.IsUnresolved(_withdrawals.Rows["key"]));
        Assert.Empty(_service.Notified);
    }

    // The SDK leaves funds about to expire out of every send, exit and swap funding, and its refusal
    // for them comes before anything is sent: they are renewed, and the store is asked to try again.
    [Fact]
    public async Task AnArkadeSendRefusedForFundsAboutToExpireRenewsThemAndAsksToTryAgain()
    {
        _wallet.SendError = new SdkException(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.ShortOfExpiring);

        var result = await Execute(Ark, "0.00001");

        Assert.Equal((SatoraWithdrawResultKind.Failed, "Nothing was sent: " + SatoraWithdrawalService.ExpiringFundsMessage), (result.Kind, result.Message));
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, TxId: null, Error: SatoraWithdrawalService.ExpiringFundsMessage });
        Assert.Equal([Store], _renewals.Started);
        Assert.Empty(_service.Notified);
    }

    [Fact]
    public async Task AnExitRefusedForFundsAboutToExpireRenewsThemAndAsksToTryAgain()
    {
        _wallet.SendError = new SdkException(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.OffboardShortOfExpiring);

        var result = await Execute(OnChain, null, sendAll: true, maxFeeSats: 200);

        Assert.Equal((SatoraWithdrawResultKind.Failed, "Nothing was sent: " + SatoraWithdrawalService.ExpiringFundsMessage), (result.Kind, result.Message));
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, TxId: null });
        Assert.Equal([Store], _renewals.Started);
    }

    [Fact]
    public async Task AnExitsReviewRefusedForFundsAboutToExpireRenewsThemAndAsksToTryAgain()
    {
        _wallet.OffboardQuoteError = new SdkException(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.OffboardShortOfExpiring);

        var review = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = OnChain, SendAll = true });

        Assert.Equal((SatoraWithdrawalService.ExpiringFundsMessage, null), (review.Error, review.Key));
        Assert.Equal([Store], _renewals.Started);
    }

    // The swap's attempt is undone, so the worker never funds it, and the withdrawal failed.
    [Fact]
    public async Task ALightningWithdrawalRefusedForFundsAboutToExpireRenewsThemAndAsksToTryAgain()
    {
        _wallet.FundError = new SdkException(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.ShortOfExpiring);

        var result = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Equal((SatoraWithdrawResultKind.Failed, "Nothing was sent: " + SatoraWithdrawalService.ExpiringFundsMessage), (result.Kind, result.Message));
        Assert.True(_withdrawals.Rows["key"] is { Status: SatoraWithdrawalStatus.Failed, SwapId: "swap", TxId: null });
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: false, FundingTxId: null, CompletedAt: null });
        Assert.Equal([Store], _renewals.Started);
    }

    // The swap server, like the swaps table, takes one swap per invoice, so the retry the refusal asks
    // for funds the swap the first attempt recorded, and takes its link from the failed withdrawal.
    [Fact]
    public async Task ALightningInvoiceWithdrawalRetriedAfterARefusalForFundsAboutToExpireFundsTheSameSwap()
    {
        var invoice = TestBolt11.Create(12_345, Network.Main);
        _wallet.Create = _ => FakeWallet.ArkadeToLightning("swap", bolt11: invoice);
        _wallet.FundError = new SdkException(SdkErrorKind.InvalidSwap, SatoraWalletRenewalsTests.ShortOfExpiring);
        var refused = await Execute(invoice, null, maxTotalSats: 12_400, key: "first");
        Assert.Equal((SatoraWithdrawResultKind.Failed, "Nothing was sent: " + SatoraWithdrawalService.ExpiringFundsMessage), (refused.Kind, refused.Message));
        _wallet.FundError = null;
        _wallet.CreateError = SwapExists();
        _events.All.Clear();

        var result = await Execute(invoice, null, maxTotalSats: 12_400, key: "retry");

        Assert.Equal(SatoraWithdrawResultKind.Pending, result.Kind);
        Assert.Equal(["withdrawal retry (gated)", "move swap to retry (gated)", "attempted swap (gated)", "fund swap (gated)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: true, FundingTxId: "ark-swap", CompletedAt: null });
        Assert.True(_withdrawals.Rows["retry"] is { Status: SatoraWithdrawalStatus.Pending, SwapId: "swap", TxId: "ark-swap", AmountSats: 12_345, FeeSats: 55 });
        Assert.True(_withdrawals.Rows["first"] is { Status: SatoraWithdrawalStatus.Failed, SwapId: null, TxId: null });
    }

    // Only a swap not marked attempted, which nothing can have paid, open and still awaiting funding,
    // with no withdrawal but a failed one pointing at it, is taken over. Any other is refused before
    // anything is asked of the swap server or the wallet, and keeps its link.
    [Theory]
    // Funded.
    [InlineData(true, "ark-swap", SatoraSwapStatus.ClientFunded, false, SatoraWithdrawalStatus.Pending)]
    // A funding whose outcome the worker has not resolved yet.
    [InlineData(true, null, SatoraSwapStatus.Pending, false, SatoraWithdrawalStatus.Pending)]
    // Given up by the swap server, or closed.
    [InlineData(false, null, SatoraSwapStatus.Expired, false, SatoraWithdrawalStatus.Failed)]
    [InlineData(false, null, SatoraSwapStatus.Expired, true, SatoraWithdrawalStatus.Failed)]
    // Unfunded, but its withdrawal is still pending.
    [InlineData(false, null, SatoraSwapStatus.Pending, false, SatoraWithdrawalStatus.Pending)]
    public async Task ALightningInvoiceWithdrawalRetriedOnASwapThatMayHoldFundsOrHasEndedSendsNothing(bool attempted, string? fundingTxId,
        SatoraSwapStatus status, bool closed, SatoraWithdrawalStatus earlier)
    {
        var invoice = TestBolt11.Create(12_345, Network.Main);
        _wallet.CreateError = SwapExists();
        EarlierPayment(invoice, earlier, swap =>
        {
            (swap.FundingAttempted, swap.FundingTxId, swap.Status) = (attempted, fundingTxId, status);
            swap.CompletedAt = closed ? DateTimeOffset.UtcNow : null;
        });
        var before = MemoryOutgoingSwaps.Copy(_swaps.Rows["swap"]);

        var result = await Execute(invoice, null, maxTotalSats: 12_400, key: "retry");

        Assert.Equal((SatoraWithdrawResultKind.Failed,
                "Nothing was sent: An earlier payment from this wallet already used this invoice (Satora swap swap). Ask the payee for a new invoice if it was not paid."),
            (result.Kind, result.Message));
        Assert.DoesNotContain(_events.All, e => e.StartsWith("create", StringComparison.Ordinal) || e.StartsWith("attempted", StringComparison.Ordinal) ||
                                                e.StartsWith("fund", StringComparison.Ordinal));
        Assert.Equivalent(before, _swaps.Rows["swap"]);
        Assert.True(_withdrawals.Rows["retry"] is { Status: SatoraWithdrawalStatus.Failed, SwapId: null, TxId: null });
        Assert.True(_withdrawals.Rows["earlier"] is { SwapId: "swap" } row && row.Status == earlier);
    }

    // As for a payout, the swap of an invoice that has expired is not funded.
    [Fact]
    public async Task ALightningInvoiceWithdrawalRetriedAfterTheInvoiceExpiredSendsNothing()
    {
        _wallet.CreateError = SwapExists();
        EarlierPayment(SatoraLightningCreditTests.Bolt11, SatoraWithdrawalStatus.Failed);

        var result = await Execute(SatoraLightningCreditTests.Bolt11, null, maxTotalSats: 12_400, key: "retry");

        Assert.Equal((SatoraWithdrawResultKind.Failed, "Nothing was sent: The invoice has expired."), (result.Kind, result.Message));
        Assert.Equal(["withdrawal retry (gated)"], _events.All);
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: false, CompletedAt: null });
        Assert.True(_withdrawals.Rows["earlier"] is { Status: SatoraWithdrawalStatus.Failed, SwapId: "swap" });
    }

    // Sending everything takes what the wallet holds when it runs, so its total is not held to the
    // review's; its fees are. The test swap keeps 55 sats of fees.
    [Fact]
    public async Task RefusesALightningSwapTakingMoreThanTheReviewShowed()
    {
        var capped = await Execute("alice@example.com", "0.00012345", maxTotalSats: 12_399);
        Assert.Equal(SatoraWithdrawResultKind.Failed, capped.Kind);
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: false });

        _wallet.Create = _ => FakeWallet.ArkadeToLightning("all-over");
        var overFee = await Execute("alice@example.com", null, sendAll: true, maxTotalSats: 1, maxFeeSats: 54, key: "over");
        Assert.Equal(SatoraWithdrawResultKind.Failed, overFee.Kind);
        Assert.Contains("55 sats", overFee.Message);
        Assert.True(_swaps.Rows["all-over"] is { FundingAttempted: false });

        _wallet.Create = _ => FakeWallet.ArkadeToLightning("all");
        _events.All.Clear();
        var everything = await Execute("alice@example.com", null, sendAll: true, maxTotalSats: 1, maxFeeSats: 55, key: "all");
        Assert.Equal(SatoraWithdrawResultKind.Pending, everything.Kind);
        Assert.True(_swaps.Rows["all"] is { FundingAttempted: true });
        Assert.Equal(["send-max quote 100000 (gated)", "lnurl invoice 12345 (gated)", $"quote {new SdkLightningDestination.Invoice(SatoraLightningCreditTests.Bolt11)} (gated)"],
            _events.All.Where(e => e.Contains("quote", StringComparison.Ordinal) || e.StartsWith("lnurl invoice", StringComparison.Ordinal)));
    }

    // The amount-only quote estimates the Lightning fee. When the invoice's own quote comes out
    // higher, sending everything asks the payee once more, for less by the difference.
    [Fact]
    public async Task SendingEverythingLowersTheAmountOnceWhenTheInvoicesFeeIsAboveTheEstimate()
    {
        _service.InvoiceFor = sats => TestBolt11.Create(sats, Network.Main);
        _wallet.SendMaxQuote = _wallet.SendMaxQuote with { NetTargetAmount = "99690" };
        var networkFees = new Queue<long>([20, 20]);
        _wallet.QuoteFor = destination => QuoteOf(destination, networkFees.Dequeue());

        var lowered = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = "alice@example.com", SendAll = true });
        networkFees = new Queue<long>([20, 40]);
        var refused = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = "alice@example.com", SendAll = true });

        Assert.True(lowered is { Error: null, AmountSats: 99_680, FeeSats: 320, TotalSats: 100_000 }, lowered.Error);
        Assert.Equal("Paying the Lightning address takes 100020 sats with fees, more than the 100000 sats the wallet can send.", refused.Error);
        Assert.Equal(["lnurl invoice 99690", "lnurl invoice 99680", "lnurl invoice 99690", "lnurl invoice 99680"],
            _events.All.Where(e => e.StartsWith("lnurl invoice", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReviewsALightningWithdrawalWithTheQuotedFeesAndTotal()
    {
        var review = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = "alice@example.com", AmountBtc = "0.00012345" });

        Assert.Null(review.Error);
        Assert.NotNull(review.Key);
        Assert.Equal((12_345L, 55L, 12_400L, 100_000L), (review.AmountSats!.Value, review.FeeSats, review.TotalSats, review.AvailableSats));
        Assert.DoesNotContain(_events.All, e => e.EndsWith("(gated)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReviewRefusesWhatTheQuoteOrTheBalanceDoesNotAllow()
    {
        _wallet.LightningQuote = Quote with { MaxAmountSats = 10_000 };
        Assert.Contains("10000 sats", (await Review("alice@example.com", "0.00012345")).Error);

        _wallet.LightningQuote = Quote;
        _wallet.Balance = new ArkadeBalance(12_000, 399, 0);
        Assert.Contains("12399 sats", (await Review("alice@example.com", "0.00012345")).Error);

        _wallet.QuoteError = new SdkException(SdkErrorKind.Api, "could not quote this invoice", 400);
        Assert.Equal("could not quote this invoice", (await Review("alice@example.com", "0.00012345")).Error);
    }

    [Fact]
    public async Task ReviewsAnOffboardWithTheQuotedAmountAndAnArkadeSendOfEverythingItCanSpend()
    {
        var offboard = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = OnChain, SendAll = true });
        Assert.Equal((50_000L, 200L, 50_200L), (offboard.AmountSats!.Value, offboard.FeeSats, offboard.TotalSats));

        _wallet.Balance = new ArkadeBalance(1_000, 200, 30);
        var arkade = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = Ark, SendAll = true });
        Assert.Equal((1_200L, 0L, 1_200L, 30L), (arkade.TotalSats, arkade.FeeSats, arkade.AvailableSats, arkade.RecoverableSats));
    }

    // The page offers to settle whenever a review carries recoverable funds, refused or not.
    [Theory]
    [InlineData(Ark)]
    [InlineData("{0}")]
    [InlineData("alice@example.com")]
    public async Task ReviewLeavesOutRecoverableFundsAndRefusesWhatOnlyTheyWouldCover(string destination)
    {
        _wallet.Balance = new ArkadeBalance(80_000, 10_000, 10_200);
        _service.InvoiceFor = sats => TestBolt11.Create(sats, Network.Main);
        destination = string.Format(destination, OnChain);

        var covered = await Review(destination, "0.0005");
        var uncovered = await Review(destination, "0.00095");

        Assert.True(covered is { Error: null, Key: not null, AvailableSats: 90_000, RecoverableSats: 10_200 });
        Assert.True(uncovered is { Key: null, AvailableSats: 90_000, RecoverableSats: 10_200 });
        Assert.Contains("the wallet can spend 90000 sats", uncovered.Error);
        Assert.Contains("Another 10200 sats are recoverable. Settle them", uncovered.Error);
        Assert.DoesNotContain(_events.All, e => e.Contains("95000", StringComparison.Ordinal) || e.StartsWith("settle", StringComparison.Ordinal));
    }

    // A recoverable total below dust cannot be settled on its own, so it is not offered to settle.
    [Theory]
    [InlineData(330UL, " Another 330 sats are recoverable. Settle them, then review the withdrawal again.")]
    [InlineData(329UL, " Another 329 sats are recoverable but too small to settle yet.")]
    [InlineData(2UL, " Another 2 sats are recoverable but too small to settle yet.")]
    [InlineData(0UL, "")]
    public async Task ReviewRefusesATotalOverWhatTheWalletCanSpendAndSaysWhatItsRecoverableFundsNeed(ulong recoverable, string hint)
    {
        _wallet.Balance = new ArkadeBalance(12_000, 350, recoverable);

        var review = await Review("alice@example.com", "0.00012345");

        Assert.Equal("The withdrawal takes 12400 sats, but the wallet can spend 12350 sats." + hint, review.Error);
    }

    [Fact]
    public async Task TheOwnerSettlesRecoverableFundsOnlyWhileNoWithdrawalIsUnresolved()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_200);
        _withdrawals.Rows["earlier"] = Row("earlier", SatoraWithdrawalStatus.Unknown, DateTimeOffset.UtcNow);

        Assert.Equal(new SatoraSettleResult(SatoraSettleOutcome.Blocked, SatoraWithdrawalService.UnresolvedMessage), await _service.SettleAsync(Store, cancellationToken: Ct));
        Assert.Empty(_events.All);

        _withdrawals.Rows.Clear();
        Assert.Equal(SatoraSettleOutcome.Settled, (await _service.SettleAsync(Store, cancellationToken: Ct)).Outcome);
        Assert.Equal(["settle (gated)"], _events.All);
        Assert.Equal(new ArkadeBalance(100_200, 0, 0), _wallet.Balance);
        Assert.Equal(new SatoraSettleResult(SatoraSettleOutcome.NothingToSettle, "The wallet had no recoverable funds to settle."), await _service.SettleAsync(Store, cancellationToken: Ct));
    }

    // A settle spends only recoverable VTXOs and pays only the wallet, which changes nothing an
    // unresolved withdrawal's outcome depends on, while recoverable funds left alone cross expiry.
    [Fact]
    public async Task TheWorkerSettlesWhileAWithdrawalIsUnresolved()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_200);
        _withdrawals.Rows["earlier"] = Row("earlier", SatoraWithdrawalStatus.Unknown, DateTimeOffset.UtcNow);

        Assert.Equal(SatoraSettleOutcome.Settled, (await _service.SettleAsync(Store, automatic: true, Ct)).Outcome);
        Assert.Equal(["settle (gated)"], _events.All);
        Assert.Equal(["Settled the recoverable funds of the Satora wallet of store store in Arkade batch commitment, automatically"],
            _log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message));
    }

    // The settle is a native call that nothing can interrupt.
    [Fact]
    public async Task TheWorkersSettleStartsNothingOnceBTCPayIsStopping()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_200);
        using var stopping = new CancellationTokenSource();
        await stopping.CancelAsync();

        Assert.Equal(SatoraSettleOutcome.Blocked, (await _service.SettleAsync(Store, automatic: true, stopping.Token)).Outcome);
        Assert.Empty(_events.All);
    }

    // Past its swap's attach a Lightning withdrawal is no longer unresolved, but its request still runs.
    [Fact]
    public async Task DoesNotSettleWhileAWithdrawalIsRunning()
    {
        var fund = new TaskCompletionSource();
        _wallet.FundGate = fund.Task;
        var execute = Execute("alice@example.com", "0.00012345", maxTotalSats: 12_400);

        Assert.Equal(SatoraSettleOutcome.Blocked, (await _service.SettleAsync(Store, cancellationToken: Ct)).Outcome);
        Assert.Equal(SatoraSettleOutcome.Blocked, (await _service.SettleAsync(Store, automatic: true, Ct)).Outcome);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("settle", StringComparison.Ordinal));
        fund.SetResult();
        Assert.Equal(SatoraWithdrawResultKind.Pending, (await execute).Kind);
        Assert.Equal(SatoraSettleOutcome.NothingToSettle, (await _service.SettleAsync(Store, cancellationToken: Ct)).Outcome);
        Assert.Single(_events.All, e => e.StartsWith("settle", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASettleThatFailsSaysWhy()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 10_200);
        _wallet.SettleError = new SdkException(SdkErrorKind.Timeout, "batch timed out");

        var result = await _service.SettleAsync(Store, cancellationToken: Ct);

        Assert.Equal(SatoraSettleOutcome.Failed, result.Outcome);
        Assert.Contains("batch timed out", result.Message);
    }

    // ark-client refuses to settle recoverable funds below dust; any other Transport error may have
    // left a batch running.
    [Theory]
    [InlineData(SubDust, SatoraSettleOutcome.TooSmall, "The recoverable funds are too small to settle on their own. They can be settled once more funds become recoverable.")]
    [InlineData("settle: Failed to join batch: connection reset", SatoraSettleOutcome.Failed, "Settling the wallet's recoverable funds failed (settle: Failed to join batch: connection reset). The balance shows whether the batch went through.")]
    public async Task ASettleOfFundsBelowDustSaysTheyCanBeSettledLater(string error, SatoraSettleOutcome outcome, string expected)
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 200);
        _wallet.SettleError = new SdkException(SdkErrorKind.Transport, error);

        var result = await _service.SettleAsync(Store, cancellationToken: Ct);

        Assert.Equal((outcome, expected), (result.Outcome, result.Message));
        Assert.Equal(outcome == SatoraSettleOutcome.Failed ? error : null, result.Error);
        Assert.Equal(["settle (gated)"], _events.All);
    }

    // Expected of the change a send leaves below dust, and tried again at each BTCPay start and each
    // press of the button, so one plain line without the SDK's stack trace.
    [Fact]
    public async Task ASettleOfFundsBelowDustLogsOnePlainLine()
    {
        _wallet.Balance = new ArkadeBalance(90_000, 0, 200);
        _wallet.SettleError = new SdkException(SdkErrorKind.Transport, SubDust);

        await _service.SettleAsync(Store, cancellationToken: Ct);

        Assert.Equal([(LogLevel.Information,
                $"The recoverable funds of the Satora wallet of store store are below dust, and are settled once more funds become recoverable ({SubDust})")],
            _log.Entries);
        Assert.Null(Assert.Single(_log.Errors));
    }

    // An attempted payment past its refund locktime, with its Lightning withdrawal.
    private void UnfinishedSwap(string swapId = "swap", string withdrawalId = "lightning")
    {
        var swap = MemoryOutgoingSwaps.Copy(SatoraLightningPayments.ToRow(FakeWallet.ArkadeToLightning(swapId), Store, Network.Main, SatoraLightningCreditTests.PaymentHash, DateTimeOffset.UtcNow));
        swap.FundingAttempted = true;
        swap.Status = SatoraSwapStatus.ServerFunded;
        _swaps.Rows[swapId] = swap;
        var withdrawal = Row(withdrawalId, SatoraWithdrawalStatus.Pending, DateTimeOffset.UtcNow);
        withdrawal.SwapId = swapId;
        _withdrawals.Rows[withdrawalId] = withdrawal;
    }

    // What the swap server answers when asked for a second swap for an invoice.
    private static SdkException SwapExists() => new(SdkErrorKind.Api, "a swap with this payment hash exists already");

    // An earlier Lightning invoice withdrawal, as the store's first try left it: its swap "swap", open,
    // awaiting funding and never attempted unless change says otherwise, and its withdrawal "earlier".
    private void EarlierPayment(string invoice, SatoraWithdrawalStatus status, Action<SatoraLightningSwap>? change = null)
    {
        var paymentHash = BOLT11PaymentRequest.Parse(invoice, Network.Main).PaymentHash!.ToString();
        var swap = SatoraLightningPayments.ToRow(FakeWallet.ArkadeToLightning("swap", bolt11: invoice), Store, Network.Main, paymentHash, DateTimeOffset.UtcNow);
        change?.Invoke(swap);
        _swaps.Rows["swap"] = swap;
        var withdrawal = Row("earlier", status, DateTimeOffset.UtcNow);
        (withdrawal.Type, withdrawal.Destination, withdrawal.SwapId) = (SatoraWithdrawalType.LightningInvoice, invoice, "swap");
        _withdrawals.Rows["earlier"] = withdrawal;
    }

    // The swap server's quote for paying an invoice: 0.3% and the given Lightning fee.
    private static LightningSendQuote QuoteOf(SdkLightningDestination destination, long networkFeeSats)
    {
        var sats = BOLT11PaymentRequest.Parse(((SdkLightningDestination.Invoice)destination).invoice, Network.Main).MinimumAmount.MilliSatoshi / 1000;
        var protocolFee = (long)Math.Ceiling(sats * 0.003m);
        var total = sats + protocolFee + networkFeeSats;
        return new LightningSendQuote("1", 1_000_000, 1_000, (ulong)networkFeeSats, 0.003, (ulong)protocolFee, total.ToString(), (ulong)total, (ulong)sats);
    }

    private Task<SatoraWithdrawResult> Execute(string destination, string? amountBtc, bool sendAll = false, long? maxTotalSats = null,
        string key = "key", long? maxFeeSats = null) =>
        _service.ExecuteAsync(Store, new SatoraWithdrawRequest
        {
            Destination = destination,
            AmountBtc = amountBtc,
            SendAll = sendAll,
            Key = key,
            MaxTotalSats = maxTotalSats,
            MaxFeeSats = maxFeeSats
        });

    private Task<SatoraWithdrawReview> Review(string destination, string amountBtc) =>
        _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = destination, AmountBtc = amountBtc });

    private static SatoraWithdrawal Row(string id, SatoraWithdrawalStatus status, DateTimeOffset createdAt) => new()
    {
        Id = id,
        StoreId = Store,
        Type = SatoraWithdrawalType.Arkade,
        Destination = Ark,
        AmountSats = 1_000,
        Status = status,
        CreatedAt = createdAt,
        UpdatedAt = createdAt
    };
}
