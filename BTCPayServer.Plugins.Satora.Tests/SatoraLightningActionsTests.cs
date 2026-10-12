using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using Xunit;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraLightningActionsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(SatoraSwapStatus.Pending, SatoraLightningAction.Wait)]
    [InlineData(SatoraSwapStatus.ClientFundingSeen, SatoraLightningAction.Wait)]
    [InlineData(SatoraSwapStatus.ClientFunded, SatoraLightningAction.Wait)]
    [InlineData(SatoraSwapStatus.ServerFunded, SatoraLightningAction.Claim)]
    [InlineData(SatoraSwapStatus.ClientRedeeming, SatoraLightningAction.Claim)]
    [InlineData(SatoraSwapStatus.ClientRedeemed, SatoraLightningAction.Claim)]
    [InlineData(SatoraSwapStatus.ServerRedeemed, SatoraLightningAction.Claim)]
    [InlineData(SatoraSwapStatus.Expired, SatoraLightningAction.Complete)]
    [InlineData(SatoraSwapStatus.ServerWontFund, SatoraLightningAction.Complete)]
    [InlineData(SatoraSwapStatus.ClientFundedServerRefunded, SatoraLightningAction.CompleteRefunded)]
    [InlineData(SatoraSwapStatus.ClientRefunded, SatoraLightningAction.Alert)]
    [InlineData(SatoraSwapStatus.ClientInvalidFunded, SatoraLightningAction.Alert)]
    [InlineData(SatoraSwapStatus.ClientFundedTooLate, SatoraLightningAction.Alert)]
    [InlineData(SatoraSwapStatus.ClientRefundedServerFunded, SatoraLightningAction.Alert)]
    [InlineData(SatoraSwapStatus.ClientRefundedServerRefunded, SatoraLightningAction.Alert)]
    [InlineData(SatoraSwapStatus.ClientRedeemedAndClientRefunded, SatoraLightningAction.Alert)]
    [InlineData(SatoraSwapStatus.Unknown, SatoraLightningAction.Alert)]
    public void ActsOnEachStatusOfAnOpenSwap(SatoraSwapStatus status, SatoraLightningAction expected)
    {
        Assert.Equal(expected, SatoraLightningActions.Decide(Swap(), status, Now));
    }

    [Theory]
    [InlineData(SatoraSwapStatus.ServerFunded)]
    [InlineData(SatoraSwapStatus.ClientRedeeming)]
    [InlineData(SatoraSwapStatus.ClientRedeemed)]
    [InlineData(SatoraSwapStatus.ServerRedeemed)]
    public void DoesNotClaimASwapMarkedDoNotClaimOrPastItsCutoff(SatoraSwapStatus status)
    {
        Assert.Equal(SatoraLightningAction.Wait, SatoraLightningActions.Decide(Swap(doNotClaim: true), status, Now));
        Assert.NotEqual(SatoraLightningAction.Claim, SatoraLightningActions.Decide(Swap(cutoff: Now), status, Now));
        Assert.Equal(SatoraLightningAction.Claim, SatoraLightningActions.Decide(Swap(cutoff: Now.AddSeconds(1)), status, Now));
    }

    // Past the cutoff the SDK starts no new claim, but finishes one it journaled before, until the
    // server can take the VHTLC back.
    [Theory]
    [InlineData(SatoraSwapStatus.ServerFunded)]
    [InlineData(SatoraSwapStatus.ClientRedeeming)]
    public void TriesToFinishAClaimPastItsCutoffUntilTheVhtlcLocktimeUnlessMarkedDoNotClaim(SatoraSwapStatus status)
    {
        var locktime = Now + SatoraClaimWindow.ClaimMargin;

        Assert.Equal(SatoraLightningAction.FinishClaim, SatoraLightningActions.Decide(Swap(cutoff: Now), status, Now));
        Assert.Equal(SatoraLightningAction.FinishClaim, SatoraLightningActions.Decide(Swap(cutoff: Now), status, locktime.AddSeconds(-1)));
        Assert.Equal(SatoraLightningAction.Wait, SatoraLightningActions.Decide(Swap(cutoff: Now), status, locktime));
        Assert.Equal(SatoraLightningAction.Wait, SatoraLightningActions.Decide(Swap(doNotClaim: true, cutoff: Now), status, Now));
    }

    // The server's word that a swap was redeemed is not this wallet's receipt for it.
    [Theory]
    [InlineData(SatoraSwapStatus.ClientRedeemed)]
    [InlineData(SatoraSwapStatus.ServerRedeemed)]
    public void NeverCreditsARedeemedSwapWithoutTheWalletsReceipt(SatoraSwapStatus status)
    {
        Assert.Equal(SatoraLightningAction.CompleteUnverified, SatoraLightningActions.Decide(Swap(cutoff: Now), status, Now));
        Assert.Equal(SatoraLightningAction.CompleteUnverified, SatoraLightningActions.Decide(Swap(doNotClaim: true, cutoff: Now.AddHours(-1)), status, Now));
        foreach (var swap in new[] { Swap(), Swap(doNotClaim: true), Swap(cutoff: Now) })
            Assert.NotEqual(SatoraLightningAction.Credit, SatoraLightningActions.Decide(swap, status, Now));
    }

    // Once claimed, the receipt decides whatever the server reports, and a credited swap is credited only once.
    [Theory]
    [InlineData(SatoraSwapStatus.ClientRedeemed)]
    [InlineData(SatoraSwapStatus.ServerFunded)]
    [InlineData(SatoraSwapStatus.ClientFundedServerRefunded)]
    [InlineData(SatoraSwapStatus.Unknown)]
    public void CreditsAClaimedSwapOnce(SatoraSwapStatus status)
    {
        Assert.Equal(SatoraLightningAction.Credit, SatoraLightningActions.Decide(Swap(claimTxId: "ark-txid", doNotClaim: true, cutoff: Now.AddHours(-1)), status, Now));
        Assert.Equal(SatoraLightningAction.Complete, SatoraLightningActions.Decide(Swap(claimTxId: "ark-txid", credited: true), status, Now));
    }

    // A swap BTCPay asked not to claim was refunded as intended.
    [Fact]
    public void TellsTheStoreOnlyAboutARefundedSwapItMeantToClaim()
    {
        Assert.Equal(SatoraLightningAction.CompleteRefunded, SatoraLightningActions.Decide(Swap(cutoff: Now.AddHours(-1)), SatoraSwapStatus.ClientFundedServerRefunded, Now));
        Assert.Equal(SatoraLightningAction.Complete, SatoraLightningActions.Decide(Swap(doNotClaim: true), SatoraSwapStatus.ClientFundedServerRefunded, Now));
    }

    [Fact]
    public void WarnsAboutAPaidUnclaimedSwapFromTenMinutesBeforeItsCutoff()
    {
        var cutoff = Now.AddMinutes(10);
        Assert.False(SatoraLightningActions.NearCutoff(Swap(cutoff: cutoff.AddSeconds(1)), SatoraSwapStatus.ServerFunded, Now));
        Assert.True(SatoraLightningActions.NearCutoff(Swap(cutoff: cutoff), SatoraSwapStatus.ServerFunded, Now));
        Assert.True(SatoraLightningActions.NearCutoff(Swap(cutoff: Now.AddMinutes(-5)), SatoraSwapStatus.ClientRedeeming, Now));
        Assert.False(SatoraLightningActions.NearCutoff(Swap(cutoff: cutoff, doNotClaim: true), SatoraSwapStatus.ServerFunded, Now));
        Assert.False(SatoraLightningActions.NearCutoff(Swap(cutoff: cutoff), SatoraSwapStatus.Pending, Now));
    }

    [Fact]
    public void ReportsUnpaidUntilCreditedAndExpiredOnceNoClaimCanStart()
    {
        var open = SatoraLightningActions.ToInvoice(Swap(status: SatoraSwapStatus.ClientRedeemed), Now);
        Assert.Equal(LightningInvoiceStatus.Unpaid, open.Status);
        Assert.Null(open.AmountReceived);
        Assert.Null(open.PaidAt);

        Assert.Equal(LightningInvoiceStatus.Expired, SatoraLightningActions.ToInvoice(Swap(cutoff: Now), Now).Status);
        Assert.Equal(LightningInvoiceStatus.Expired, SatoraLightningActions.ToInvoice(Swap(completed: true), Now).Status);
    }

    [Fact]
    public void ReportsACreditedSwapPaidWithTheBolt11Amount()
    {
        var swap = Swap(credited: true, cutoff: Now.AddHours(-1));
        swap.PaidAt = Now.AddMinutes(-2);

        var invoice = SatoraLightningActions.ToInvoice(swap, Now);

        Assert.Equal(LightningInvoiceStatus.Paid, invoice.Status);
        Assert.Equal(LightMoney.MilliSatoshis(12_345_000), invoice.Amount);
        Assert.Equal(invoice.Amount, invoice.AmountReceived);
        Assert.Equal(swap.PaidAt, invoice.PaidAt);
        Assert.Equal((swap.SwapId, swap.PaymentHash, swap.Bolt11, swap.ExpiresAt), (invoice.Id, invoice.PaymentHash, invoice.BOLT11, invoice.ExpiresAt));
    }

    [Fact]
    public void PollsEveryFiveSecondsWhileAnInvoiceCanStillBeClaimed()
    {
        Assert.Equal(SatoraLightningWorker.ActiveInterval, SatoraLightningWorker.Interval([Now.AddMinutes(-1), Now.AddSeconds(1)], Now));
        Assert.Equal(SatoraLightningWorker.IdleInterval, SatoraLightningWorker.Interval([Now.AddMinutes(-1), Now], Now));
        Assert.Equal(TimeSpan.FromSeconds(5), SatoraLightningWorker.ActiveInterval);
        Assert.Equal(TimeSpan.FromSeconds(30), SatoraLightningWorker.IdleInterval);
    }

    // Every SDK status keeps its name in the database; only SwapStatus.Other becomes Unknown.
    [Fact]
    public void StoresEverySdkStatusUnderItsOwnName()
    {
        var variants = typeof(SdkSwapStatus).GetNestedTypes().Where(t => t.IsSubclassOf(typeof(SdkSwapStatus)) && t.Name != nameof(SdkSwapStatus.Other)).ToList();
        Assert.Equal(Enum.GetValues<SatoraSwapStatus>().Length - 1, variants.Count);
        foreach (var variant in variants)
            Assert.Equal(variant.Name, SatoraSwapStatuses.From((SdkSwapStatus)Activator.CreateInstance(variant)!).ToString());
        Assert.Equal(SatoraSwapStatus.Unknown, SatoraSwapStatuses.From(new SdkSwapStatus.Other("SomethingNew")));
    }

    private static SatoraLightningSwap Swap(
        bool doNotClaim = false,
        bool credited = false,
        bool completed = false,
        DateTimeOffset? cutoff = null,
        SatoraSwapStatus status = SatoraSwapStatus.Pending,
        string? claimTxId = null) => new()
    {
        SwapId = "swap",
        StoreId = "store",
        Direction = SatoraLightningDirection.LightningToArkade,
        Status = status,
        PaymentHash = new string('a', 64),
        Bolt11 = "lnbc",
        AmountMsat = 12_345_000,
        ClaimCutoff = cutoff ?? Now.AddMinutes(20),
        ExpiresAt = cutoff ?? Now.AddMinutes(20),
        DoNotClaim = doNotClaim,
        Credited = credited,
        ClaimTxId = claimTxId,
        CreatedAt = Now.AddMinutes(-5),
        CompletedAt = completed || credited ? Now : null
    };
}
