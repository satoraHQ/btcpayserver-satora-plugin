using System.Globalization;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Lightning;

// How long after creating a Lightning to Arkade swap its VHTLC can still be claimed. The swap server
// sets it per network (refund_timeouts.rs: 1800 s on mainnet; a regtest server reads it from
// LENDASWAP_REGTEST_SERVER_TIMEOUT_SECS), and each created swap is checked against its own.
public class SatoraClaimWindow(SatoraBitcoinNetwork? network)
{
    // ClaimAsync and SettleAsync refuse a claim within this much of the VHTLC refund locktime.
    public static readonly TimeSpan ClaimMargin = TimeSpan.FromSeconds(300);
    // Room for the payer's wallet, the server funding the VHTLC and the claim itself.
    public static readonly TimeSpan SafetyMargin = TimeSpan.FromSeconds(300);

    private static readonly TimeSpan MainnetWindow = TimeSpan.FromSeconds(1800);

    // The window to refuse an invoice by before any swap exists. Off mainnet a server can be
    // configured with any window, and a remembered one would outlive a reconfiguration.
    public TimeSpan? Current { get; } = network == SatoraBitcoinNetwork.Mainnet ? MainnetWindow : null;

    public static TimeSpan MaxInvoiceExpiry(TimeSpan window) => window - ClaimMargin - SafetyMargin;

    public static bool Fits(TimeSpan requestedExpiry, TimeSpan window) => requestedExpiry <= MaxInvoiceExpiry(window);

    public static DateTimeOffset ClaimCutoff(long vhtlcRefundLocktime) =>
        DateTimeOffset.FromUnixTimeSeconds(vhtlcRefundLocktime) - ClaimMargin;

    // Measured on the server's clock, so a skewed BTCPay clock does not distort it.
    public static TimeSpan Observed(string serverCreatedAt, long vhtlcRefundLocktime, DateTimeOffset now)
    {
        var created = DateTimeOffset.TryParse(serverCreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : now;
        return DateTimeOffset.FromUnixTimeSeconds(vhtlcRefundLocktime) - created;
    }

    public static DateTimeOffset ExpiresAt(DateTimeOffset bolt11Expiry, DateTimeOffset claimCutoff) =>
        bolt11Expiry < claimCutoff ? bolt11Expiry : claimCutoff;
}
