using System.Globalization;
using System.Numerics;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using NBitcoin;
using Satora.Sdk;

namespace BTCPayServer.Plugins.Satora.Stable;

public enum SatoraStableAction
{
    Wait,
    // Read the deposit address on its polling schedule, show the checkout what arrived, and fund the
    // swap through the server's relay once the address holds the deposit.
    Fund,
    // No funding may start any more. Once no relay signature can still pull the deposit, what the
    // address holds goes to the owner, and an empty address closes the swap if nothing was signed or
    // every signature expired unspent. A deposit that may be in the HTLC is left there for its refund
    // once the server stops following the swap.
    Release,
    // The server saw the funding in the swap's HTLC: record a Processing payment.
    RecordFunding,
    Claim,
    // Past the cutoff the SDK starts no new claim but finishes one it journaled: try that until the
    // VHTLC locktime, after which the server can take the VHTLC back.
    FinishClaim,
    // Past the VHTLC locktime, or once the server reports taking the VHTLC back: keep finishing a claim
    // the SDK says can still complete, and otherwise leave the deposit in the HTLC for its refund.
    FinishClaimOrHtlcRefund,
    // The server saw a claim, then the HTLC's refund to the deposit address: finish only a claim that
    // already spent the VHTLC, and once none can complete leave the refunded token for the owner.
    FinishClaimOrSweep,
    // Record the Settled payment with this wallet's claim receipt.
    Credit,
    // Claimed, and the server has taken the HTLC: close, unless the deposit address holds more.
    Close,
    // The HTLC holds the customer's deposit and the server will not complete the swap.
    NeedsHtlcRefund,
    // The HTLC went back to the deposit address, which now holds the token it locked.
    NeedsSweep,
    // The server reports a claim this wallet holds no receipt for and can no longer start one.
    ClaimUnverified,
    // A status this table does not handle: move nothing and tell the store.
    Alert
}

// What the SDK's relay journal says can still happen to a deposit.
public enum SatoraRelayState
{
    // Never sent to the relay.
    None,
    // A signature may still land until its deadline.
    Live,
    // Every signature is past its deadline with its nonce unspent: nothing can land any more.
    Expired,
    // A funding landed, proven or with the nonce spent.
    Funded,
    // The worker tried to fund the swap but the SDK has no journal for it: every attempt was refused
    // before signing, or the journal was lost.
    Unknown
}

public enum SatoraStableRelease
{
    Wait,
    Sweep,
    Close,
    HtlcRefund
}

public static class SatoraStableActions
{
    // The SDK's own permit deadline, the longest a relay funding can take to land after it is signed.
    public static readonly TimeSpan MaxPermitDeadline = TimeSpan.FromMinutes(30);
    // Below this the relay has too little time to submit a funding, so none starts.
    public static readonly TimeSpan MinPermitDeadline = TimeSpan.FromMinutes(5);
    // The SDK counts a nonce as dead only once chain time passes its deadline by this much (rust-sdk
    // evm/relay.rs EXPIRY_MARGIN), so a funding is assumed able to land until then.
    public static readonly TimeSpan PermitExpiryMargin = TimeSpan.FromSeconds(120);
    // A customer's transfer sent as the invoice expires still has to land, and the worker to see it,
    // before the last funding may start. Exchange withdrawals can take an hour.
    public static readonly TimeSpan MainnetDepositAllowance = TimeSpan.FromHours(1);
    public static readonly TimeSpan TestDepositAllowance = TimeSpan.FromMinutes(1);
    // How far the swap's token amount may be from the cached quote's rate for its amount.
    public const decimal MaxRateDeviation = 0.05m;
    // The payment a quote is read for, to tell the route's limits and its fees' share of a payment.
    public const long TypicalSats = 100_000;
    public const decimal MinToleranceFloorPercent = 1;

    // From a funding landing to the wallet seeing the VHTLC funded: EVM finality, the server's
    // confirmations and its funding of the VHTLC, and the worker's poll. Test chains are instant.
    public static TimeSpan FundingAllowance(Network network) =>
        network.ChainName == ChainName.Mainnet ? TimeSpan.FromMinutes(20) : TimeSpan.FromMinutes(2);

    public static TimeSpan DepositAllowance(Network network) =>
        network.ChainName == ChainName.Mainnet ? MainnetDepositAllowance : TestDepositAllowance;

