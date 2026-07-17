using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Services;
using NBitcoin;

namespace BTCPayServer.Plugins.Satora.Lightning;

public class SatoraLightningConnectionStringHandler(
    SatoraPluginDbContextFactory dbContextFactory,
    SatoraPluginService pluginService,
    ILogger<SatoraLightningClient> logger) : ILightningConnectionStringHandler
{
    public const string Type = "satora";
    public const string StoreIdKey = "storeid";

    public ILightningClient Create(string connectionString, Network network, out string error)
    {
        error = null!;
        try
        {
            var values = LightningConnectionStringHelper.ExtractValues(connectionString, out var type);
            if (!Type.Equals(type, StringComparison.OrdinalIgnoreCase))
                return null!;

            if (!values.TryGetValue(StoreIdKey, out var storeId) || string.IsNullOrWhiteSpace(storeId))
            {
                error = "Missing Satora store id";
                return null!;
            }

            return new SatoraLightningClient(storeId, network, dbContextFactory, pluginService, logger);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null!;
        }
    }

    public static string ToConnectionString(string storeId) => $"type={Type};{StoreIdKey}={storeId}";
}
