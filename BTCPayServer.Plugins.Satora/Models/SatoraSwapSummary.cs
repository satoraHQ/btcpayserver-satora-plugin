namespace BTCPayServer.Plugins.Satora.Models
{
    public class SatoraSwapSummary
    {
        public int Total { get; set; }

        public int Active { get; set; }

        public int NeedsAction { get; set; }

        public int Stale { get; set; }

        public int Completed { get; set; }
    }
}
