namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraWithdrawResult
{
    public SatoraWithdrawDestinationType DestinationType { get; set; }
    public string DestinationTypeDisplay { get; set; } = string.Empty;
    public string Destination { get; set; } = string.Empty;
    public ulong AmountSats { get; set; }
    public decimal AmountBtc { get; set; }
    public string? TransactionId { get; set; }
    public string? SwapId { get; set; }
    public ulong? FundingAmountSats { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? FeeDescription { get; set; }
}
