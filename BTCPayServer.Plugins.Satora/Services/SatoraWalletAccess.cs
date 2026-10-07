using Satora.Sdk;
using SdkClient = Satora.Sdk.Client;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;
using SdkArkadeSpend = uniffi.satora_sdk_ffi.ArkadeSpend;
using SdkChainId = uniffi.satora_sdk_ffi.ChainId;
using SdkLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;
using SdkQuoteAmount = uniffi.satora_sdk_ffi.QuoteAmount;
using SdkTokenId = uniffi.satora_sdk_ffi.TokenId;

namespace BTCPayServer.Plugins.Satora.Services;

// The SDK calls payments and withdrawals make on one store's wallet. An interface so their tests
// run without a swap server or arkd.
public interface ISatoraWallet
{
    Task<ArkadeBalance> GetBalanceAsync();

    // The wallet's own Arkade address, read again rather than cached: arkd rotating its signer key changes it.
    Task<string> GetArkadeAddressAsync();

    Task<string?> SettleArkadeAsync();

    // Renews the VTXOs about to expire that sends, swap fundings and exits leave out; returns the
    // Arkade batch, or null when none was due.
    Task<string?> RenewArkadeAsync();

    Task<string> SendArkadeAsync(string destination, ulong amountSats);

    Task<OffboardQuote> GetOffboardQuoteAsync(string address, ulong? amountSats);

    Task<string> OffboardAsync(string address, ulong? amountSats);

    Task<LightningSendQuote> GetLightningSendQuoteAsync(SdkLightningDestination destination);

    // What sourceSats pays out over Lightning after fees (NetTargetAmount), quoted without a
    // destination, so with the swap server's estimate of the Lightning fee.
    Task<Quote> GetLightningSendMaxQuoteAsync(ulong sourceSats);

    Task<SwapDetails> CreateArkadeToLightningSwapAsync(SdkLightningDestination destination);

    Task<string> FundArkadeSwapAsync(string swapId);

    Task<BulkStatus> GetBulkStatusAsync(IReadOnlyList<string> swapIds);

    // Refunds a swap's VHTLC into the wallet; returns the refund's txid.
    Task<string> RefundAsync(string swapId, SdkArkadeSpend operation);

    // What a swap's VHTLC holds, read from the Arkade indexer without the swap server.
    Task<VhtlcAmounts> AmountsForSwapAsync(string swapId);
}

public class SatoraWalletAccess(SatoraWalletRegistry registry)
{
    // For reads.
    public virtual async Task<ISatoraWallet> GetAsync(string storeId) => new Live(await registry.GetAsync(storeId));

    // For the worker's VHTLC refunds (SatoraWalletRegistry.UseAsync): a replace or eviction waits for
    // them as for a spend, but they do not wait for the spend lock, as they spend the VHTLC, not the
    // wallet's own funds.
    public virtual Task<T> UseAsync<T>(string storeId, Func<ISatoraWallet, Task<T>> action, CancellationToken cancellationToken = default) =>
        registry.UseAsync(storeId, wallet => action(new Live(wallet)), cancellationToken);

    // With the store's spend lock held (SatoraWalletRegistry.SpendAsync): every spend that is not
    // resumable from the SDK's journal, and every row a seed replace counts, goes through here.
    public virtual Task<T> SpendAsync<T>(string storeId, Func<ISatoraWallet, Task<T>> action, CancellationToken cancellationToken = default) =>
        registry.SpendAsync(storeId, wallet => action(new Live(wallet)), cancellationToken);

    private sealed class Live(StoreWallet wallet) : ISatoraWallet
    {
        private SdkClient Client => wallet.Client;

        public Task<ArkadeBalance> GetBalanceAsync() => Client.GetArkadeBalanceAsync();

        public Task<string> GetArkadeAddressAsync() => wallet.RefreshArkadeAddressAsync();

        public Task<string?> SettleArkadeAsync() => Client.SettleArkadeAsync();

        public Task<string?> RenewArkadeAsync() => Client.RenewArkadeAsync();

        public Task<string> SendArkadeAsync(string destination, ulong amountSats) => Client.SendArkadeAsync(destination, amountSats);

        public Task<OffboardQuote> GetOffboardQuoteAsync(string address, ulong? amountSats) => Client.GetArkadeOffboardQuoteAsync(address, amountSats);

        public Task<string> OffboardAsync(string address, ulong? amountSats) => Client.OffboardArkadeAsync(address, amountSats);

        public Task<LightningSendQuote> GetLightningSendQuoteAsync(SdkLightningDestination destination) =>
            Client.GetLightningSendQuoteAsync(destination);

        public Task<Quote> GetLightningSendMaxQuoteAsync(ulong sourceSats) =>
            Client.GetQuoteAsync(new SdkChainId.Arkade(), new SdkTokenId.Btc(), new SdkChainId.Lightning(), new SdkTokenId.Btc(),
                new SdkQuoteAmount.Source(sourceSats));

        public Task<SwapDetails> CreateArkadeToLightningSwapAsync(SdkLightningDestination destination) =>
            Client.CreateArkadeToLightningSwapAsync(destination);

        public Task<string> FundArkadeSwapAsync(string swapId) => Client.FundArkadeSwapAsync(swapId);

        public Task<BulkStatus> GetBulkStatusAsync(IReadOnlyList<string> swapIds) => Client.GetBulkStatusAsync(swapIds);

        public Task<VhtlcAmounts> AmountsForSwapAsync(string swapId) => Client.AmountsForSwapAsync(swapId);

        // Read for every refund, as for claims: after arkd rotates its signer key, SettleAsync
        // refuses a destination under the old one.
        public async Task<string> RefundAsync(string swapId, SdkArkadeSpend operation) =>
            await Client.SettleAsync(swapId, operation, await wallet.RefreshArkadeAddressAsync()) switch
            {
                SdkArkadeSettlement.Offchain offchain => offchain.arkTxid,
                SdkArkadeSettlement.Delegated delegated => delegated.commitmentTxid,
                var other => throw new InvalidOperationException($"The SDK settled the refund with an unexpected outcome, {other}.")
            };
    }
}
