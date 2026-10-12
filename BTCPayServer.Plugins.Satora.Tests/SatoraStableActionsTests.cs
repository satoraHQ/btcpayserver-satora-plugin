using System.Numerics;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Stable;
using Xunit;
using Action = BTCPayServer.Plugins.Satora.Stable.SatoraStableAction;
using Relay = BTCPayServer.Plugins.Satora.Stable.SatoraRelayState;
using Release = BTCPayServer.Plugins.Satora.Stable.SatoraStableRelease;
using Status = BTCPayServer.Plugins.Satora.Models.SatoraSwapStatus;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraStableActionsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // Every status the SDK names, for a swap before its funding cutoff with nothing recorded.
    public static TheoryData<Status, Action> Unclaimed => new()
    {
        { Status.Pending, Action.Fund },
        { Status.ClientFundingSeen, Action.RecordFunding },
        { Status.ClientFunded, Action.RecordFunding },
        { Status.ServerFunded, Action.Claim },
        { Status.ClientRedeeming, Action.Claim },
        { Status.ClientRedeemed, Action.Claim },
        { Status.ServerRedeemed, Action.Claim },
        // Redeemed, then the HTLC went back to the deposit address: no new claim.
        { Status.ClientRedeemedAndClientRefunded, Action.FinishClaimOrSweep },
        { Status.ClientInvalidFunded, Action.NeedsHtlcRefund },
        { Status.ServerWontFund, Action.NeedsHtlcRefund },
        { Status.ClientFundedTooLate, Action.NeedsHtlcRefund },
        // The server's word that it took the VHTLC back is checked against the SDK's.
        { Status.ClientFundedServerRefunded, Action.FinishClaimOrHtlcRefund },
        // After the HTLC's refund, which pays the deposit address.
        { Status.ClientRefunded, Action.NeedsSweep },
        { Status.ClientRefundedServerFunded, Action.NeedsSweep },
        { Status.ClientRefundedServerRefunded, Action.NeedsSweep },
        { Status.Expired, Action.Release },
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
        Assert.Equal(expected, SatoraStableActions.Decide(Swap(), status, Now));
    }

    // No new signature may commit a deposit from the funding cutoff on.
    [Fact]
    public void APendingSwapIsFundedOnlyBeforeItsFundingCutoff()
    {
        var swap = Swap();

        Assert.Equal(Action.Fund, SatoraStableActions.Decide(swap, Status.Pending, swap.FundingStartCutoff.AddSeconds(-1)));
        Assert.Equal(Action.Release, SatoraStableActions.Decide(swap, Status.Pending, swap.FundingStartCutoff));
    }

    // The relay proved the funding; the server has not seen it yet.
    [Fact]
    public void AFundingTheRelayProvedWaitsForTheServer()
    {
        var swap = Swap();
        swap.FundTxHash = "0xfund";

        Assert.Equal(Action.Wait, SatoraStableActions.Decide(swap, Status.Pending, Now));
        Assert.Equal(Action.Wait, SatoraStableActions.Decide(swap, Status.Pending, swap.FundingStartCutoff));
        // Expired with the funding in the HTLC: the release asks the journal and finds it funded.
        Assert.Equal(Action.Release, SatoraStableActions.Decide(swap, Status.Expired, Now));
    }

    [Fact]
    public void ARecordedFundingWaitsForTheServerToFundTheVhtlc()
    {
        var swap = Swap();
        swap.PaymentRecorded = true;

        Assert.Equal(Action.Wait, SatoraStableActions.Decide(swap, Status.ClientFundingSeen, Now));
        Assert.Equal(Action.Wait, SatoraStableActions.Decide(swap, Status.ClientFunded, Now));
    }

    // The server funds no VHTLC after its locktime, so the funding stays in the HTLC.
    [Theory]
    [InlineData(Status.ClientFundingSeen)]
    [InlineData(Status.ClientFunded)]
    public void AFundingTheServerHasNotTakenByTheVhtlcLocktimeIsLeftForItsRefund(Status status)
    {
        var swap = Swap();

        Assert.Equal(Action.NeedsHtlcRefund, SatoraStableActions.Decide(swap, status, swap.VhtlcRefundLocktime));
        Assert.Equal(Action.RecordFunding, SatoraStableActions.Decide(swap, status, swap.VhtlcRefundLocktime.AddSeconds(-1)));
    }

    [Theory]
    [InlineData(Status.ServerFunded, Action.FinishClaim)]
    [InlineData(Status.ClientRedeeming, Action.FinishClaim)]
    [InlineData(Status.ClientRedeemed, Action.ClaimUnverified)]
    [InlineData(Status.ServerRedeemed, Action.ClaimUnverified)]
    public void PastTheClaimCutoffNothingNewIsClaimed(Status status, Action expected)
    {
        var swap = Swap();

        Assert.Equal(Action.Claim, SatoraStableActions.Decide(swap, status, swap.ClaimCutoff.AddSeconds(-1)));
        Assert.Equal(expected, SatoraStableActions.Decide(swap, status, swap.ClaimCutoff));
    }

    // Once the server can take the VHTLC back, the worker asks the SDK whether a claim can still complete.
    [Theory]
    [InlineData(Status.ServerFunded)]
    [InlineData(Status.ClientRedeeming)]
    public void FromTheVhtlcLocktimeAClaimIsFinishedOnlyIfItCanStillComplete(Status status)
    {
        var swap = Swap();

        Assert.Equal(Action.FinishClaim, SatoraStableActions.Decide(swap, status, swap.VhtlcRefundLocktime.AddSeconds(-1)));
        Assert.Equal(Action.FinishClaimOrHtlcRefund, SatoraStableActions.Decide(swap, status, swap.VhtlcRefundLocktime));
    }

    // Only the wallet's own receipt settles a payment, and a settled swap closes once the server took the HTLC.
    [Theory]
    [MemberData(nameof(Unclaimed))]
    public void AClaimedSwapIsCreditedOnItsReceiptWhateverTheServerReports(Status status, Action _)
    {
        var swap = Swap();
        swap.ClaimTxId = "ark";

        Assert.Equal(Action.Credit, SatoraStableActions.Decide(swap, status, Now));
        swap.Credited = true;
        Assert.Equal(status switch
        {
            Status.ServerRedeemed => Action.Close,
            // The HTLC went back to the deposit address after the claim.
            Status.ClientRedeemedAndClientRefunded => Action.NeedsSweep,
            _ => Action.Wait
        }, SatoraStableActions.Decide(swap, status, Now));
    }

    [Fact]
    public void ARedeemedSwapWhoseHtlcWentBackIsNeverClaimedAnewWhateverTheTime()
    {
        var swap = Swap();

        foreach (var now in new[] { Now, swap.ClaimCutoff, swap.VhtlcRefundLocktime })
            Assert.Equal(Action.FinishClaimOrSweep, SatoraStableActions.Decide(swap, Status.ClientRedeemedAndClientRefunded, now));
    }

    // USDC has 6 decimals: a hundredth of a USDC is 10 000 units.
    public static TheoryData<Relay, bool, long, Release> Releases => new()
    {
        // A signature that may still land could empty the address at any moment.
        { Relay.Live, false, 10_130_000, Release.Wait },
        { Relay.Live, true, 0, Release.Wait },
        // Funded while the server still follows the swap: it sees the funding.
        { Relay.Funded, false, 0, Release.Wait },
        // Funded after it stopped: the deposit is in the HTLC.
        { Relay.Funded, true, 0, Release.HtlcRefund },
        { Relay.Funded, true, 500_000, Release.HtlcRefund },
        { Relay.None, false, 10_000, Release.Sweep },
        { Relay.Expired, true, 10_130_000, Release.Sweep },
        { Relay.Unknown, true, 10_000, Release.Sweep },
        { Relay.None, true, 9_999, Release.Close },
        { Relay.Expired, false, 0, Release.Close },
        // A funding attempt the SDK has no journal for, and nothing at the address.
        { Relay.Unknown, false, 0, Release.Wait },
        { Relay.Unknown, true, 0, Release.HtlcRefund }
    };

    [Theory]
    [MemberData(nameof(Releases))]
    public void AnAddressNoFundingCanTakeFromIsReleasedOnlyOnceNoSignatureCanLand(Relay relay, bool serverStopped, long deposited, Release expected)
    {
        Assert.Equal(expected, SatoraStableActions.Release(relay, serverStopped, deposited, 6));
    }

    // The address does not tell when its tokens arrived, so only what is certain is said: a deposit
    // still short may have been sent in time, and one the worker tried to fund was whole before the cutoff.
    [Theory]
    [InlineData(5_000_000L, false, false, SatoraStableActions.ShortReason)]
    [InlineData(5_000_000L, true, true, SatoraStableActions.ShortReason)]
    [InlineData(10_130_000L, true, false, SatoraStableActions.NotFundedReason)]
    [InlineData(10_130_000L, true, true, SatoraStableActions.NotFundedReason)]
    [InlineData(10_130_000L, false, true, nameof(Status.Expired))]
    [InlineData(10_130_000L, false, false, SatoraStableActions.PastCutoffReason)]
    public void SaysWhyTheAddressHoldsTokensOnlyAsFarAsTheAddressShows(long deposited, bool fundingAttempted, bool serverStopped, string expected)
    {
        var swap = Swap();
        swap.FundingAttemptedAt = fundingAttempted ? Now : null;

        Assert.Equal(expected, SatoraStableActions.SweepReason(swap, serverStopped, deposited));
    }

    [Fact]
    public void TheAddressIsReadEveryTickUntilTenMinutesAfterTheInvoiceExpiresAndWhileARelayRequestIsUnresolved()
    {
        var swap = Swap();
        var late = swap.InvoiceExpiresAt + SatoraStableActions.FastPollWindow;

        Assert.True(SatoraStableActions.FastPoll(swap, late.AddSeconds(-1), relayUnresolved: false));
        Assert.False(SatoraStableActions.FastPoll(swap, late, relayUnresolved: false));
        Assert.True(SatoraStableActions.FastPoll(swap, late, relayUnresolved: true));
    }

    [Fact]
    public void ASwapIsAbandonedOnlyIfNothingReachedItAnHourAfterTheInvoiceExpired()
    {
        var swap = Swap();
        var from = swap.InvoiceExpiresAt + SatoraStableActions.AbandonAfter;

        Assert.True(SatoraStableActions.MayBeAbandoned(swap, BigInteger.Zero, from));
        Assert.False(SatoraStableActions.MayBeAbandoned(swap, BigInteger.Zero, from.AddSeconds(-1)));
        Assert.False(SatoraStableActions.MayBeAbandoned(swap, BigInteger.One, from));
        swap.FundingAttemptedAt = Now;
        Assert.False(SatoraStableActions.MayBeAbandoned(swap, BigInteger.Zero, from));
    }

    [Fact]
    public void WarnsAboutAFundedSwapTenMinutesBeforeItsClaimCutoff()
    {
        var swap = Swap();
        var warnFrom = swap.ClaimCutoff - SatoraStableActions.CutoffWarning;

        Assert.True(SatoraStableActions.NearCutoff(swap, Status.ServerFunded, warnFrom));
        Assert.False(SatoraStableActions.NearCutoff(swap, Status.ServerFunded, warnFrom.AddSeconds(-1)));
        // Funded on chain, but the server has not funded the VHTLC yet.
        Assert.True(SatoraStableActions.NearCutoff(swap, Status.ClientFunded, warnFrom));
        Assert.False(SatoraStableActions.NearCutoff(swap, Status.Pending, warnFrom));
        swap.ClaimTxId = "ark";
        Assert.False(SatoraStableActions.NearCutoff(swap, Status.ServerFunded, warnFrom));
    }

    // Rounded down, so a shortfall the SDK accepts never exceeds what BTCPay settles the invoice with.
    [Theory]
    [InlineData("1", 100u)]
    [InlineData("1.5", 150u)]
    [InlineData("0.999", 99u)]
    [InlineData("100", 10_000u)]
    [InlineData("250", 10_000u)]
    [InlineData("-1", 0u)]
    [InlineData(null, 0u)]
    public void AClaimsShortfallLimitIsTheInvoicesPaymentToleranceInBasisPoints(string? tolerance, uint limit) =>
        Assert.Equal(limit, SatoraStableActions.ShortfallLimit(tolerance is null ? null : decimal.Parse(tolerance, System.Globalization.CultureInfo.InvariantCulture)));

    private static SatoraStableSwap Swap() => SatoraStableIntegrationTests.Swap("store", "swap");
}
