using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;
using SdkArkadeSpend = uniffi.satora_sdk_ffi.ArkadeSpend;

namespace BTCPayServer.Plugins.Satora.Lightning;

// The SDK calls SatoraLightningWorker makes, through each store's wallet in the registry. Virtual so
// the worker's tests run without a swap server or arkd.
public class SatoraLightningWallets(SatoraWalletRegistry registry)
{
    public virtual async Task<BulkStatus> GetBulkStatusAsync(string storeId, IEnumerable<string> swapIds, CancellationToken cancellationToken) =>
        await (await registry.GetAsync(storeId)).Client.GetBulkStatusAsync(swapIds, cancellationToken);

    // Read for every claim: after arkd rotates its signer key, SettleAsync refuses a destination
    // under the old one, which a cached address would keep.
    public virtual async Task<string> GetClaimAddressAsync(string storeId, CancellationToken cancellationToken) =>
        await (await registry.GetAsync(storeId)).RefreshArkadeAddressAsync(cancellationToken);

    public virtual Task<SdkArkadeSettlement> ClaimAsync(string storeId, string swapId, string destination, CancellationToken cancellationToken) =>
        registry.UseAsync(storeId, wallet => wallet.Client.SettleAsync(swapId, SdkArkadeSpend.Claim, destination), cancellationToken);

    // Reads the SDK's claim journal, arkd's indexer and the claim window, and sends nothing that carries
    // the preimage. False is final: no later call can complete the claim.
    public virtual async Task<bool> ClaimPossibleAsync(string storeId, string swapId) =>
        await (await registry.GetAsync(storeId)).Client.IsArkadeClaimPossibleAsync(swapId);
}
