namespace BTCPayServer.Plugins.Satora.Models
{
    public class SwapResponse
    {
        public bool Success { get; set; }
        public string SwapId { get; set; }
        public string StatusMessage { get; set; }
        public string FromAddress { get; set; }
        public float FromAmount { get; set; }
        public ulong NetworkFeeSats { get; set; }
        public ulong GaslessNetworkFeeSats { get; set; }
        public ulong ProtocolFeeSats { get; set; }
        public double ProtocolFeeRate { get; set; }
        public ulong MinAmountSats { get; set; }
        public ulong MaxAmountSats { get; set; }
        public ulong? BridgeFee { get; set; }

    }
}
