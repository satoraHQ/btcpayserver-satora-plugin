using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;

namespace BTCPayServer.Plugins.Satora.Lightning;

public enum SatoraLightningConfigChange
{
    None,
    Write,
    Remove
}

// The store's BTC-LN payment method. The plugin only ever writes it from an owner's action (enable,
// disable, seed change, key rotation), never at startup, and replaces a Lightning node the merchant
// set up only when the owner asks for exactly that (replace). It never removes one.
public class SatoraStoreLightningConfig(StoreRepository storeRepository, PaymentMethodHandlerDictionary handlers)
{
    public static SatoraLightningConfigChange Decide(LightningPaymentMethodConfig? current, string storeId, bool enabled, bool replace = false)
    {
        var satoraStoreId = SatoraLightningConnectionString.StoreIdOf(current?.GetExternalLightningUrl());
        if (enabled)
            return replace || current is null || satoraStoreId is not null ? SatoraLightningConfigChange.Write : SatoraLightningConfigChange.None;
        // Another store's Satora wallet, set up by hand, is the merchant's choice too.
        return satoraStoreId == storeId ? SatoraLightningConfigChange.Remove : SatoraLightningConfigChange.None;
    }

    public virtual async Task SyncAsync(string storeId, bool enabled, string? connectionKey, bool replace = false)
    {
        var store = await storeRepository.FindStore(storeId);
        if (store is not null && Apply(store, handlers.GetLightningHandler(SatoraLightningCredit.CryptoCode), enabled, connectionKey, replace))
            await storeRepository.UpdateStore(store);
    }

    public static bool Apply(StoreData store, LightningLikePaymentHandler lightning, bool enabled, string? connectionKey, bool replace = false)
    {
        var current = store.GetPaymentMethodConfig(lightning.PaymentMethodId) is { } config
            ? lightning.ParsePaymentMethodConfig(config)
            : null;
        switch (Decide(current, store.Id, enabled && connectionKey is not null, replace))
        {
            case SatoraLightningConfigChange.Write:
                var connectionString = SatoraLightningConnectionString.Format(store.Id, connectionKey!);
                // Satora invoices cannot commit to a description hash, which LNURL-pay needs.
                var lnurl = PaymentTypes.LNURL.GetPaymentMethodId(SatoraLightningCredit.CryptoCode);
                var blob = store.GetStoreBlob();
                // Only the owner's replace turns BTC-LN back on: enabling leaves a merchant's choice to turn it off.
                var include = replace && blob.IsExcluded(lightning.PaymentMethodId);
                if (current?.GetExternalLightningUrl() == connectionString && blob.IsExcluded(lnurl) && !include)
                    return false;
                store.SetPaymentMethodConfig(lightning, new LightningPaymentMethodConfig { ConnectionString = connectionString });
                blob.SetExcluded(lnurl, true);
                if (include)
                    blob.SetExcluded(lightning.PaymentMethodId, false);
                store.SetStoreBlob(blob);
                return true;
            case SatoraLightningConfigChange.Remove:
                store.SetPaymentMethodConfig(lightning.PaymentMethodId, null);
                return true;
            default:
                return false;
        }
    }
}
