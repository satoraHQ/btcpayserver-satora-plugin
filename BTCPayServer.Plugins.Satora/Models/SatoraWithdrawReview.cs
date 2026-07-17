namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraWithdrawReview
{
    public SatoraWithdrawRequest Request { get; set; } = new();
    public SatoraWithdrawDestinationType DestinationType { get; set; }
    public string DestinationTypeDisplay { get; set; } = string.Empty;
    public string NormalizedDestination { get; set; } = string.Empty;
    public ulong AmountSats { get; set; }
    public decimal AmountBtc { get; set; }
    public ulong AvailableSats { get; set; }
    public string FeeDescription { get; set; } = string.Empty;
    public bool CanExecute { get; set; }
    public string? Error { get; set; }
}
