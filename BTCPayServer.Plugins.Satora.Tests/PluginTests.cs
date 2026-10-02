using System.Reflection;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
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
    public void LeavesTheStablecoinCheckoutAndItsWatcherUnregistered()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        var extensions = services.Where(d => d.ServiceType == typeof(IUIExtension))
            .Select(d => (IUIExtension)d.ImplementationInstance!).ToList();
        var hostedServices = services.Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType).ToList();

        Assert.Contains(extensions, e => e.Location == "store-wallets-nav");
        Assert.DoesNotContain(extensions, e => e.Partial.StartsWith("CheckoutV2/", StringComparison.Ordinal));
        Assert.Equal(["checkout-end"], extensions.Where(e => e.Location.StartsWith("checkout", StringComparison.Ordinal)).Select(e => e.Location));
        Assert.Contains(typeof(PluginMigrationRunner), hostedServices);
        Assert.DoesNotContain(typeof(SatoraSwapWatcher), hostedServices);
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
    public void RegistersTheOnChainMethodAsBtcpayRegistersItsOwn()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);

        foreach (var type in new[] { typeof(IPaymentMethodHandler), typeof(IPaymentLinkExtension), typeof(ICheckoutModelExtension) })
            Assert.Single(services, d => d.ServiceType == type);
        var name = Assert.Single(services, d => d.ServiceType == typeof(PrettyNameProvider.UntranslatedPrettyName)).ImplementationInstance;
        Assert.Equal(new PrettyNameProvider.UntranslatedPrettyName(SatoraChainPaymentMethod.Id, "Bitcoin (Satora)"), name);
        foreach (var location in new[] { "checkout-end", "store-invoices-payments" })
        {
            var extension = Assert.Single(services.Select(d => d.ImplementationInstance).OfType<IUIExtension>(), e => e.Location == location);
            // The partial resolves to a view compiled into the plugin.
            Assert.Contains(typeof(Plugin).Assembly.GetCustomAttributes<RazorCompiledItemAttribute>(),
                v => v.Identifier == $"/Views/Shared/{extension.Partial}.cshtml");
        }
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SatoraChainWorker));
        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(SatoraChainPromptRenewer));
        foreach (var dependency in new[] { typeof(SatoraChainWorker), typeof(SatoraChainPaymentHandler), typeof(SatoraChainPromptRenewer), typeof(SatoraChainWallets) }
                     .SelectMany(t => t.GetConstructors().Single().GetParameters().Select(p => p.ParameterType))
                     .Where(t => t.Assembly == typeof(Plugin).Assembly))
            Assert.Contains(services, d => d.ServiceType == dependency);
    }

    // BTCPay adds the plugin assembly as an MVC application part, so every public controller in it is routed
    // whether or not the plugin registers anything.
    [Fact]
    public void DoesNotRouteTheAnonymousSwapController()
    {
        var parts = new ApplicationPartManager();
        parts.ApplicationParts.Add(new AssemblyPart(typeof(Plugin).Assembly));
        parts.FeatureProviders.Add(new ControllerFeatureProvider());
        var feature = new ControllerFeature();
        parts.PopulateFeature(feature);

        var controllers = feature.Controllers.Select(t => t.AsType()).ToList();
        Assert.Contains(typeof(SatoraPluginController), controllers);
        Assert.DoesNotContain(typeof(SatoraSwapController), controllers);
    }
}
