using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Models;
using NBitcoin;
using Xunit;
using Action = BTCPayServer.Plugins.Satora.Chain.SatoraChainAction;
using Status = BTCPayServer.Plugins.Satora.Models.SatoraSwapStatus;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraChainActionsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // Every status the SDK names, for a swap still within its claim window with nothing recorded.
    public static TheoryData<Status, Action> Unclaimed => new()
    {
        { Status.Pending, Action.Wait },
        { Status.ClientFundingSeen, Action.CheckFunding },
        { Status.ClientFunded, Action.CheckFunding },
        { Status.ServerFunded, Action.Claim },
        { Status.ClientRedeeming, Action.Claim },
        { Status.ClientRedeemed, Action.Claim },
        { Status.ServerRedeemed, Action.Claim },
        { Status.ClientInvalidFunded, Action.NeedsRefund },
        { Status.ServerWontFund, Action.NeedsRefund },
        { Status.ClientFundedServerRefunded, Action.NeedsRefund },
        { Status.Expired, Action.Close },
        // Never set for this direction, or not named by this SDK: move nothing.
        { Status.ClientRefunded, Action.Alert },
        { Status.ClientRefundedServerFunded, Action.Alert },
        { Status.ClientRefundedServerRefunded, Action.Alert },
        { Status.ClientFundedTooLate, Action.Alert },
        { Status.ClientRedeemedAndClientRefunded, Action.Alert },
        { Status.Unknown, Action.Alert }
    };

    [Fact]
    public void TheTableCoversEveryStatus()
    {
        Assert.Equal(Enum.GetValues<Status>().Order(), Unclaimed.Select(row => row.Data.Item1).Order());
    }

    [Theory]
    [MemberData(nameof(Unclaimed))]
    public void DecidesEveryStatusOfAnUnclaimedSwap(Status status, Action expected)
    {
        Assert.Equal(expected, SatoraChainActions.Decide(Swap(), status, Now));
    }

    // A mempool output can still be replaced or dropped; a confirmed one cannot.
    [Fact]
    public void ARecordedFundingIsReadAgainOnlyUntilItConfirms()
    {
        Assert.Equal(Action.CheckFunding, SatoraChainActions.Decide(Swap(paymentRecorded: true), Status.ClientFundingSeen, Now));
        Assert.Equal(Action.Wait, SatoraChainActions.Decide(Swap(paymentRecorded: true), Status.ClientFunded, Now));
    }

    // The server stops watching it at the VHTLC locktime and funds no VHTLC after it, so it would wait for good.
    [Theory]
    [InlineData(Status.ClientFundingSeen, Action.CheckFunding)]
    [InlineData(Status.ClientFunded, Action.Wait)]
    public void AFundingTheServerHasNotTakenByTheVhtlcLocktimeGoesBack(Status status, Action before)
    {
        var swap = Swap(vhtlcLocktime: Now, paymentRecorded: true);

        Assert.Equal(Action.FundingTooLate, SatoraChainActions.Decide(swap, status, Now));
        Assert.Equal(before, SatoraChainActions.Decide(swap, status, Now.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(Status.ServerFunded, Action.FinishClaim)]
    [InlineData(Status.ClientRedeeming, Action.FinishClaim)]
    [InlineData(Status.ClientRedeemed, Action.ClaimUnverified)]
    [InlineData(Status.ServerRedeemed, Action.ClaimUnverified)]
    public void PastTheClaimCutoffNothingNewIsClaimed(Status status, Action expected)
    {
        // The cutoff is the SDK's claim margin before the VHTLC locktime.
        var swap = Swap(vhtlcLocktime: Now.AddSeconds(299));

        Assert.Equal(expected, SatoraChainActions.Decide(swap, status, Now));
    }

    // Once the server can take the VHTLC back, the worker asks the SDK whether a claim can still complete.
    [Theory]
    [InlineData(Status.ServerFunded)]
    [InlineData(Status.ClientRedeeming)]
    public void FromTheVhtlcLocktimeAClaimIsFinishedOnlyIfItCanStillComplete(Status status)
    {
        var swap = Swap(vhtlcLocktime: Now);

        Assert.Equal(Action.FinishClaim, SatoraChainActions.Decide(swap, status, Now.AddSeconds(-1)));
        Assert.Equal(Action.FinishClaimOrRefund, SatoraChainActions.Decide(swap, status, Now));
    }

    // Only the wallet's own receipt settles a payment, and a settled swap closes once the server swept its output.
    [Theory]
    [MemberData(nameof(Unclaimed))]
    public void AClaimedSwapIsCreditedOnItsReceiptWhateverTheServerReports(Status status, Action _)
    {
        Assert.Equal(Action.Credit, SatoraChainActions.Decide(Swap(claimTxId: "ark"), status, Now));
        Assert.Equal(status == Status.ServerRedeemed ? Action.Close : Action.Wait,
            SatoraChainActions.Decide(Swap(claimTxId: "ark", credited: true), status, Now));
    }

    [Fact]
    public void WarnsAboutAFundedSwapTenMinutesBeforeItsCutoff()
    {
        var swap = Swap(vhtlcLocktime: Now + TimeSpan.FromMinutes(15));

        Assert.True(SatoraChainActions.NearCutoff(swap, Status.ServerFunded, Now));
        Assert.False(SatoraChainActions.NearCutoff(swap, Status.ServerFunded, Now - TimeSpan.FromMinutes(1)));
        // Confirmed, but the server has not funded the VHTLC yet.
        Assert.True(SatoraChainActions.NearCutoff(swap, Status.ClientFunded, Now));
        Assert.False(SatoraChainActions.NearCutoff(swap, Status.ClientFundingSeen, Now));
        Assert.False(SatoraChainActions.NearCutoff(Swap(vhtlcLocktime: swap.VhtlcRefundLocktime, claimTxId: "ark"), Status.ServerFunded, Now));
    }

    // On mainnet a payment made at the invoice's expiry has two hours to confirm before the claim cutoff.
    [Fact]
    public void AnInvoiceMustExpireEarlyEnoughForItsLastPaymentToConfirmBeforeTheCutoff()
    {
        var cutoff = Now.AddHours(12);

        Assert.True(SatoraChainActions.ExpiryFits(cutoff.AddHours(-2), cutoff, Network.Main));
        Assert.False(SatoraChainActions.ExpiryFits(cutoff.AddHours(-2).AddSeconds(1), cutoff, Network.Main));
        Assert.True(SatoraChainActions.ExpiryFits(cutoff.AddMinutes(-5), cutoff, Network.RegTest));
        Assert.False(SatoraChainActions.ExpiryFits(cutoff.AddMinutes(-4), cutoff, Network.RegTest));
    }

    // Mainnet's 12 hour window less the 5 minute claim margin and the 2 hour allowance, before any swap
    // exists; other networks wait for the created swap's own window.
    [Fact]
    public void OnMainnetAnInvoiceExpiringBeyondTheKnownWindowIsRefusedBeforeASwapExists()
    {
        var limit = Now + TimeSpan.FromHours(12) - TimeSpan.FromMinutes(5) - TimeSpan.FromHours(2);

        Assert.True(SatoraChainActions.ExpiryFitsKnownWindow(limit, Now, Network.Main));
        Assert.False(SatoraChainActions.ExpiryFitsKnownWindow(limit.AddSeconds(1), Now, Network.Main));
        Assert.True(SatoraChainActions.ExpiryFitsKnownWindow(Now.AddDays(7), Now, Network.RegTest));
        Assert.True(SatoraChainActions.ExpiryFitsKnownWindow(Now.AddDays(7), Now, Network.TestNet));
    }

    [Fact]
    public void AnExpiredSwapsAddressIsReadUntilAnHourAfterTheInvoiceExpired()
    {
        var swap = Swap();

        Assert.True(SatoraChainActions.LatePaymentPossible(swap, swap.InvoiceExpiresAt.AddMinutes(59)));
        Assert.False(SatoraChainActions.LatePaymentPossible(swap, swap.InvoiceExpiresAt.AddHours(1)));
    }

    // A refund of 300 sats in fees leaves an output of 845 sats with 545, below the largest dust limit.
    [Theory]
    [InlineData(new long[0], true)]
    [InlineData(new long[] { 845 }, true)]
    [InlineData(new long[] { 845, 846 }, false)]
    [InlineData(new long[] { 100_000 }, false)]
    public void NothingIsLeftToRefundOnceEveryOutputIsWorthLessThanItsRefund(long[] values, bool nothing)
    {
        var outputs = values.Select((v, i) => new SatoraHtlcOutput($"tx{i}", 0, v, true));

        Assert.Equal(nothing, SatoraChainActions.NothingToRefund(outputs, 300));
    }

    public static TheoryData<long[], long?> Outputs => new()
    {
        { [], null },
        // Underpaid by one sat.
        { [99_999], null },
        { [100_000], 100_000 },
        // The server pays out only the swap's amount; the merchant owes nothing for the excess.
        { [150_000], 150_000 },
        // Two payments never add up: the server takes one output of at least the deposit.
        { [60_000, 40_000], null },
        { [1_000, 100_000], 100_000 }
    };

    [Theory]
    [MemberData(nameof(Outputs))]
    public void OnlyASingleOutputOfAtLeastTheDepositFundsTheSwap(long[] values, long? expected)
    {
        var outputs = values.Select((v, i) => new SatoraHtlcOutput($"tx{i}", (uint)i, v, false));

        Assert.Equal(expected, SatoraChainActions.Funding(outputs, 100_000)?.ValueSats);
    }

    // The first output as mutinynet.com/api/address/:address/utxo returned it, and a mempool one.
    [Fact]
    public void ReadsEsploraOutputs()
    {
        const string body = """
            [{"txid":"87ee7a18c708aea7d4fc48001b808e768fc7345c6bf5699a76be09019f141bc3","vout":0,"status":{"confirmed":true,"block_height":3367763,"block_hash":"0000035d28884d0a493b9166ad413bbef5318c194b2271a2de7b3b19ff24ddde","block_time":1787497544},"value":20534},
             {"txid":"cc","vout":1,"status":{"confirmed":false},"value":546}]
            """;

        var outputs = SatoraChainWallets.ParseUtxos(System.Text.Json.JsonSerializer.Deserialize<List<SatoraChainWallets.EsploraUtxo>>(body));

        Assert.Equal([
            new SatoraHtlcOutput("87ee7a18c708aea7d4fc48001b808e768fc7345c6bf5699a76be09019f141bc3", 0, 20_534, true),
            new SatoraHtlcOutput("cc", 1, 546, false)
        ], outputs);
        Assert.Throws<FormatException>(() => SatoraChainWallets.ParseUtxos(null));
    }

    // The regtest stack's localhost:3000/api/address/:address/txs, cut to the fields read, after a
    // funding of the address and a spend of it both entered the mempool; then the spend confirmed.
    [Fact]
    public void FindsASpendOfTheAddressThatMayBeARefund()
    {
        const string address = "bcrt1psumgl5ynhd54d2hye93ljry3yt6tpzt0q8njyhddt8scc0a0238qczuwlw";
        const string funding = """
            {"txid":"293816f27fabad9da8e235536dd47a8a305e78944811e27c0f539bd0dd637849","vin":[{"is_coinbase":false,"prevout":{"value":227128,"scriptpubkey":"0014ca6c63f4a73556f75528fcd4277f093e2526386b","scriptpubkey_address":"bcrt1qefkx8a98x4t0w4fgln2zwlcf8cjjvwrtnq7qke","scriptpubkey_type":"v0_p2wpkh"},"txid":"b4c2bfa59cd87e5dab01c2014edad0cb38e2d0f1352aa5eee449876649e50ae6","vout":0}],"vout":[{"value":100000,"scriptpubkey_address":"bcrt1psumgl5ynhd54d2hye93ljry3yt6tpzt0q8njyhddt8scc0a0238qczuwlw","scriptpubkey_type":"v1_p2tr"}],"status":{"confirmed":false}}
            """;
        const string spend = """
            {"txid":"852d338bf578ee4406b075c9f68bb62b3670c2383161f24e667be3905b630543","vin":[{"is_coinbase":false,"prevout":{"value":100000,"scriptpubkey":"512087368fd093bb6956aae4c963f90c9122f4b0896f01e7225dad59e18c3faf544e","scriptpubkey_address":"bcrt1psumgl5ynhd54d2hye93ljry3yt6tpzt0q8njyhddt8scc0a0238qczuwlw","scriptpubkey_type":"v1_p2tr"},"txid":"293816f27fabad9da8e235536dd47a8a305e78944811e27c0f539bd0dd637849","vout":1}],"status":{"confirmed":false}}
            """;
        static List<SatoraChainWallets.EsploraTx>? Txs(params string[] txs) =>
            System.Text.Json.JsonSerializer.Deserialize<List<SatoraChainWallets.EsploraTx>>($"[{string.Join(',', txs)}]");

        const string refundTo = "bcrt1qefkx8a98x4t0w4fgln2zwlcf8cjjvwrtnq7qke";
        var confirmed = spend.Replace("\"confirmed\":false", "\"confirmed\":true", StringComparison.Ordinal);
        var confirmedRefund = confirmed.Replace("\"status\"", $"\"vout\":[{{\"value\":99700,\"scriptpubkey_address\":\"{refundTo}\"}}],\"status\"", StringComparison.Ordinal);

        Assert.True(SatoraChainWallets.PossibleRefund(Txs(spend, funding), address, null));
        Assert.True(SatoraChainWallets.PossibleRefund(Txs(spend, funding), address, refundTo));
        Assert.False(SatoraChainWallets.PossibleRefund(Txs(funding), address, refundTo));
        Assert.False(SatoraChainWallets.PossibleRefund(Txs(confirmed), address, refundTo));
        Assert.True(SatoraChainWallets.PossibleRefund(Txs(confirmedRefund), address, refundTo));
        Assert.False(SatoraChainWallets.PossibleRefund(Txs(confirmedRefund), address, null));
        Assert.False(SatoraChainWallets.PossibleRefund(Txs(confirmedRefund), address, address));
        Assert.False(SatoraChainWallets.PossibleRefund(Txs(), address, refundTo));
        Assert.Throws<FormatException>(() => SatoraChainWallets.PossibleRefund(null, address, refundTo));
    }

    [Fact]
    public void ARefundIsDueOnceRequestedAndPastTheHtlcLocktime()
    {
        var locktime = Now.AddHours(20);
        var requested = Swap(btcLocktime: locktime, needsRefund: true, refundAddress: "bcrt1q...");

        Assert.False(SatoraChainActions.RefundDue(requested, locktime.AddSeconds(-1)));
        Assert.True(SatoraChainActions.RefundDue(requested, locktime));
        Assert.False(SatoraChainActions.RefundDue(Swap(btcLocktime: locktime, needsRefund: true), locktime));
        Assert.False(SatoraChainActions.RefundDue(Swap(btcLocktime: locktime, refundAddress: "bcrt1q..."), locktime));
        // Median time past trails the clock, often by more than an hour, so a refused attempt counts as a failure only from then.
        Assert.Equal(locktime.AddHours(2), SatoraChainActions.RefundPossibleFrom(requested));
    }

    [Fact]
    public void ARefundGoesOnlyToAnAddressOnTheStoresNetworkWithTheTypedConfirmation()
    {
        var regtest = Network.RegTest;
        var address = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, regtest).ToString();
        var mainnetAddress = new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString();
        var swap = Swap(needsRefund: true);

        Assert.Equal(SatoraChainRefundRefusal.None, SatoraChainRefunds.Check(swap, address, regtest, "REFUND"));
        Assert.Equal(SatoraChainRefundRefusal.None, SatoraChainRefunds.Check(swap, $" {address} ", regtest, " REFUND "));
        Assert.Equal(SatoraChainRefundRefusal.InvalidAddress, SatoraChainRefunds.Check(swap, mainnetAddress, regtest, "REFUND"));
        Assert.Equal(SatoraChainRefundRefusal.InvalidAddress, SatoraChainRefunds.Check(swap, "not an address", regtest, "REFUND"));
        Assert.Equal(SatoraChainRefundRefusal.InvalidAddress, SatoraChainRefunds.Check(swap, null, regtest, "REFUND"));
        Assert.Equal(SatoraChainRefundRefusal.ConfirmationRequired, SatoraChainRefunds.Check(swap, address, regtest, "refund"));
        Assert.Equal(SatoraChainRefundRefusal.NotFound, SatoraChainRefunds.Check(null, address, regtest, "REFUND"));
        Assert.Equal(SatoraChainRefundRefusal.NotNeeded, SatoraChainRefunds.Check(Swap(), address, regtest, "REFUND"));
        var closed = Swap(needsRefund: true);
        closed.CompletedAt = Now;
        Assert.Equal(SatoraChainRefundRefusal.NotNeeded, SatoraChainRefunds.Check(closed, address, regtest, "REFUND"));
        var started = Swap(needsRefund: true, refundAddress: address);
        started.RefundAttempted = true;
        Assert.Equal(SatoraChainRefundRefusal.AlreadyStarted, SatoraChainRefunds.Check(started, address, regtest, "REFUND"));
    }

    [Fact]
    public void DismissesOnlyAnOpenRefundWithTheTypedConfirmation()
    {
        var swap = Swap(needsRefund: true);

        Assert.Equal(SatoraChainRefundRefusal.None, SatoraChainRefunds.CheckDismiss(swap, " DISMISS "));
        Assert.Equal(SatoraChainRefundRefusal.ConfirmationRequired, SatoraChainRefunds.CheckDismiss(swap, "REFUND"));
        Assert.Equal(SatoraChainRefundRefusal.NotFound, SatoraChainRefunds.CheckDismiss(null, "DISMISS"));
        Assert.Equal(SatoraChainRefundRefusal.NotNeeded, SatoraChainRefunds.CheckDismiss(Swap(), "DISMISS"));
        // A refund that went out can still be dismissed, so a remainder it could not sweep stops being followed.
        var refunded = Swap(needsRefund: true, refundAddress: "bcrt1q...");
        refunded.RefundTxId = "refund";
        Assert.Equal(SatoraChainRefundRefusal.None, SatoraChainRefunds.CheckDismiss(refunded, "DISMISS"));
        refunded.CompletedAt = Now;
        Assert.Equal(SatoraChainRefundRefusal.NotNeeded, SatoraChainRefunds.CheckDismiss(refunded, "DISMISS"));
    }

    [Fact]
    public void TellsTheOwnerWhyAPaymentGoesBack()
    {
        Assert.Equal("Underpaid", SatoraChainRefunds.Describe(nameof(Status.ClientInvalidFunded)));
        Assert.Equal("Paid after the swap ended", SatoraChainRefunds.Describe(SatoraChainActions.LeftoverReason));
        Assert.Equal("Not claimed in time", SatoraChainRefunds.Describe(SatoraChainActions.UnclaimedReason));
        Assert.Equal("Not funded by the swap server in time", SatoraChainRefunds.Describe(nameof(Status.ClientFunded)));
        Assert.Equal("Something new", SatoraChainRefunds.Describe("Something new"));
    }

    internal static SatoraChainSwap Swap(
        DateTimeOffset? vhtlcLocktime = null,
        DateTimeOffset? btcLocktime = null,
        bool paymentRecorded = false,
        string? claimTxId = null,
        bool credited = false,
        bool needsRefund = false,
        string? refundAddress = null,
        string swapId = "swap",
        string storeId = "store") => new()
    {
        SwapId = swapId,
        StoreId = storeId,
        InvoiceId = "invoice-" + swapId,
        Status = Status.Pending,
        HtlcAddress = "htlc-" + swapId,
        DepositSats = 100_000,
        ReceiveSats = 99_500,
        VhtlcRefundLocktime = vhtlcLocktime ?? Now.AddHours(12),
        BtcRefundLocktime = btcLocktime ?? Now.AddHours(24),
        InvoiceExpiresAt = Now.AddMinutes(15),
        PaymentRecorded = paymentRecorded || credited,
        ClaimTxId = claimTxId,
        Credited = credited,
        NeedsRefundAt = needsRefund ? Now : null,
        NeedsRefundReason = needsRefund ? nameof(Status.ClientInvalidFunded) : null,
        RefundAddress = refundAddress,
        CreatedAt = Now
    };
}
