using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Kinds = BTCPayServer.Plugins.Satora.Services.SatoraWalletNotification.Kinds;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraWalletNotificationTests
{
    [Theory]
    [InlineData(Kinds.DestinationChanged, "now go to tark1destination. If no one who manages the store made this change, turn automatic withdrawal off")]
    [InlineData(Kinds.AutoWithdrawFailing, "to tark1destination failed twice in a row (fee too high). Nothing was sent")]
    [InlineData(Kinds.AutoWithdrawInterrupted, "to tark1destination was interrupted")]
    [InlineData(Kinds.SettleFailing, "Settling the recoverable funds of the store's Satora wallet failed twice in a row (fee too high).")]
    public void SaysWhatHappenedAndLinksTheWithdrawalsPage(Kinds kind, string says)
    {
        INotificationHandler handler = new SatoraWalletNotification.Handler(new RouteLinks(), new BTCPayServerOptions { RootPath = "/" });
        var vm = new NotificationViewModel();

        handler.FillViewModel(new SatoraWalletNotification { StoreId = "store", Kind = kind, Destination = "tark1destination", Error = "fee too high" }, vm);

        Assert.Contains(says, vm.Body);
        Assert.Equal("SatoraWithdraw/Index/store", vm.ActionLink);
        Assert.Equal("store", vm.StoreId);
    }

    // BTCPay shows a notification only through the handler registered for its type.
    [Fact]
    public void RegistersAHandlerForEveryNotificationThePluginSends()
    {
        var services = new ServiceCollection();
        new Plugin().Execute(services);
        var handled = services.Where(d => d.ServiceType == typeof(INotificationHandler))
            .Select(d => d.ImplementationType!.BaseType!.GetGenericArguments().Single())
            .ToList();

        var notifications = typeof(Plugin).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(BaseNotification))).ToList();

        Assert.Contains(typeof(SatoraWalletNotification), notifications);
        Assert.All(notifications, n => Assert.Contains(n, handled));
    }
}
