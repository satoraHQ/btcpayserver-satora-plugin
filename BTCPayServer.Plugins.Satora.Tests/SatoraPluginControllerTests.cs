using System.Reflection;
using System.Security.Claims;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraPluginControllerTests
{
    private static readonly MethodInfo[] Posts = typeof(SatoraPluginController).GetMethods()
        .Where(m => m.GetCustomAttributes<HttpPostAttribute>().Any())
        .ToArray();

    // MVC reads the form before the route, so a storeId in the body would win over an unannotated parameter.
    [Fact]
    public async Task PostsTakeTheStoreFromTheRouteOnly()
    {
        var form = new Dictionary<string, StringValues>
        {
            ["storeId"] = "victim",
            ["Enabled"] = "true",
            ["SeedConfirmation"] = "words"
        };
        Assert.NotEmpty(Posts);
        foreach (var post in Posts)
        {
            var bound = await Bind(post, routeStoreId: "own", form);

            Assert.True(bound.TryGetValue("storeId", out var storeId), post.Name);
            Assert.Equal("own", storeId);
            foreach (var model in post.GetParameters().Select(p => p.ParameterType).Where(t => t.IsClass && t != typeof(string)))
            {
                Assert.NotEqual(typeof(SatoraSettings), model);
                Assert.Null(model.GetProperty("StoreId", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
            }
        }
    }

    [Fact]
    public async Task SettingsPostBindsTheViewModel()
    {
        var post = Posts.Single(p => p.Name == nameof(SatoraPluginController.Index));
        var bound = await Bind(post, "own", new Dictionary<string, StringValues> { ["Enabled"] = "true" });

        Assert.True(Assert.IsType<SatoraSettingsViewModel>(bound["vm"]).Enabled);
    }

    // The wallet service has no database or registry behind it: an action that got past the hot
    // wallet check would throw or show up in its reads.
    [Fact]
    public async Task HotWalletActionsAreNotFoundWithoutHotWalletPermission()
    {
        var wallets = new RecordingWalletService();
        var controller = Controller(admin: false, allowHotWalletForAll: false, wallets);

        Assert.IsType<NotFoundResult>(await controller.GenerateWallet("store", new SatoraGenerateWalletViewModel()));
        Assert.IsType<NotFoundResult>(await controller.RestoreWallet("store", new SatoraRestoreWalletViewModel()));
        Assert.IsType<NotFoundResult>(await controller.ConfirmBackup("store", new SatoraConfirmBackupViewModel()));
        Assert.IsType<NotFoundResult>(await controller.ViewSeed("store"));
        Assert.IsType<NotFoundResult>(await controller.Index("store", new SatoraSettingsViewModel { Enabled = true }));
        Assert.Empty(wallets.StatusReads);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task HotWalletPermissionComesFromAdminOrServerPolicy(bool admin, bool allowHotWalletForAll)
    {
        var wallets = new RecordingWalletService();
        var controller = Controller(admin, allowHotWalletForAll, wallets);

        var result = await controller.ViewSeed("store");

        Assert.Equal(["store"], wallets.StatusReads);
        Assert.Equal(nameof(SatoraPluginController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
    }

    private static SatoraPluginController Controller(bool admin, bool allowHotWalletForAll, SatoraWalletService wallets)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user") };
        if (admin)
            claims.Add(new Claim(ClaimTypes.Role, Roles.ServerAdmin));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemes.Cookie));
        var http = new DefaultHttpContext { User = user };
        return new SatoraPluginController(null!, wallets, null!, new PoliciesSettings { AllowHotWalletForAll = allowHotWalletForAll })
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new DiscardingTempDataProvider())
        };
    }

    // A wallet whose seed cannot be read, so ViewSeed redirects back to the settings page.
    private sealed class RecordingWalletService() : SatoraWalletService(null!, null!, null!, NullLogger<SatoraWalletService>.Instance)
    {
        public List<string> StatusReads { get; } = [];

        public override Task<SatoraWalletStatus> GetStatusAsync(string storeId)
        {
            StatusReads.Add(storeId);
            return Task.FromResult(new SatoraWalletStatus(SatoraWalletState.Locked, null, false));
        }
    }

    private sealed class DiscardingTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }

    private static async Task<Dictionary<string, object?>> Bind(MethodInfo action, string routeStoreId, Dictionary<string, StringValues> form)
    {
        var services = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider();
        var http = new DefaultHttpContext { RequestServices = services };
        http.Request.Method = "POST";
        http.Request.ContentType = "application/x-www-form-urlencoded";
        http.Request.Form = new FormCollection(form);
        var routeData = new RouteData();
        routeData.Values["storeId"] = routeStoreId;
        var context = new ControllerContext(new ActionContext(http, routeData, new ControllerActionDescriptor()))
        {
            ValueProviderFactories = services.GetRequiredService<IOptions<MvcOptions>>().Value.ValueProviderFactories.ToList()
        };
        var valueProvider = await CompositeValueProvider.CreateAsync(context);
        var metadataProvider = (ModelMetadataProvider)services.GetRequiredService<IModelMetadataProvider>();
        var binderFactory = services.GetRequiredService<IModelBinderFactory>();
        var parameterBinder = services.GetRequiredService<ParameterBinder>();

        var values = new Dictionary<string, object?>();
        foreach (var parameter in action.GetParameters())
        {
            var descriptor = new ControllerParameterDescriptor
            {
                Name = parameter.Name!,
                ParameterType = parameter.ParameterType,
                ParameterInfo = parameter,
                BindingInfo = BindingInfo.GetBindingInfo(parameter.GetCustomAttributes())
            };
            var metadata = metadataProvider.GetMetadataForParameter(parameter);
            var binder = binderFactory.CreateBinder(new ModelBinderFactoryContext
            {
                BindingInfo = descriptor.BindingInfo,
                Metadata = metadata,
                CacheToken = descriptor
            });
            var result = await parameterBinder.BindModelAsync(context, binder, valueProvider, descriptor, metadata, value: null, container: null);
            values[parameter.Name!] = result.Model;
        }
        return values;
    }
}
