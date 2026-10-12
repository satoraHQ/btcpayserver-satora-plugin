using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using SdkChainId = uniffi.satora_sdk_ffi.ChainId;
using SdkTokenId = uniffi.satora_sdk_ffi.TokenId;

namespace BTCPayServer.Plugins.Satora.Stable;

// A token on an EVM chain swapped into the store's Arkade wallet, offered as a payment method of its
// own. Only tokens the swap server's relay can pull with a permit, on a chain the server serves directly:
// native USDC on Arbitrum.
public sealed record SatoraStablecoin(
    PaymentMethodId Id,
    string PrettyName,
    string Symbol,
    string ChainName,
    long ChainId,
    SdkChainId SdkChain,
    SdkTokenId SdkToken,
    string Contract,
    int Decimals)
{
    // The plugin's embedded token icon, served by BTCPay from the plugin assembly.
    public string Image => $"Resources/ico-satora/{Symbol}.webp";
}

public static class SatoraStablePaymentMethod
{
    // The prompt is in bitcoin at the store's rate, so the wallet receives exactly what is due and the
    // customer's token amount carries the swap's fees.
    public const string CryptoCode = "BTC";

    // Contracts as the SDK names them (rust-sdk types/token.rs).
    public static readonly SatoraStablecoin UsdcArbitrum = new(
        new PaymentMethodId("BTC-SATORA-USDC-ARBITRUM"), "USDC on Arbitrum (Satora)", "USDC", "Arbitrum One", 42161,
        new SdkChainId.Arbitrum(), new SdkTokenId.UsdcArbitrum(), "0xaf88d065e77c8cc2239327c5edb3a432268e5831", 6);

    public static readonly IReadOnlyList<SatoraStablecoin> All = [UsdcArbitrum];

    // The owner's choice as SatoraSettings stores it. An id this version does not offer is dropped.
    public static IReadOnlyList<SatoraStablecoin> Parse(string? stored) =>
        stored is null
            ? []
            : All.Where(c => stored.Split(',', StringSplitOptions.TrimEntries).Contains(c.Id.ToString())).ToList();

    public static string? Format(IEnumerable<SatoraStablecoin> coins) =>
        All.Where(coins.Contains).Select(c => c.Id.ToString()).ToList() is { Count: > 0 } ids ? string.Join(",", ids) : null;
}

// What the store's payment method config holds while the method is offered: nothing, as for the
// on-chain method.
public class SatoraStablePaymentMethodConfig;

public class SatoraStablePromptDetails
{
    public string SwapId { get; set; } = null!;

    public long ChainId { get; set; }

    public string Token { get; set; } = null!;

    public string TokenAddress { get; set; } = null!;

    public int Decimals { get; set; }

    // What the customer sends to the prompt's deposit address, in the token's smallest unit.
    public string Units { get; set; } = null!;

    // What the swap pays into the wallet. The prompt shows its address only while this is what is due.
    public long ReceiveSats { get; set; }

    // What the deposit address held when the worker last read it, in the token's smallest unit; null
    // before the first read.
    public string? ReceivedUnits { get; set; }

    // Read without the handler's serializer, as Newtonsoft matches the stored camelCase names anyway.
    public static SatoraStablePromptDetails? Of(PaymentPrompt? prompt) => prompt?.Details?.ToObject<SatoraStablePromptDetails>();
}

public class SatoraStablePaymentData
{
    public string SwapId { get; set; } = null!;

    public string? ClaimTxId { get; set; }
}
