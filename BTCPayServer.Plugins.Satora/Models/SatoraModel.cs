using BTCPayServer.Plugins.Satora.Stable;


namespace BTCPayServer.Plugins.Satora.Models
{
    public class SatoraModel
    {
        public string StoreId { get; set; } = null!;

        public bool Enabled { get; set; }

        public SatoraWalletState WalletState { get; set; }

        public bool CanUseHotWallet { get; set; }

        public string? NetworkError { get; set; }

        public bool AutomatedLightningPayouts { get; set; }

        public IReadOnlyList<SatoraStablecoin> Stablecoins { get; set; } = [];

        public double PaymentTolerance { get; set; }

        // The coins turned on whose payments get no swap while the tolerance is below their floor.
        public IReadOnlyList<(SatoraStablecoin Coin, decimal Floor)> StablecoinToleranceShortfalls { get; set; } = [];
    }
}
