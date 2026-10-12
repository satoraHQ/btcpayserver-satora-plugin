using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Satora.Stable;

// The store's stablecoin payment methods: each one the owner turned on is offered while the store's
// wallet is enabled, as the on-chain method is. The methods are the plugin's own, so they are written
// and removed with the wallet; the store's exclusion of one at checkout is the merchant's and is left alone.
public class SatoraStoreStableConfig(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers)
{
    public virtual async Task SyncAsync(string storeId, bool walletEnabled, IReadOnlyCollection<SatoraStablecoin> coins)
    {
        var store = await storeRepository.FindStore(storeId);
        var offered = walletEnabled ? coins : [];
        if (store is not null && Apply(store, SatoraStablePaymentMethod.All.Select(c => handlers[c.Id]), offered.Select(c => c.Id).ToHashSet()))
            await storeRepository.UpdateStore(store);
    }

    public static bool Apply(StoreData store, IEnumerable<IPaymentMethodHandler> stablecoinHandlers, IReadOnlySet<PaymentMethodId> offered)
    {
        var changed = false;
        foreach (var handler in stablecoinHandlers)
        {
            var offer = offered.Contains(handler.PaymentMethodId);
            if (offer == store.GetPaymentMethodConfig(handler.PaymentMethodId) is not null)
                continue;
            store.SetPaymentMethodConfig(handler, offer ? new SatoraStablePaymentMethodConfig() : null);
            changed = true;
        }
        return changed;
    }
}
