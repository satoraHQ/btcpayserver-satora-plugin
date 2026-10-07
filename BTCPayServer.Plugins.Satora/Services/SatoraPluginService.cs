using BTCPayServer.Plugins.Satora.Models;

namespace BTCPayServer.Plugins.Satora.Services
{
    public class SatoraPluginService(
        SatoraWalletService walletService,
        SatoraWithdrawalService withdrawalService)
    {
        public virtual async Task<SatoraModel> GetStoreData(string storeId)
        {
            var status = await walletService.GetStatusAsync(storeId);
            return new SatoraModel
            {
                StoreId = storeId,
                Enabled = status.Enabled,
                WalletState = status.State,
                NetworkError = walletService.NetworkError,
                AutomatedLightningPayouts = await withdrawalService.HasAutomatedLightningPayoutsAsync(storeId),
                Stablecoins = await walletService.GetStablecoinsAsync(storeId)
            };
        }
    }
}
