using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using Satora.Sdk;
using SdkArkadeSpend = uniffi.satora_sdk_ffi.ArkadeSpend;

namespace BTCPayServer.Plugins.Satora.Lightning;

public enum SatoraOutgoingAction
{
    Wait,
    // Fund the VHTLC, or resolve an earlier funding from the SDK's journal.
    Fund,
    // The server claimed the VHTLC with the preimage, so the invoice is paid.
    Paid,
    Refund,
    // Ended without the wallet's sats in the VHTLC: nothing to refund.
    Close,
    // A status this table does not handle: move nothing and tell the store.
    Alert
}

public static class SatoraOutgoingActions
{
    // Median time past trails the clock by about an hour, often more, and the SDK refuses a refund
    // without the swap server until it has passed the VHTLC's refund locktime.
    public static readonly TimeSpan MedianTimeLag = TimeSpan.FromHours(2);

    // The statuses in which the swap server cosigns a refund (can_sign_client_collab_refund): it will
    // not pay the invoice any more.
    public static bool IsRefundState(SatoraSwapStatus status) => status is
        SatoraSwapStatus.ServerWontFund or SatoraSwapStatus.Expired or SatoraSwapStatus.ClientFundedServerRefunded or
        SatoraSwapStatus.ClientInvalidFunded or SatoraSwapStatus.ClientFundedTooLate;

    public static SatoraOutgoingAction Decide(SatoraLightningSwap swap, SatoraSwapStatus status)
    {
        // Nothing funds a swap whose payment was refused before its first funding attempt: once the
        // server reports anything but awaiting funding, it is over.
        if (!swap.FundingAttempted)
            return status == SatoraSwapStatus.Pending ? SatoraOutgoingAction.Wait : SatoraOutgoingAction.Close;
        return status switch
        {
            SatoraSwapStatus.Pending => swap.FundingTxId is null ? SatoraOutgoingAction.Fund : SatoraOutgoingAction.Wait,
            // ServerFunded: the server started paying the invoice, which can take days to settle or fail.
            SatoraSwapStatus.ClientFundingSeen or SatoraSwapStatus.ClientFunded or SatoraSwapStatus.ServerFunded => SatoraOutgoingAction.Wait,
            SatoraSwapStatus.ServerRedeemed => SatoraOutgoingAction.Paid,
            _ when IsRefundState(status) => SatoraOutgoingAction.Refund,
            // The server saw the refund the wallet made without it.
            SatoraSwapStatus.ClientRefunded when swap.RefundTxId is not null => SatoraOutgoingAction.Refund,
            _ => SatoraOutgoingAction.Alert
        };
    }

    // What the store may close by hand after checking the wallet's balance: an attempted swap the
    // worker cannot finish, past its refund locktime or in a status the worker does not handle.
    public static bool OwnerCanClose(SatoraLightningSwap swap, DateTimeOffset now) =>
        swap is { FundingAttempted: true, CompletedAt: null } &&
        (now >= swap.ClaimCutoff || Decide(swap, swap.Status) == SatoraOutgoingAction.Alert);

    // Past the refund locktime the wallet takes back alone what the server has neither paid nor agreed
    // to refund, whatever else it reports; the payment stays pending, as the server may have paid.
    public static bool RefundsWithoutServer(SatoraLightningSwap swap, SatoraOutgoingAction action, DateTimeOffset now) =>
        swap.FundingAttempted && now >= swap.ClaimCutoff &&
        action is SatoraOutgoingAction.Wait or SatoraOutgoingAction.Fund or SatoraOutgoingAction.Alert;

    // The cooperative refund first. Without the server, a refund needs the VHTLC's refund locktime
    // to have passed by median time past, which trails the clock by about an hour; the SDK checks
    // it, so before then the attempt only fails and is retried.
    public static SdkArkadeSpend[] RefundOperations(SatoraLightningSwap swap, DateTimeOffset now) =>
        now >= swap.ClaimCutoff
            ? [SdkArkadeSpend.CooperativeRefund, SdkArkadeSpend.Refund]
            : [SdkArkadeSpend.CooperativeRefund];

    // FundArkadeSwapAsync fails with UnresolvedFunding while a journaled payment may still be the
    // swap's. Its refusal of a swap that no longer awaits funding, given only after the journal was
    // checked, is the one error proving nothing was paid; it is an InvalidSwap like refusals that
    // prove nothing, so only its text tells it apart.
    private const string NotAwaitingFundingText = "swap is not awaiting funding";

    public static bool IsUnresolvedFunding(Exception error) =>
        error is SdkException { Kind: SdkErrorKind.UnresolvedFunding };

    public static bool ProvesNothingFunded(Exception error) =>
        error is SdkException { Kind: SdkErrorKind.InvalidSwap } sdk &&
        sdk.Message.Contains(NotAwaitingFundingText, StringComparison.Ordinal);

    // The SDK refuses a refund without the server until median time past passes the VHTLC's refund
    // locktime, before it looks at what the VHTLC holds; another InvalidSwap, such as a VHTLC with no
    // spendable outputs, is told apart only by its text.
    private const string RefundNotMatureText = "Arkade refund timelock has not matured";

    public static bool IsRefundLocktimeNotMature(Exception error) =>
        error is SdkException { Kind: SdkErrorKind.InvalidSwap } sdk &&
        sdk.Message.Contains(RefundNotMatureText, StringComparison.Ordinal);

    // What BTCPay's payout listener reads: Complete and Failed are final for it, Failed cancelling
    // the payout. A closed swap is complete only once the worker or the store recorded it paid; any
    // other close (refunded, never funded, or the store's word that the funds are back) failed. An
    // open one has failed only once the server will not pay the invoice.
    public static LightningPaymentStatus PaymentStatus(SatoraLightningSwap swap) =>
        swap.PaidAt is not null ? LightningPaymentStatus.Complete
        : swap.CompletedAt is not null ? LightningPaymentStatus.Failed
        : swap.Status == SatoraSwapStatus.ServerRedeemed ? LightningPaymentStatus.Complete
        : IsRefundState(swap.Status) ? LightningPaymentStatus.Failed
        : LightningPaymentStatus.Pending;

    // The preimage stays unknown: the SDK does not return it for this direction. FundingSats is the
    // swap server's quote, written with the swap, so it says what was sent only once a funding was
    // attempted, and nothing once a refund has taken the whole VHTLC back into the wallet.
    public static LightningPayment ToPayment(SatoraLightningSwap swap)
    {
        var amount = LightMoney.MilliSatoshis(swap.AmountMsat);
        var sent = swap is { FundingAttempted: true, FundingSats: { } funding, RefundTxId: null } ? LightMoney.Satoshis(funding) : null;
        return new LightningPayment
        {
            Id = swap.SwapId,
            PaymentHash = swap.PaymentHash,
            BOLT11 = swap.Bolt11,
            Status = PaymentStatus(swap),
            CreatedAt = swap.CreatedAt,
            Amount = amount,
            AmountSent = sent,
            Fee = sent is null ? null : sent - amount
        };
    }
}
