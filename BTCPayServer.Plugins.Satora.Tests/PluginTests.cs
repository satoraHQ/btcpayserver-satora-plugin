using System.Reflection;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Razor.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class PluginTests
{
    // The check BTCPay runs at startup (PluginManager.AssertDependencies), on the same version source.
    [Fact]
    public void LoadsOnTheBtcpayItIsBuiltAgainst()
    {
        var btcpay = new BTCPayServerPlugin();
        var installed = new Dictionary<string, Version> { [btcpay.Identifier] = btcpay.Version };

        Assert.True(PluginManager.DependenciesMet(new Plugin().Dependencies, installed));
    }

    // 2.4.2 is a must-update security release.
    [Theory]
    [InlineData("2.4.1", false)]
    [InlineData("2.4.2", true)]
    [InlineData("2.5.0", true)]
    public void RefusesBtcpayBeforeTheSecurityRelease(string version, bool expected)
    {
        var installed = new Dictionary<string, Version> { [nameof(BTCPayServer)] = Version.Parse(version) };

        Assert.Equal(expected, PluginManager.DependenciesMet(new Plugin().Dependencies, installed));
    }

    [Fact]
    public void RegistersTheNavTheMigrationsAndOnlyTheSatoraCheckoutBodies()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        var extensions = services.Where(d => d.ServiceType == typeof(IUIExtension))
            .Select(d => (IUIExtension)d.ImplementationInstance!).ToList();
        var hostedServices = services.Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType).ToList();

        Assert.Contains(extensions, e => e.Location == "store-wallets-nav");
        Assert.Equal([("checkout-end", "SatoraChain/SatoraChainMethodCheckout"), ("checkout-end", "SatoraStable/SatoraStableMethodCheckout")],
            extensions.Where(e => e.Location.StartsWith("checkout", StringComparison.Ordinal)).Select(e => (e.Location, e.Partial)));
        Assert.Contains(typeof(PluginMigrationRunner), hostedServices);
    }

    [Fact]
    public void RegistersTheWalletAsALightningNodeWithItsWorker()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        Assert.Contains(services, d => d.ServiceType == typeof(ILightningConnectionStringHandler) && d.ImplementationType == typeof(SatoraLightningConnectionStringHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SatoraLightningWorker));
        foreach (var dependency in typeof(SatoraLightningWorker).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
                     .Where(t => t.Assembly == typeof(Plugin).Assembly))
            Assert.Contains(services, d => d.ServiceType == dependency);
    }

    [Fact]
    public void RegistersTheWalletAutomationWorkerWithItsDependencies()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SatoraWalletAutomationWorker));
        foreach (var dependency in typeof(SatoraWalletAutomationWorker).GetConstructors().Single().GetParameters().Select(p => p.ParameterType)
                     .Where(t => t.Assembly == typeof(Plugin).Assembly))
            Assert.Contains(services, d => d.ServiceType == dependency);
    }

    [Fact]
    public void RegistersTheOnChainMethodAsBtcpayRegistersItsOwn()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        Assert.Contains(services, d => d.ServiceType == typeof(SatoraChainPaymentHandler));
        Assert.Contains(new PrettyNameProvider.UntranslatedPrettyName(SatoraChainPaymentMethod.Id, "Bitcoin (Satora)"), PrettyNames(services));
        AssertCompiledPartial(services, "checkout-end", "SatoraChain/SatoraChainMethodCheckout");
        AssertCompiledPartial(services, "store-invoices-payments", "SatoraChain/ViewSatoraChainPaymentData");
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SatoraChainWorker));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SatoraPromptRenewer));
        foreach (var dependency in new[] { typeof(SatoraChainWorker), typeof(SatoraChainPaymentHandler), typeof(SatoraPromptRenewer), typeof(SatoraChainWallets) }
                     .SelectMany(t => t.GetConstructors().Single().GetParameters().Select(p => p.ParameterType))
                     .Where(t => t.Assembly == typeof(Plugin).Assembly))
            Assert.Contains(services, d => d.ServiceType == dependency);
    }

    [Fact]
    public void RegistersOneStablecoinMethodPerCoinBesideTheOnChainOne()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        Assert.Equal(1 + SatoraStablePaymentMethod.All.Count, services.Count(d => d.ServiceType == typeof(IPaymentMethodHandler)));
        foreach (var coin in SatoraStablePaymentMethod.All)
            Assert.Contains(new PrettyNameProvider.UntranslatedPrettyName(coin.Id, coin.PrettyName), PrettyNames(services));
        Assert.Equal(["USDC on Arbitrum (Satora)"], SatoraStablePaymentMethod.All.Select(c => c.PrettyName));
        // A link and a checkout body for each method, the on-chain one's included.
        foreach (var type in new[] { typeof(IPaymentLinkExtension), typeof(ICheckoutModelExtension) })
            Assert.Equal(1 + SatoraStablePaymentMethod.All.Count, services.Count(d => d.ServiceType == type));
        AssertCompiledPartial(services, "checkout-end", "SatoraStable/SatoraStableMethodCheckout");
        // The coin is passed to each handler; everything else comes from the container.
        foreach (var dependency in new[] { typeof(SatoraStablePaymentHandler), typeof(SatoraWalletService), typeof(SatoraPluginController) }
                     .SelectMany(t => t.GetConstructors().Single().GetParameters().Select(p => p.ParameterType))
                     .Where(t => t.Assembly == typeof(Plugin).Assembly && t != typeof(SatoraStablecoin)))
            Assert.Contains(services, d => d.ServiceType == dependency);
    }

    [Fact]
    public void RegistersTheStablecoinWorkerAndRefundsPageWithTheirDependencies()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SatoraStableWorker));
        foreach (var dependency in new[] { typeof(SatoraStableWorker), typeof(SatoraStableWallets), typeof(SatoraStableRefundController) }
                     .SelectMany(t => t.GetConstructors().Single().GetParameters().Select(p => p.ParameterType))
                     .Where(t => t.Assembly == typeof(Plugin).Assembly))
            Assert.Contains(services, d => d.ServiceType == dependency);
    }

    // The partial resolves to a view compiled into the plugin.
    private static void AssertCompiledPartial(ServiceCollection services, string location, string partial)
    {
        Assert.Single(services.Select(d => d.ImplementationInstance).OfType<IUIExtension>(), e => e.Location == location && e.Partial == partial);
        Assert.Contains(typeof(Plugin).Assembly.GetCustomAttributes<RazorCompiledItemAttribute>(), v => v.Identifier == $"/Views/Shared/{partial}.cshtml");
    }

    private static List<PrettyNameProvider.UntranslatedPrettyName> PrettyNames(ServiceCollection services) =>
        services.Where(d => d.ServiceType == typeof(PrettyNameProvider.UntranslatedPrettyName))
            .Select(d => (PrettyNameProvider.UntranslatedPrettyName)d.ImplementationInstance!).ToList();

    // BTCPay adds the plugin assembly as an MVC application part, so every public controller in it is routed
    // whether or not the plugin registers anything. Customers only ever reach BTCPay's own checkout.
    [Fact]
    public void EveryRouteNeedsARightOnTheStoreItNames()
    {
        var parts = new ApplicationPartManager();
        parts.ApplicationParts.Add(new AssemblyPart(typeof(Plugin).Assembly));
        parts.FeatureProviders.Add(new ControllerFeatureProvider());
        var feature = new ControllerFeature();
        parts.PopulateFeature(feature);

        Assert.Contains(typeof(SatoraPluginController).GetTypeInfo(), feature.Controllers);
        foreach (var controller in feature.Controllers)
        {
            var policies = controller.GetCustomAttributes<AuthorizeAttribute>(true).Select(a => a.Policy).ToList();
            Assert.NotEmpty(policies);
            Assert.All(policies, policy => Assert.StartsWith("btcpay.store.", policy));
            Assert.Empty(controller.GetCustomAttributes<AllowAnonymousAttribute>(true));
            Assert.Contains("{storeId}", controller.GetCustomAttribute<RouteAttribute>()?.Template);
            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.Empty(action.GetCustomAttributes<AllowAnonymousAttribute>(true));
                // An absolute template would leave the controller's {storeId} route, and the store with it.
                Assert.All(action.GetCustomAttributes(true).OfType<IRouteTemplateProvider>(),
                    route => Assert.False(route.Template?.StartsWith('~') is true || route.Template?.StartsWith('/') is true, $"{controller.Name}.{action.Name}"));
            }
        }
    }

    // BTCPay renders layout-banner on every page of its main layout.
    [Fact]
    public void RegistersThePaymentMethodNamesOnTheLayoutBanner()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        AssertCompiledPartial(services, "layout-banner", "SatoraPaymentMethodNames");
    }
}