    // The latest a funding may land: the wallet still has SatoraClaimWindow.SafetyMargin to claim
    // before its cutoff, which keeps the SDK's own claim margin to the VHTLC refund locktime.
    public static DateTimeOffset LandingDeadline(DateTimeOffset vhtlcRefundLocktime, Network network) =>
        vhtlcRefundLocktime - SatoraClaimWindow.ClaimMargin - SatoraClaimWindow.SafetyMargin - FundingAllowance(network);

    // About 11 h 23 min after creation on mainnet, whose VHTLCs last 12 h, and 11 min on a regtest
    // server with 1800 s ones.
    public static DateTimeOffset FundingStartCutoff(DateTimeOffset vhtlcRefundLocktime, Network network) =>
        LandingDeadline(vhtlcRefundLocktime, network) - MinPermitDeadline - PermitExpiryMargin;

    // A funding signed now lands by the landing deadline at the latest: late starts sign a shorter deadline.
    public static TimeSpan PermitDeadline(SatoraStableSwap swap, DateTimeOffset now)
    {
        var left = swap.FundingStartCutoff + MinPermitDeadline - now;
        return left < MaxPermitDeadline ? left : MaxPermitDeadline;
    }

    public static bool ExpiryFits(DateTimeOffset invoiceExpiresAt, DateTimeOffset fundingStartCutoff, Network network) =>
        invoiceExpiresAt + DepositAllowance(network) <= fundingStartCutoff;

    // The mainnet swap server gives every EVM to Arkade VHTLC 43,200 s (evm_to_arkade_generic.rs takes
    // htlc_refund_timeouts.server_seconds(), refund_timeouts.rs). Other servers can be configured with any window.
    public static TimeSpan? KnownVhtlcWindow(Network network) => network.ChainName == ChainName.Mainnet ? TimeSpan.FromHours(12) : null;

    // How long the window of a swap the server made is taken for what it still gives, as a test server
    // can be restarted with another.
    public static readonly TimeSpan ObservedVhtlcWindowAge = TimeSpan.FromHours(1);

    // The DEX swap that funds the HTLC need only deliver the payout, and the server takes its fees out
    // of what it delivers (evm_to_arkade.rs), so a payment can arrive short by up to the swap's fees. The
    // store's payment tolerance has to cover that share of a typical payment for the invoice to settle.
    public static decimal ToleranceFloorPercent(Quote quote)
    {
        var share = (decimal)quote.ProtocolFeeRate + (decimal)(quote.NetworkFee + quote.GaslessNetworkFee) / TypicalSats;
        return Math.Max(MinToleranceFloorPercent, Math.Ceiling(share * 1000) / 10);
    }

    // The SDK's limit on a VHTLC funded below the swap's target, in basis points of the target: the
    // invoice's payment tolerance, rounded down, so whatever the wallet claims settles the invoice. The
    // tolerance is a share of the invoice's price, so a claim refused by it might still have settled an
    // invoice partly paid another way; the refusal errs towards a refund. Without the invoice nothing
    // below the target is claimed.
    public static uint ShortfallLimit(decimal? tolerancePercent) =>
        tolerancePercent is { } percent ? (uint)Math.Clamp(Math.Floor(percent * 100), 0, 10_000) : 0;

    // The swap's token amount against what the quote's DEX rate asks for its payout plus the quote's
    // fees, added as the server adds them (quote_calculator.rs compute_net_amounts).
    public static bool RateFits(decimal sourceUnits, long receiveSats, Quote quote)
    {
        if (!TryParseUnits(quote.SourceAmount, out var quoteUnits) || !TryParseUnits(quote.TargetAmount, out var quoteSats))
            return false;
        var grossSats = receiveSats * (1 + (decimal)quote.ProtocolFeeRate) + quote.NetworkFee + quote.GaslessNetworkFee;
        var expected = grossSats * quoteUnits / quoteSats;
        return Math.Abs(sourceUnits / expected - 1) <= MaxRateDeviation;
    }

    public static readonly TimeSpan CutoffWarning = TimeSpan.FromMinutes(10);
    // A deposit made by the invoice's expiry shows within this, so the address is read every tick
    // until then and every LatePoll after it: public RPCs rate-limit, and a swap nobody pays stays
    // open until its funding cutoff.
    public static readonly TimeSpan FastPollWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LatePoll = TimeSpan.FromMinutes(5);
    // An exchange withdrawal sent by the invoice's expiry can take an hour to arrive.
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromHours(1);

