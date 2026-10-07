using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
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
using Satora.Sdk;
using Xunit;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraPluginControllerTests
{
    private static readonly MethodInfo[] Posts = typeof(SatoraPluginController).GetMethods()
        .Concat(typeof(SatoraWithdrawController).GetMethods())
        .Concat(typeof(SatoraChainRefundController).GetMethods())
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

    // Withdrawing from an existing wallet, or refunding a customer, needs no hot wallet rights, but BTCPay
    // must check the store settings permission on the route's store before any such action runs.
    [Theory]
    [InlineData(typeof(SatoraWithdrawController))]
    [InlineData(typeof(SatoraChainRefundController))]
    public void WithdrawalsNeedTheStoreSettingsPermissionAndAnAntiforgeryToken(Type controller)
    {
        Assert.Equal([Policies.CanModifyStoreSettings], controller.GetCustomAttributes<AuthorizeAttribute>().Select(a => a.Policy));
        Assert.NotNull(controller.GetCustomAttribute<AutoValidateAntiforgeryTokenAttribute>());
        Assert.All(controller.GetMethods(), action =>
        {
            Assert.Null(action.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.Null(action.GetCustomAttribute<IgnoreAntiforgeryTokenAttribute>());
        });
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
        Assert.IsType<NotFoundResult>(await controller.RotateLightningKey("store"));
        Assert.IsType<NotFoundResult>(await controller.UseAsLightningNode("store", new SatoraUseAsLightningNodeViewModel { ReplaceConfirmation = SeedWriteGuard.ConfirmationWord }));
        Assert.IsType<NotFoundResult>(await controller.Index("store", new SatoraSettingsViewModel { Enabled = true }));
        Assert.Empty(wallets.StatusReads);
        Assert.Empty(wallets.LightningNodeReplaces);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("replace", false)]
    [InlineData(SeedWriteGuard.ConfirmationWord, true)]
    public async Task ReplacingTheStoresLightningNodeNeedsTheConfirmationWord(string? confirmation, bool replaced)
    {
        var wallets = new RecordingWalletService();
        var controller = Controller(admin: true, allowHotWalletForAll: false, wallets);

        var result = await controller.UseAsLightningNode("store", new SatoraUseAsLightningNodeViewModel { ReplaceConfirmation = confirmation });

        Assert.Equal(nameof(SatoraPluginController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal(replaced ? ["store"] : [], wallets.LightningNodeReplaces);
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

    // Asked to turn on a coin the store does not offer yet, a user without hot wallet rights changes
    // nothing; anyone with the store's settings permission can turn coins off.
    [Fact]
    public async Task TurningAStablecoinOnNeedsHotWalletRightsAndTurningOneOffDoesNot()
    {
        var wallets = new RecordingWalletService();
        var quotes = new SatoraStableFakes.Quotes();
        var controller = Controller(admin: false, allowHotWalletForAll: false, wallets, quotes, paymentTolerance: 5);

        Assert.IsType<NotFoundResult>(await controller.Stablecoins("store", Methods(SatoraStablePaymentMethod.UsdcArbitrum)));
        Assert.Empty(wallets.StablecoinSaves);
        wallets.Stablecoins = [SatoraStablePaymentMethod.UsdcArbitrum];
        Assert.IsType<RedirectToActionResult>(await controller.Stablecoins("store", Methods(SatoraStablePaymentMethod.UsdcArbitrum)));
        Assert.IsType<RedirectToActionResult>(await controller.Stablecoins("store", Methods()));

        Assert.Equal([[SatoraStablePaymentMethod.UsdcArbitrum], []], wallets.StablecoinSaves);
        Assert.Empty(quotes.Reads);
    }

    // The swap's fees on a typical payment are 0.25% plus 300 sats of 100 000, which the 1% floor covers;
    // at 1.3% plus 2000 sats they are 3.3%.
    [Theory]
    [InlineData(0.0025, 300, 0.99, false)]
    [InlineData(0.0025, 300, 1, true)]
    [InlineData(0.013, 2000, 3.29, false)]
    [InlineData(0.013, 2000, 3.3, true)]
    public async Task AStablecoinIsTurnedOnOnlyWhileThePaymentToleranceCoversTheSwapsFees(double feeRate, long networkFee, double tolerance, bool saved)
    {
        var wallets = new RecordingWalletService();
        var quotes = new SatoraStableFakes.Quotes { Quote = SatoraStableFakes.Quote(protocolFeeRate: feeRate, networkFee: (ulong)networkFee) };
        var controller = Controller(admin: true, allowHotWalletForAll: false, wallets, quotes, tolerance);

        await controller.Stablecoins("store", Methods(SatoraStablePaymentMethod.UsdcArbitrum));

        Assert.Equal(saved ? [[SatoraStablePaymentMethod.UsdcArbitrum]] : [], wallets.StablecoinSaves);
        Assert.Equal([SatoraStablePaymentMethod.UsdcArbitrum], quotes.Reads);
        if (!saved)
            Assert.Contains($"at least {(networkFee == 300 ? "1" : "3.3")}%", controller.TempData[WellKnownTempData.ErrorMessage] as string);
    }

    [Fact]
    public async Task AStablecoinWhoseFeesCannotBeReadIsNotTurnedOn()
    {
        var wallets = new RecordingWalletService();
        var quotes = new SatoraStableFakes.Quotes { Failure = new SdkException(SdkErrorKind.Transport, "unreachable") };
        var controller = Controller(admin: true, allowHotWalletForAll: false, wallets, quotes, paymentTolerance: 50);

        await controller.Stablecoins("store", Methods(SatoraStablePaymentMethod.UsdcArbitrum));

        Assert.Empty(wallets.StablecoinSaves);
        Assert.Contains("could not be read", controller.TempData[WellKnownTempData.ErrorMessage] as string);
    }

    // The page asks the swap server nothing: a coin's floor comes from the last quote read, and is the
    // 1% minimum before any. These fees, 1.3% and 2000 sats of 100 000, make it 3.3%.
    [Theory]
    [InlineData(false, false, 0, null)]
    [InlineData(true, false, 0.99, 1.0)]
    [InlineData(true, false, 1, null)]
    [InlineData(true, true, 3.29, 3.3)]
    [InlineData(true, true, 3.3, null)]
    public async Task ThePageWarnsWhileACoinIsOnAndThePaymentToleranceIsBelowItsFloor(bool on, bool quoteRead, double tolerance, double? floor)
    {
        var quotes = new SatoraStableFakes.Quotes { Last = quoteRead ? SatoraStableFakes.Quote(protocolFeeRate: 0.013, networkFee: 2000) : null };
        var plugin = new FixedPluginService(new SatoraModel { StoreId = "store", Stablecoins = on ? [SatoraStablePaymentMethod.UsdcArbitrum] : [] });
        var controller = Controller(admin: true, allowHotWalletForAll: false, new RecordingWalletService(), quotes, tolerance, plugin);

        var model = Assert.IsType<SatoraModel>(Assert.IsType<ViewResult>(await controller.Index("store")).Model);

        var expected = floor is { } f ? new List<(SatoraStablecoin, decimal)> { (SatoraStablePaymentMethod.UsdcArbitrum, (decimal)f) } : [];
        Assert.Equal(expected, model.StablecoinToleranceShortfalls);
        Assert.Empty(quotes.Reads);
    }

    // ark-client refuses to settle a recoverable total below dust, so the withdrawals page offers its
    // settle only from dust up.
    [Theory]
    [InlineData(330UL, true)]
    [InlineData(329UL, false)]
    [InlineData(0UL, false)]
    public async Task TheWalletBalanceSaysWhetherItsRecoverableFundsCanBeSettled(ulong recoverable, bool canSettle)
    {
        var wallets = new RecordingWalletService { Balance = new ArkadeBalance(1_000, 0, recoverable) };
        var controller = Controller(admin: false, allowHotWalletForAll: false, wallets);

        var balance = JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(await controller.WalletBalance("store")).Value);

        Assert.Equal(recoverable, balance.GetProperty("recoverableSats").GetUInt64());
        Assert.Equal(canSettle, balance.GetProperty("canSettle").GetBoolean());
    }

    private static SatoraStablecoinsViewModel Methods(params SatoraStablecoin[] coins) => new() { Methods = coins.Select(c => c.Id.ToString()).ToList() };

    private static SatoraPluginController Controller(bool admin, bool allowHotWalletForAll, SatoraWalletService wallets,
        SatoraStableQuotes? quotes = null, double paymentTolerance = 0, SatoraPluginService? plugin = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user") };
        if (admin)
            claims.Add(new Claim(ClaimTypes.Role, Roles.ServerAdmin));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationSchemes.Cookie));
        var http = new DefaultHttpContext { User = user };
        var store = new StoreData { Id = "store" };
        store.SetStoreBlob(new StoreBlob { PaymentTolerance = paymentTolerance });
        http.SetStoreData(store);
        return new SatoraPluginController(plugin!, wallets, null!, new PoliciesSettings { AllowHotWalletForAll = allowHotWalletForAll },
            quotes!, NullLogger<SatoraPluginController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new DiscardingTempDataProvider())
        };
    }

    // A wallet whose seed cannot be read, so ViewSeed redirects back to the settings page.
    private sealed class FixedPluginService(SatoraModel model) : SatoraPluginService(null!, null!)
    {
        public override Task<SatoraModel> GetStoreData(string storeId) => Task.FromResult(model);
    }

    private sealed class RecordingWalletService() : SatoraWalletService(null!, null!, null!, null!, null!, null!, null!, null!, NullLogger<SatoraWalletService>.Instance)
    {
        public List<string> StatusReads { get; } = [];

        public List<string> LightningNodeReplaces { get; } = [];

        public override Task<SatoraWalletStatus> GetStatusAsync(string storeId)
        {
            StatusReads.Add(storeId);
            return Task.FromResult(new SatoraWalletStatus(SatoraWalletState.Locked, null, false));
        }

        public override Task<bool> UseAsLightningNodeAsync(string storeId)
        {
            LightningNodeReplaces.Add(storeId);
            return Task.FromResult(true);
        }

        public ArkadeBalance? Balance { get; set; }

        public override Task<ArkadeBalance?> TryGetBalanceAsync(string storeId) => Task.FromResult(Balance);

        public List<SatoraStablecoin> Stablecoins { get; set; } = [];

        public List<List<SatoraStablecoin>> StablecoinSaves { get; } = [];

        public override Task<IReadOnlyList<SatoraStablecoin>> GetStablecoinsAsync(string storeId) =>
            Task.FromResult<IReadOnlyList<SatoraStablecoin>>(Stablecoins);

        public override Task<bool> SetStablecoinsAsync(string storeId, IReadOnlyCollection<SatoraStablecoin> coins)
        {
            StablecoinSaves.Add(coins.ToList());
            return Task.FromResult(true);
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
