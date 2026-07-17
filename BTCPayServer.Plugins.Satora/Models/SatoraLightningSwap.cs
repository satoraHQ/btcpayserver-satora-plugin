using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraLightningSwap
{
    [Key]
    public string SwapId { get; set; } = null!;
    public string StoreId { get; set; } = null!;
    public string Direction { get; set; } = null!;
    public string Bolt11 { get; set; } = null!;
    public string PaymentHash { get; set; } = null!;
    public long AmountSats { get; set; }
    public long? FundingAmountSats { get; set; }
    public string? FundingAddress { get; set; }
    public string? FundingTxId { get; set; }
    public string? ClaimTxId { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
}
