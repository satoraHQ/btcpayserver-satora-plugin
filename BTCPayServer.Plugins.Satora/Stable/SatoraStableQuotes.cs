using System.Collections.Concurrent;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using SdkChainId = uniffi.satora_sdk_ffi.ChainId;
using SdkQuoteAmount = uniffi.satora_sdk_ffi.QuoteAmount;
using SdkTokenId = uniffi.satora_sdk_ffi.TokenId;

namespace BTCPayServer.Plugins.Satora.Stable;

// Each token's quote for a typical payment, read through the asking store's wallet and reused for a
// minute: every prompt checks its limits and rate against it, and the settings page its fees. A quote
// does not depend on the store. Requests that find it stale at the same time each read one.
public class SatoraStableQuotes(SatoraWalletRegistry registry)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<PaymentMethodId, (Quote Quote, DateTimeOffset ReadAt)> _quotes = new();

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public virtual async Task<Quote> GetAsync(string storeId, SatoraStablecoin coin, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        if (_quotes.TryGetValue(coin.Id, out var cached) && now - cached.ReadAt < MaxAge)
            return cached.Quote;
        var quote = await FetchAsync(storeId, coin, cancellationToken);
        _quotes[coin.Id] = (quote, now);
        return quote;
    }

    // Whatever its age, and null before any read: the settings page asks the swap server nothing.
    public virtual Quote? LastRead(SatoraStablecoin coin) => _quotes.TryGetValue(coin.Id, out var cached) ? cached.Quote : null;

    protected virtual async Task<Quote> FetchAsync(string storeId, SatoraStablecoin coin, CancellationToken cancellationToken)
    {
        var wallet = await registry.GetAsync(storeId, cancellationToken);
        return await wallet.Client.GetQuoteAsync(coin.SdkChain, coin.SdkToken, new SdkChainId.Arkade(), new SdkTokenId.Btc(),
            new SdkQuoteAmount.Target(SatoraStableActions.TypicalSats), cancellationToken);
    }
}
