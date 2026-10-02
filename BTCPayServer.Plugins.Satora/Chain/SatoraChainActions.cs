using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using NBitcoin;

namespace BTCPayServer.Plugins.Satora.Chain;

public enum SatoraChainAction
{
    Wait,
    // Read the HTLC address: record a Processing payment once one output pays the deposit, and take it
    // off again if that output leaves the mempool.
    CheckFunding,
    Claim,
    // Past the cutoff the SDK starts no new claim but finishes one it journaled: try that until the
    // VHTLC locktime, after which the server can take the VHTLC back.
    FinishClaim,
    // Past the VHTLC locktime: keep finishing a claim the SDK says can still complete, and never refund
    // it; otherwise the customer's bitcoin goes back.
    FinishClaimOrRefund,
    // Record the Settled payment with this wallet's claim receipt.
    Credit,
    // Check the HTLC address for outputs left behind, then complete or ask for a refund.
    Close,
    // The customer's bitcoin has to go back: take the payment off the invoice and wait for the owner.
    NeedsRefund,
    // The server will not fund the VHTLC for a funding it saw: complete if that funding has left the
    // address, otherwise ask for a refund.
    FundingTooLate,
    // The server reports a claim this wallet holds no receipt for and can no longer start one.
    ClaimUnverified,
    // A status this table does not handle: move nothing and tell the store.
    Alert
}

public sealed record SatoraHtlcOutput(string TxId, uint Vout, long ValueSats, bool Confirmed);

public static class SatoraChainActions
{
    public static readonly TimeSpan CutoffWarning = TimeSpan.FromMinutes(10);
    // Median time past trails the clock by about an hour, often more, and the SDK refuses a refund
    // until it has passed the HTLC's refund locktime.
    public static readonly TimeSpan MedianTimeLag = TimeSpan.FromHours(2);
    // How long after the invoice's expiry an expired swap's address is still read for a late payment.
    public static readonly TimeSpan LatePaymentMargin = TimeSpan.FromHours(1);
    // A P2PKH output's dust limit, the highest of any address a refund can pay to.
    public const long DustSats = 546;

    public const string LeftoverReason = "LeftoverOutputs";
    // A funded VHTLC that no claim by the wallet can complete any more, past its locktime.
    public const string UnclaimedReason = "Unclaimed";

    // Only this wallet's own claim receipt (ClaimTxId) settles a payment; the server's status is only
    // its word. A settled swap stays open until the server has swept the customer's output, so that
    // anything still at the HTLC address afterwards is a deposit to refund.
    public static SatoraChainAction Decide(SatoraChainSwap swap, SatoraSwapStatus status, DateTimeOffset now)
    {
        if (swap.ClaimTxId is not null)
            return !swap.Credited ? SatoraChainAction.Credit
                : status == SatoraSwapStatus.ServerRedeemed ? SatoraChainAction.Close
                : SatoraChainAction.Wait;
        var claimable = now < swap.ClaimCutoff;
        return status switch
        {
            SatoraSwapStatus.Pending => SatoraChainAction.Wait,
            // The server stops watching a swap and funds no VHTLC at its locktime, so a funding it saw
            // or confirmed by then stays here for good. The wallet never claims past its cutoff, so the
            // preimage stays private and only the customer's refund can spend the HTLC.
            SatoraSwapStatus.ClientFundingSeen or SatoraSwapStatus.ClientFunded when now >= swap.VhtlcRefundLocktime =>
                SatoraChainAction.FundingTooLate,
            // An output seen only in the mempool can still be replaced or dropped.
            SatoraSwapStatus.ClientFundingSeen => SatoraChainAction.CheckFunding,
            SatoraSwapStatus.ClientFunded => swap.PaymentRecorded ? SatoraChainAction.Wait : SatoraChainAction.CheckFunding,
            SatoraSwapStatus.ServerFunded or SatoraSwapStatus.ClientRedeeming =>
                claimable ? SatoraChainAction.Claim
                : now < swap.VhtlcRefundLocktime ? SatoraChainAction.FinishClaim
                : SatoraChainAction.FinishClaimOrRefund,
            // Redeemed without a receipt: a claim that timed out or was cut short by a restart. Claiming
            // again resumes it from the SDK's journal and returns its receipt.
            SatoraSwapStatus.ClientRedeemed or SatoraSwapStatus.ServerRedeemed =>
                claimable ? SatoraChainAction.Claim : SatoraChainAction.ClaimUnverified,
            SatoraSwapStatus.ClientInvalidFunded or SatoraSwapStatus.ServerWontFund or SatoraSwapStatus.ClientFundedServerRefunded =>
                SatoraChainAction.NeedsRefund,
            SatoraSwapStatus.Expired => SatoraChainAction.Close,
            _ => SatoraChainAction.Alert
        };
    }

