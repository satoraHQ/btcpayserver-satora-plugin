using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
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
        Assert.DoesNotContain(extensions, e => e.Location.StartsWith("checkout", StringComparison.Ordinal));
        Assert.Contains(typeof(PluginMigrationRunner), hostedServices);
        Assert.DoesNotContain(typeof(SatoraSwapWatcher), hostedServices);
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
