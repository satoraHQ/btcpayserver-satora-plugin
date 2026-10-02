using System.Runtime.CompilerServices;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Services;

[assembly: InternalsVisibleTo("BTCPayServer.Plugins.Satora.Tests")]

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
                .AddSingleton<SatoraSeedProtector>()
                .AddSingleton<SatoraNotifier>()
                .AddSingleton<SatoraSentAlerts>()
                .AddSingleton<SatoraWalletRegistry>()
                // After PluginMigrationRunner, so the tables exist when it prepares the enabled wallets.
                .AddSingleton<IHostedService>(s => s.GetRequiredService<SatoraWalletRegistry>())
                .AddSingleton<SatoraWalletService>()
                .AddSingleton<INotificationHandler, SatoraWalletLockedNotification.Handler>()
                .AddSingleton(s => new SatoraClaimWindow(s.GetRequiredService<SatoraWalletRegistry>().Network))
                .AddSingleton<SatoraLightningSwaps>()
                .AddSingleton<SatoraLightningWallets>()
                .AddSingleton<SatoraLightningInvoiceHub>()
                .AddSingleton<SatoraLightningCredit>()
                .AddSingleton<SatoraStoreLightningConfig>()
                .AddSingleton<ILightningConnectionStringHandler, SatoraLightningConnectionStringHandler>()
                .AddHostedService<SatoraLightningWorker>()
                .AddSingleton<INotificationHandler, SatoraLightningNotification.Handler>()
                .AddSingleton<SatoraSettlementService>()
                .AddSingleton<SatoraService>()
                .AddSingleton<SatoraPluginService>();
    }
}