    // A paid swap that is still not claimed this close to its cutoff needs someone to look at it,
    // whether the server has funded the VHTLC or not. There is deliberately no upper bound: a swap
    // first seen paid past its cutoff (paid late, or after a restart that outlasted the cutoff) alerts
    // once too.
    public static bool NearCutoff(SatoraChainSwap swap, SatoraSwapStatus status, DateTimeOffset now) =>
        swap.ClaimTxId is null &&
        status is SatoraSwapStatus.ClientFunded or SatoraSwapStatus.ServerFunded or SatoraSwapStatus.ClientRedeeming &&
        now >= swap.ClaimCutoff - CutoffWarning;

    // A payment made just before the invoice expires still needs a confirmation before the server
    // funds the VHTLC, and the wallet claims only until its cutoff. Test networks mine within minutes,
    // and their swap servers give far shorter windows than mainnet's 12 hours.
    public static TimeSpan ConfirmationAllowance(Network network) =>
        network.ChainName == ChainName.Mainnet ? TimeSpan.FromHours(2) : TimeSpan.FromMinutes(5);

    public static bool ExpiryFits(DateTimeOffset invoiceExpiresAt, DateTimeOffset claimCutoff, Network network) =>
        invoiceExpiresAt <= claimCutoff - ConfirmationAllowance(network);

    // The mainnet swap server gives every Bitcoin to Arkade VHTLC 43,200 s (bitcoin_to_arkade.rs takes
    // htlc_refund_timeouts.server_seconds(), set by HtlcRefundTimeouts::new in refund_timeouts.rs, which
    // config overrides do not reach). Other servers can be configured with any window.
    public static TimeSpan? KnownWindow(Network network) => network.ChainName == ChainName.Mainnet ? TimeSpan.FromHours(12) : null;

    // Checked before a swap exists, so an invoice that cannot fit creates none; ExpiryFits on the
    // created swap decides.
    public static bool ExpiryFitsKnownWindow(DateTimeOffset invoiceExpiresAt, DateTimeOffset now, Network network) =>
        KnownWindow(network) is not { } window ||
        ExpiryFits(invoiceExpiresAt, now + window - SatoraClaimWindow.ClaimMargin, network);

    // An expired swap's address is read until a payment made by the invoice's expiry has had time to appear.
    public static bool LatePaymentPossible(SatoraChainSwap swap, DateTimeOffset now) =>
        now < swap.InvoiceExpiresAt + LatePaymentMargin;

    // The server takes a deposit from a single output of at least the expected amount
    // (onchain_htlc_watcher.rs), so smaller outputs never add up to one.
    public static SatoraHtlcOutput? Funding(IEnumerable<SatoraHtlcOutput> outputs, long depositSats) =>
        outputs.FirstOrDefault(o => o.ValueSats >= depositSats);

    // The first attempt, which fixes the refund address, comes at the locktime by the clock, and so is
    // the time the store is shown.
    public static bool RefundDue(SatoraChainSwap swap, DateTimeOffset now) =>
        swap is { NeedsRefundAt: not null, RefundAddress: not null, RefundTxId: null, CompletedAt: null } && now >= swap.BtcRefundLocktime;

    // By when median time past has passed the locktime and a refund can surely go out: refusals count
    // as failures from then, and an address with nothing worth a refund closes its swap.
    public static DateTimeOffset RefundPossibleFrom(SatoraChainSwap swap) => swap.BtcRefundLocktime + MedianTimeLag;

