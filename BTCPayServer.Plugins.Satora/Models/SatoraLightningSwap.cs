using System.ComponentModel.DataAnnotations;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraLightningSwap
{
    [Key]
    public string SwapId { get; set; } = null!;

    // The store whose wallet created the swap and whose seed claims it.
    public string StoreId { get; set; } = null!;

    public SatoraLightningDirection Direction { get; set; }

    // The swap server's status as last read by the worker.
    public SatoraSwapStatus Status { get; set; }

    public string PaymentHash { get; set; } = null!;

    public string Bolt11 { get; set; } = null!;

    public long AmountMsat { get; set; }

    public long? ReceiveSats { get; set; }

    // Incoming: VHTLC refund locktime minus the SDK's claim margin: no claim can start after it.
    // Outgoing: the VHTLC refund locktime, after which the wallet can refund without the swap server.
    public DateTimeOffset ClaimCutoff { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    // Outgoing only. Set before the first FundArkadeSwapAsync; from then on the VHTLC may hold the
    // wallet's sats, and only the worker decides what happened.
    public bool FundingAttempted { get; set; }

    // Outgoing only: the sats the VHTLC takes from the wallet, invoice amount plus the swap's fees.
    public long? FundingSats { get; set; }

    public string? FundingTxId { get; set; }

    public string? RefundTxId { get; set; }

    public bool DoNotClaim { get; set; }

    // Incoming only. Set before the first claim; from then on CancelInvoice cannot mark the swap
    // DoNotClaim, so a claim the SDK journaled is always resumed.
    public DateTimeOffset? ClaimStartedAt { get; set; }

    // The BTCPay payment is recorded (or there was no BTCPay invoice to record it on).
    public bool Credited { get; set; }

    public string? ClaimTxId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    // Set once the swap needs nothing more from the plugin; the worker only reads rows without it.
    public DateTimeOffset? CompletedAt { get; set; }
}

public enum SatoraLightningDirection
{
    LightningToArkade,
    ArkadeToLightning
}

// The SDK's SwapStatus variants, stored by name.
public enum SatoraSwapStatus
{
    Pending,
    ClientFundingSeen,
    ClientFunded,
    ClientRefunded,
    ServerFunded,
    ClientRedeeming,
    ClientRedeemed,
    ServerRedeemed,
    ClientFundedServerRefunded,
    ClientRefundedServerFunded,
    ClientRefundedServerRefunded,
    Expired,
    ClientInvalidFunded,
    ClientFundedTooLate,
    ServerWontFund,
    ClientRedeemedAndClientRefunded,
    // SwapStatus.Other: a status this SDK version does not name.
    Unknown
}

public static class SatoraSwapStatuses
{
    public static SatoraSwapStatus From(SdkSwapStatus status) => status switch
    {
        SdkSwapStatus.Pending => SatoraSwapStatus.Pending,
        SdkSwapStatus.ClientFundingSeen => SatoraSwapStatus.ClientFundingSeen,
        SdkSwapStatus.ClientFunded => SatoraSwapStatus.ClientFunded,
        SdkSwapStatus.ClientRefunded => SatoraSwapStatus.ClientRefunded,
        SdkSwapStatus.ServerFunded => SatoraSwapStatus.ServerFunded,
        SdkSwapStatus.ClientRedeeming => SatoraSwapStatus.ClientRedeeming,
        SdkSwapStatus.ClientRedeemed => SatoraSwapStatus.ClientRedeemed,
        SdkSwapStatus.ServerRedeemed => SatoraSwapStatus.ServerRedeemed,
        SdkSwapStatus.ClientFundedServerRefunded => SatoraSwapStatus.ClientFundedServerRefunded,
        SdkSwapStatus.ClientRefundedServerFunded => SatoraSwapStatus.ClientRefundedServerFunded,
        SdkSwapStatus.ClientRefundedServerRefunded => SatoraSwapStatus.ClientRefundedServerRefunded,
        SdkSwapStatus.Expired => SatoraSwapStatus.Expired,
        SdkSwapStatus.ClientInvalidFunded => SatoraSwapStatus.ClientInvalidFunded,
        SdkSwapStatus.ClientFundedTooLate => SatoraSwapStatus.ClientFundedTooLate,
        SdkSwapStatus.ServerWontFund => SatoraSwapStatus.ServerWontFund,
        SdkSwapStatus.ClientRedeemedAndClientRefunded => SatoraSwapStatus.ClientRedeemedAndClientRefunded,
        _ => SatoraSwapStatus.Unknown
    };
}
