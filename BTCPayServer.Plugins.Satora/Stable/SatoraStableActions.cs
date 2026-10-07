using System.Globalization;
using System.Numerics;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using NBitcoin;
using Satora.Sdk;

namespace BTCPayServer.Plugins.Satora.Stable;

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
