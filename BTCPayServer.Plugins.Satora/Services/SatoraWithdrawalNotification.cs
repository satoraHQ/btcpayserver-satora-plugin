using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;

namespace BTCPayServer.Plugins.Satora.Services;

// Shaped like SatoraWalletLockedNotification.
public class SatoraWithdrawalNotification : BaseNotification
{
    private const string TYPE = "satora-withdrawal-unknown";

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options) : NotificationHandler<SatoraWithdrawalNotification>
    {
        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta => [(TYPE, "Satora withdrawal outcome unknown")];

        protected override void FillViewModel(SatoraWithdrawalNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = Body(notification);
            vm.ActionLink = linkGenerator.GetPathByAction("Index", "SatoraWithdraw", new { storeId = notification.StoreId }, options.RootPath);
        }
    }

    public static string Body(SatoraWithdrawalNotification notification) =>
        $"The outcome of a withdrawal from the store's Satora wallet to {notification.Destination} is unknown. " +
        "Check the wallet's balance and mark it sent or failed on the withdrawals page. No new withdrawal can start until then.";

    public string StoreId { get; set; } = null!;
    public string WithdrawalId { get; set; } = null!;
    public string Destination { get; set; } = null!;
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
