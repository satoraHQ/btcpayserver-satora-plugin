using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Satora.Chain;

// The store's BTC-SATORA-CHAIN payment method, offered while the store's wallet is enabled. Unlike
// BTC-LN the method is the plugin's own, so it is written and removed with the wallet; the store's
// exclusion of it at checkout is the merchant's and is left alone.
public class SatoraStoreChainConfig(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers)
{
    public virtual async Task SyncAsync(string storeId, bool enabled)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is not null && Apply(store, handlers[SatoraChainPaymentMethod.Id], enabled))
            await storeRepository.UpdateStore(store);
    }

    public static bool Apply(StoreData store, IPaymentMethodHandler handler, bool enabled)
    {
        if (enabled == store.GetPaymentMethodConfig(handler.PaymentMethodId) is not null)
            return false;
        store.SetPaymentMethodConfig(handler, enabled ? new SatoraChainPaymentMethodConfig() : null);
        return true;
    }
}
