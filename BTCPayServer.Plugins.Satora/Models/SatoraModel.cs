
namespace BTCPayServer.Plugins.Satora.Models
{
    public class SatoraModel
    {
        public string StoreId { get; set; } = null!;

        public bool Enabled { get; set; }

        public SatoraWalletState WalletState { get; set; }

        public bool CanUseHotWallet { get; set; }

        public string? NetworkError { get; set; }

        public List<SatoraTx> Transactions { get; set; } = new();
    }
}
