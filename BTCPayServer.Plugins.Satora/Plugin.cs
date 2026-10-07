using System.Runtime.CompilerServices;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
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
        services.AddUIExtension("store-wallets-nav", "SatoraPluginHeaderNav")
                .AddUIExtension("layout-banner", "SatoraPaymentMethodNames")
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
                .AddSingleton<SatoraWalletAccess>()
                .AddSingleton<SatoraWalletRenewals>()
                .AddSingleton<IHostedService>(s => s.GetRequiredService<SatoraWalletRenewals>())
                .AddSingleton<SatoraOutgoingSwaps>()
                .AddSingleton<SatoraLightningPayments>()
                .AddSingleton<SatoraWithdrawals>()
                .AddSingleton<SatoraAutoWithdrawRules>()
                .AddSingleton<SatoraWithdrawalService>()
                .AddSingleton<ILightningConnectionStringHandler, SatoraLightningConnectionStringHandler>()
                .AddHostedService<SatoraLightningWorker>()
                .AddHostedService<SatoraOutgoingWorker>()
                .AddHostedService<SatoraWalletAutomationWorker>()
                .AddSingleton<INotificationHandler, SatoraLightningNotification.Handler>()
                .AddSingleton<INotificationHandler, SatoraWithdrawalNotification.Handler>()
                .AddSingleton<INotificationHandler, SatoraWalletNotification.Handler>()
                .AddSingleton<SatoraChainSwaps>()
                .AddSingleton<SatoraChainWallets>()
                .AddSingleton<SatoraChainCredit>()
                .AddSingleton<SatoraStoreChainConfig>()
                // Registered as BTCPay registers its own BTC-CHAIN and BTC-LN methods (AddBTCPayNetwork).
                .AddDefaultPrettyName(SatoraChainPaymentMethod.Id, SatoraChainPaymentMethod.PrettyName)
                .AddSingleton(s => ActivatorUtilities.CreateInstance<SatoraChainPaymentHandler>(s, BitcoinNetwork(s)))
                .AddSingleton<IPaymentMethodHandler>(s => s.GetRequiredService<SatoraChainPaymentHandler>())
                .AddSingleton<IPaymentLinkExtension>(s => ActivatorUtilities.CreateInstance<SatoraChainPaymentLinkExtension>(s, BitcoinNetwork(s)))
                .AddSingleton<ICheckoutModelExtension>(s => ActivatorUtilities.CreateInstance<SatoraChainCheckoutModelExtension>(s, BitcoinNetwork(s)))
                .AddUIExtension("checkout-end", "SatoraChain/SatoraChainMethodCheckout")
                .AddUIExtension("store-invoices-payments", "SatoraChain/ViewSatoraChainPaymentData")
                .AddHostedService<SatoraChainWorker>()
                .AddHostedService<SatoraChainPromptRenewer>()
                .AddSingleton<INotificationHandler, SatoraChainNotification.Handler>()
                .AddSingleton<SatoraPluginService>();
    }

    private static BTCPayNetwork BitcoinNetwork(IServiceProvider services) =>
        services.GetRequiredService<BTCPayNetworkProvider>().GetNetwork<BTCPayNetwork>(SatoraChainPaymentMethod.CryptoCode);
}