    // Whether no output alone is worth a one-input refund at the estimate. Stricter than the SDK, which
    // also spends outputs together whose sum covers their fee and checks the destination's own dust
    // limit, so it decides when a swap closes but never whether the SDK is asked.
    public static bool NothingToRefund(IEnumerable<SatoraHtlcOutput> outputs, long refundFeeSats) =>
        outputs.All(o => o.ValueSats < refundFeeSats + DustSats);
}

public enum SatoraChainRefundRefusal
{
    None,
    NotFound,
    NotNeeded,
    AlreadyStarted,
    InvalidAddress,
    ConfirmationRequired,
    RefundRunning
}

public static class SatoraChainRefunds
{
    public const string ConfirmationWord = "REFUND";
    public const string DismissWord = "DISMISS";

    // Why a swap waiting for a refund closed without one.
    public const string EmptyReason = "Empty";
    public const string UneconomicReason = "Uneconomic";
    public const string DismissedReason = "Dismissed";
    // The NothingToRefund status of a swap closed after its refund confirmed, leaving outputs worth less
    // than a refund's fee at its address.
    public const string LeftBehindStatus = "LeftBehind";

    public static string Describe(string? reason) => reason switch
    {
        nameof(SatoraSwapStatus.ClientInvalidFunded) => "Underpaid",
        nameof(SatoraSwapStatus.ServerWontFund) => "Refused by the swap server",
        nameof(SatoraSwapStatus.ClientFundedServerRefunded) => "Not claimed in time",
        nameof(SatoraSwapStatus.ClientFundingSeen) => "Not confirmed in time",
        SatoraChainActions.LeftoverReason => "Paid after the swap ended",
        SatoraChainActions.UnclaimedReason => "Not claimed in time",
        nameof(SatoraSwapStatus.ClientFunded) => "Not funded by the swap server in time",
        _ => reason ?? ""
    };

    // The destination can change until the worker first tries it, as the SDK then fixes it.
    public static SatoraChainRefundRefusal Check(SatoraChainSwap? swap, string? address, Network network, string? confirmation) =>
        swap is null ? SatoraChainRefundRefusal.NotFound
        : swap.NeedsRefundAt is null || swap.CompletedAt is not null ? SatoraChainRefundRefusal.NotNeeded
        : swap.RefundAttempted ? SatoraChainRefundRefusal.AlreadyStarted
        : string.IsNullOrWhiteSpace(address) || !SatoraChainPaymentHandler.IsAddressOn(address.Trim(), network) ? SatoraChainRefundRefusal.InvalidAddress
        : !string.Equals(confirmation?.Trim(), ConfirmationWord, StringComparison.Ordinal) ? SatoraChainRefundRefusal.ConfirmationRequired
        : SatoraChainRefundRefusal.None;

    public static SatoraChainRefundRefusal CheckDismiss(SatoraChainSwap? swap, string? confirmation) =>
        swap is null ? SatoraChainRefundRefusal.NotFound
        : swap.NeedsRefundAt is null || swap.CompletedAt is not null ? SatoraChainRefundRefusal.NotNeeded
        : !string.Equals(confirmation?.Trim(), DismissWord, StringComparison.Ordinal) ? SatoraChainRefundRefusal.ConfirmationRequired
        : SatoraChainRefundRefusal.None;

    // Lists a swap that closed without a refund, or with outputs left after one, again as it was.
    public static string ReopenStatement(string swapId) =>
        $"UPDATE \"BTCPayServer.Plugins.Satora\".\"SatoraChainSwaps\" SET \"CompletedAt\" = NULL, \"ClosedReason\" = NULL WHERE \"SwapId\" = '{swapId.Replace("'", "''")}'";

    // A refund already broadcast, or an attempt whose outcome was never learned, can still confirm.
    public static string DismissedNote(SatoraChainSwap swap) =>
        $"The refund of Satora on-chain swap {swap.SwapId} was dismissed on Satora's On-chain refunds page. " +
        (swap.RefundTxId is not null ? $"Its refund in {swap.RefundTxId} already went out, and nothing more is sent from its address."
            : swap.RefundAttempted ? "An earlier refund attempt may still confirm, but nothing more is sent from its address."
            : "Nothing at its address is sent back.");
}
