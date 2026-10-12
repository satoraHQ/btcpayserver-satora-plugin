using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Services;

namespace BTCPayServer.Plugins.Satora;

public class Plugin : BaseBTCPayServerPlugin
{
    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    {
        new() { Identifier = nameof(BTCPayServer), Condition = ">=2.4.2" },
    };

    public override void Execute(IServiceCollection services)
    {
        // The old stablecoin checkout extensions and SatoraSwapWatcher are not registered; see SatoraSwapController.
        services.AddUIExtension("store-wallets-nav", "SatoraPluginHeaderNav")
                .AddSingleton<SatoraPluginDbContextFactory>()
                .AddHostedService<PluginMigrationRunner>()
                .AddSingleton<SatoraSettlementService>()
                .AddSingleton<SatoraService>()
                .AddSingleton<SatoraPluginService>();
    }
}