    // Why a swap closed with nothing at its deposit address: no funding started in time, or the
    // invoice was paid another way.
    public const string UnfundedReason = "Unfunded";
    public const string AbandonedReason = "Abandoned";
    // Why a deposit address holds tokens the swap will not take, or the HTLC its deposit.
    public const string PastCutoffReason = "PastCutoff";
    public const string ShortReason = "Short";
    public const string NotFundedReason = "NotFunded";
    public const string LeftoverReason = "Leftover";
    public const string UnclaimedReason = "Unclaimed";
    public const string HtlcRefundedReason = "HtlcRefunded";
    // Funded, but the server stopped following the swap before it saw the funding.
    public const string FundedLateReason = "FundedLate";
    // A funding attempt the SDK has no journal for, with nothing left at the deposit address.
    public const string FundingUnknownReason = "FundingUnknown";

    // Only this wallet's own claim receipt (ClaimTxId) settles a payment; the server's status is only
    // its word. A settled swap stays open until the server has taken the HTLC, so that anything left at
    // the deposit address afterwards goes to the owner.
    public static SatoraStableAction Decide(SatoraStableSwap swap, SatoraSwapStatus status, DateTimeOffset now)
    {
        if (swap.ClaimTxId is not null)
            return !swap.Credited ? SatoraStableAction.Credit
                : status switch
                {
                    SatoraSwapStatus.ServerRedeemed => SatoraStableAction.Close,
                    SatoraSwapStatus.ClientRedeemedAndClientRefunded => SatoraStableAction.NeedsSweep,
                    _ => SatoraStableAction.Wait
                };
        var claimable = now < swap.ClaimCutoff;
        return status switch
        {
            // A funding the relay proved waits for the server to see it.
            SatoraSwapStatus.Pending when swap.FundTxHash is not null => SatoraStableAction.Wait,
            SatoraSwapStatus.Pending => now < swap.FundingStartCutoff ? SatoraStableAction.Fund : SatoraStableAction.Release,
            // The server funds no VHTLC after its locktime, and the wallet claims none past its cutoff.
            SatoraSwapStatus.ClientFundingSeen or SatoraSwapStatus.ClientFunded when now >= swap.VhtlcRefundLocktime =>
                SatoraStableAction.NeedsHtlcRefund,
            SatoraSwapStatus.ClientFundingSeen or SatoraSwapStatus.ClientFunded =>
                swap.PaymentRecorded ? SatoraStableAction.Wait : SatoraStableAction.RecordFunding,
            SatoraSwapStatus.ServerFunded or SatoraSwapStatus.ClientRedeeming =>
                claimable ? SatoraStableAction.Claim
                : now < swap.VhtlcRefundLocktime ? SatoraStableAction.FinishClaim
                : SatoraStableAction.FinishClaimOrHtlcRefund,
            // Only the SDK's word that no claim can complete leaves the deposit for its refund.
            SatoraSwapStatus.ClientFundedServerRefunded => SatoraStableAction.FinishClaimOrHtlcRefund,
            // Redeemed without a receipt: a claim that timed out or was cut short by a restart. Claiming
            // again resumes it from the SDK's journal and returns its receipt.
            SatoraSwapStatus.ClientRedeemed or SatoraSwapStatus.ServerRedeemed =>
                claimable ? SatoraStableAction.Claim : SatoraStableAction.ClaimUnverified,
            SatoraSwapStatus.ClientRedeemedAndClientRefunded => SatoraStableAction.FinishClaimOrSweep,
            SatoraSwapStatus.ClientInvalidFunded or SatoraSwapStatus.ServerWontFund or SatoraSwapStatus.ClientFundedTooLate =>
                SatoraStableAction.NeedsHtlcRefund,
            // The HTLC's refund pays the deposit address. A VHTLC still funded is not claimed: that would
            // take both legs of the swap.
            SatoraSwapStatus.ClientRefunded or SatoraSwapStatus.ClientRefundedServerFunded or SatoraSwapStatus.ClientRefundedServerRefunded =>
                SatoraStableAction.NeedsSweep,
            SatoraSwapStatus.Expired => SatoraStableAction.Release,
            _ => SatoraStableAction.Alert
        };
    }

