using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Models;

namespace BTCPayServer.Plugins.Satora.Lightning;

public enum SatoraLightningAction
{
    Wait,
    Claim,
    // Past the cutoff the SDK starts no new claim but finishes one it journaled: try that until the
    // VHTLC locktime, after which the server can take the VHTLC back.
    FinishClaim,
    Credit,
    // Nothing more to do: the swap is credited, or ended without a payment to claim.
    Complete,
    // The payer paid, but the server took the VHTLC back before this wallet claimed it: complete
    // and tell the store, as the payer gets the payment back.
    CompleteRefunded,
    // The server reports a claim this wallet has no receipt for and can no longer start: tell the
    // store, keep finishing a claim the SDK says can still complete, and otherwise credit nothing and
    // complete.
    CompleteUnverified,
    // A status this table does not handle: move nothing and tell the store.
    Alert
}

public static class SatoraLightningActions
{
    public static readonly TimeSpan CutoffWarning = TimeSpan.FromMinutes(10);

    // Only this wallet's own claim receipt (ClaimTxId) credits a swap: the SDK refuses to claim an
    // underfunded VHTLC, while the server's status is only its word.
    public static SatoraLightningAction Decide(SatoraLightningSwap swap, SatoraSwapStatus status, DateTimeOffset now)
    {
        if (swap.ClaimTxId is not null)
            return swap.Credited ? SatoraLightningAction.Complete : SatoraLightningAction.Credit;
        var claimable = !swap.DoNotClaim && now < swap.ClaimCutoff;
        return status switch
        {
            SatoraSwapStatus.Pending or SatoraSwapStatus.ClientFundingSeen or SatoraSwapStatus.ClientFunded => SatoraLightningAction.Wait,
            SatoraSwapStatus.ServerFunded or SatoraSwapStatus.ClientRedeeming =>
                claimable ? SatoraLightningAction.Claim
                : !swap.DoNotClaim && now < swap.ClaimCutoff + SatoraClaimWindow.ClaimMargin ? SatoraLightningAction.FinishClaim
                : SatoraLightningAction.Wait,
            // Redeemed without a receipt: a claim that timed out or was cut short by a restart. Claiming
            // again resumes it from the SDK's journal and returns its receipt.
            SatoraSwapStatus.ClientRedeemed or SatoraSwapStatus.ServerRedeemed =>
                claimable ? SatoraLightningAction.Claim
                : now >= swap.ClaimCutoff ? SatoraLightningAction.CompleteUnverified
                : SatoraLightningAction.Wait,
            SatoraSwapStatus.Expired or SatoraSwapStatus.ServerWontFund => SatoraLightningAction.Complete,
            SatoraSwapStatus.ClientFundedServerRefunded =>
                swap.DoNotClaim ? SatoraLightningAction.Complete : SatoraLightningAction.CompleteRefunded,
            _ => SatoraLightningAction.Alert
        };
    }

    // A paid swap that is still not claimed this close to its cutoff needs someone to look at it. There
    // is deliberately no upper bound: a swap first seen paid past its cutoff (paid late, or after a
    // restart that outlasted the cutoff) alerts once too.
    public static bool NearCutoff(SatoraLightningSwap swap, SatoraSwapStatus status, DateTimeOffset now) =>
        status is SatoraSwapStatus.ServerFunded or SatoraSwapStatus.ClientRedeeming &&
        !swap.DoNotClaim &&
        now >= swap.ClaimCutoff - CutoffWarning;

    // GetInvoice reports Unpaid until the worker has recorded the payment, so BTCPay's listener never
    // records it first, and Expired only once no claim can start.
    public static LightningInvoice ToInvoice(SatoraLightningSwap swap, DateTimeOffset now)
    {
        var amount = LightMoney.MilliSatoshis(swap.AmountMsat);
        var status = swap.Credited ? LightningInvoiceStatus.Paid
            : swap.CompletedAt is not null || now >= swap.ClaimCutoff ? LightningInvoiceStatus.Expired
            : LightningInvoiceStatus.Unpaid;
        return new LightningInvoice
        {
            Id = swap.SwapId,
            PaymentHash = swap.PaymentHash,
            BOLT11 = swap.Bolt11,
            Status = status,
            Amount = amount,
            AmountReceived = status == LightningInvoiceStatus.Paid ? amount : null,
            PaidAt = status == LightningInvoiceStatus.Paid ? swap.PaidAt : null,
            ExpiresAt = swap.ExpiresAt
        };
    }
}
