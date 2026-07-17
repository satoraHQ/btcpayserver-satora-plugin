namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraWalletBalance
{
    public ulong ConfirmedSats { get; set; }
    public ulong PreConfirmedSats { get; set; }
    public ulong RecoverableSats { get; set; }
    public decimal ConfirmedBtc { get; set; }
    public decimal PreConfirmedBtc { get; set; }
    public decimal RecoverableBtc { get; set; }
    public string FiatCurrency { get; set; } = "USD";
    public decimal? BtcFiatRate { get; set; }
    public decimal? ConfirmedFiat { get; set; }
    public decimal? PreConfirmedFiat { get; set; }
    public decimal? RecoverableFiat { get; set; }
    public string? FiatError { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
}
