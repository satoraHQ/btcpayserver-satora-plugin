
namespace BTCPayServer.Plugins.Satora.Models
{
    public class SatoraModel
    {
        public SatoraSettings Settings { get; set; }

        public List<SatoraTx> Transactions { get; set; }

        public List<SatoraSwapListItem> Swaps { get; set; } = new();

        public SatoraSwapSummary SwapSummary { get; set; } = new();

        public SatoraWalletBalance? WalletBalance { get; set; }

        public bool CanManageSeed { get; set; }

        public string? OneTimeSeed { get; set; }

        public SatoraWithdrawRequest WithdrawRequest { get; set; } = new();

        public SatoraWithdrawReview? WithdrawReview { get; set; }

        public SatoraWithdrawResult? WithdrawResult { get; set; }
    }
}
