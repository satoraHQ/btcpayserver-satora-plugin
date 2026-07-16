namespace BTCPayServer.Plugins.Satora.Models
{
    public class SatoraSwapListItem
    {
        public SatoraTx LocalTx { get; set; } = new();

        public SatoraSwapStatusView Status { get; set; } = new();
    }
}
