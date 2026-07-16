namespace BTCPayServer.Plugins.Satora.Models
{
    public class SwapRequest
    {
        public Stablecoins CryptoFrom { get; set; }
        public Blockchains NetworkFrom { get; set; }
        public decimal BtcAmount { get; set; }
        public string BtcDestination { get; set; }
        public string BtcNetwork { get; set; }
        public string BtcPayInvoiceId { get; set; }
    }
}
