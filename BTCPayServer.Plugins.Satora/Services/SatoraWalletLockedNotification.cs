using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;

namespace BTCPayServer.Plugins.Satora.Services;

// Shaped like BTCPay's ExternalPayoutTransactionNotification.
public class SatoraWalletLockedNotification : BaseNotification
{
    private const string TYPE = "satora-wallet-locked";

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options) : NotificationHandler<SatoraWalletLockedNotification>
    {
        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta => [(TYPE, "Satora wallet locked")];

        protected override void FillViewModel(SatoraWalletLockedNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = "The store's Satora wallet seed can no longer be decrypted, most likely because BTCPay's data protection keys changed. Restore the wallet from its recovery phrase.";
            vm.ActionLink = linkGenerator.GetPathByAction("Index", "SatoraPlugin", new { storeId = notification.StoreId }, options.RootPath);
        }
    }

    public string StoreId { get; set; } = null!;
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