    // A deposit stays where it is while a relay signature can still pull it. A funding that landed
    // while the server still follows the swap is the server's to see; one that landed after it stopped
    // left the deposit in the HTLC. An empty address closes the swap only when nothing was ever signed
    // or every signature expired unspent.
    public static SatoraStableRelease Release(SatoraRelayState relay, bool serverStopped, BigInteger deposited, int decimals) => relay switch
    {
        SatoraRelayState.Live => SatoraStableRelease.Wait,
        SatoraRelayState.Funded => serverStopped ? SatoraStableRelease.HtlcRefund : SatoraStableRelease.Wait,
        _ when deposited >= DustUnits(decimals) => SatoraStableRelease.Sweep,
        SatoraRelayState.None or SatoraRelayState.Expired => SatoraStableRelease.Close,
        _ => serverStopped ? SatoraStableRelease.HtlcRefund : SatoraStableRelease.Wait
    };

    // Why what a deposit address holds once no funding starts goes to the owner. The address does not
    // tell when the tokens arrived: a deposit still short may have been sent in time, and a whole one
    // the worker tried to fund was there before the cutoff.
    public static string SweepReason(SatoraStableSwap swap, bool serverStopped, BigInteger deposited) =>
        Units(swap.SourceUnits) is { } deposit && deposited < deposit ? ShortReason
        : swap.FundingAttemptedAt is not null ? NotFundedReason
        : serverStopped ? nameof(SatoraSwapStatus.Expired)
        : PastCutoffReason;

    // A hundredth of a token: less is not worth the gas of sending it back.
    public static BigInteger DustUnits(int decimals) => BigInteger.Pow(10, Math.Max(decimals - 2, 0));

    public static bool FastPoll(SatoraStableSwap swap, DateTimeOffset now, bool relayUnresolved) =>
        relayUnresolved || now < swap.InvoiceExpiresAt + FastPollWindow;

    // Not enough alone: the caller closes the swap as abandoned only once another payment settled
    // its invoice.
    public static bool MayBeAbandoned(SatoraStableSwap swap, BigInteger deposited, DateTimeOffset now) =>
        swap.FundingAttemptedAt is null && deposited.IsZero && now >= swap.InvoiceExpiresAt + AbandonAfter;

    // A funded swap that is still not claimed this close to its cutoff needs someone to look at it,
    // whether the server has funded the VHTLC or not.
    public static bool NearCutoff(SatoraStableSwap swap, SatoraSwapStatus status, DateTimeOffset now) =>
        swap.ClaimTxId is null &&
        status is SatoraSwapStatus.ClientFunded or SatoraSwapStatus.ServerFunded or SatoraSwapStatus.ClientRedeeming &&
        now >= swap.ClaimCutoff - CutoffWarning;

    public static string Describe(string? reason) => reason switch
    {
        PastCutoffReason => "paid after the swap's funding cutoff",
        ShortReason => "less than the deposit by the swap's funding cutoff",
        NotFundedReason => "not funded by the swap's funding cutoff",
        nameof(SatoraSwapStatus.Expired) => "paid after the swap expired",
        LeftoverReason => "left at the deposit address after the swap",
        UnclaimedReason => "not claimed in time",
        HtlcRefundedReason => "it was refunded from the swap contract to the deposit address",
        FundedLateReason => "funded after the swap server stopped following the swap",
        FundingUnknownReason => "a funding attempt that cannot be accounted for",
        nameof(SatoraSwapStatus.ClientInvalidFunded) => "the swap contract was funded with too little",
        nameof(SatoraSwapStatus.ServerWontFund) => "refused by the swap server",
        nameof(SatoraSwapStatus.ClientFundedTooLate) => "funded too late for the swap server",
        nameof(SatoraSwapStatus.ClientFunded) or nameof(SatoraSwapStatus.ClientFundingSeen) => "not funded by the swap server in time",
        _ => reason ?? ""
    };

    public static bool TryParseUnits(string? units, out decimal value) =>
        decimal.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;

    // A token amount in its smallest unit as the SDK writes it, a decimal integer, zero included.
    public static BigInteger? Units(string? units) =>
        BigInteger.TryParse(units, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;

    // In whole tokens with every decimal the token has, as the customer has to send it.
    public static string FormatUnits(BigInteger units, int decimals)
    {
        var whole = BigInteger.DivRem(units, BigInteger.Pow(10, decimals), out var fraction);
        return decimals == 0
            ? whole.ToString(CultureInfo.InvariantCulture)
            : $"{whole.ToString(CultureInfo.InvariantCulture)}.{fraction.ToString(CultureInfo.InvariantCulture).PadLeft(decimals, '0')}";
    }
}
