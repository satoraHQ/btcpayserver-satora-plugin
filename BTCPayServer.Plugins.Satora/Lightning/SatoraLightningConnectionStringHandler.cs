using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.Satora.Lightning;

public static class SatoraLightningConnectionString
{
    public const string Type = "satora";
    public const string StoreIdKey = "storeid";
    public const string KeyKey = "key";

    // The key is the store's secret: anyone with the string can spend the store's wallet through BTCPay.
    public static string Format(string storeId, string key) => $"type={Type};{StoreIdKey}={storeId};{KeyKey}={key}";

    public static bool TryParse(string? connectionString, out string storeId, out string key)
    {
        storeId = key = "";
        if (string.IsNullOrEmpty(connectionString))
            return false;
        Dictionary<string, string> values;
        try
        {
            values = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            if (!Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch (FormatException)
        {
            return false;
        }
        storeId = values.GetValueOrDefault(StoreIdKey) ?? "";
        key = values.GetValueOrDefault(KeyKey) ?? "";
        return true;
    }

    // The store a Satora connection string names, whether or not its key is current; null for any other string.
    public static string? StoreIdOf(string? connectionString) =>
        TryParse(connectionString, out var storeId, out _) ? storeId : null;
}

// Ported from pseudozach's PR #2. The store id in the string is public (URLs, Greenfield paths), so
// the per-store key is what stops one store owner attaching another store's wallet.
public class SatoraLightningConnectionStringHandler(
    SatoraWalletRegistry registry,
    SatoraLightningSwaps swaps,
    SatoraLightningInvoiceHub invoiceHub,
    SatoraClaimWindow claimWindow,
    SatoraLightningPayments payments,
    ILogger<SatoraLightningClient> logger) : ILightningConnectionStringHandler
{
    public ILightningClient? Create(string connectionString, Network network, out string? error)
    {
        error = null;
        if (!SatoraLightningConnectionString.TryParse(connectionString, out var storeId, out var key))
            return null;
        if (!IsWalletNetwork(network))
        {
            error = registry.NetworkError ?? "The Satora wallet only takes Bitcoin payments on BTCPay's own network.";
            return null;
        }
        if (string.IsNullOrEmpty(storeId) || !registry.IsLightningKey(storeId, key))
        {
            error = "This Satora connection string is not valid for any store wallet.";
            return null;
        }
        return new SatoraLightningClient(storeId, key, network, registry, swaps, invoiceHub, claimWindow, payments, logger);
    }

    // A store could set the string as an altcoin's Lightning node, and altcoin networks share chain names with Bitcoin.
    private bool IsWalletNetwork(Network network)
    {
        if (registry.Network is not { } walletNetwork || network.NetworkSet != NBitcoin.Bitcoin.Instance)
            return false;
        try
        {
            return SatoraNetworks.FromChain(network.ChainName) == walletNetwork;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
