using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;

namespace BTCPayServer.Plugins.Satora.Services;

// What the store hears about its wallet's automatic withdrawal and settling. Shaped like
// SatoraLightningNotification.
public class SatoraWalletNotification : BaseNotification
{
    private const string TYPE = "satora-wallet-automation";

    public enum Kinds
    {
        // Sent for every new destination, the first included, so a change nobody in the store made is seen.
        DestinationChanged,
        AutoWithdrawFailing,
        AutoWithdrawInterrupted,
        SettleFailing
    }

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options) : NotificationHandler<SatoraWalletNotification>
    {
        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta => [(TYPE, "Satora automatic withdrawal and settling")];

        protected override void FillViewModel(SatoraWalletNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            vm.Body = Body(notification);
            vm.ActionLink = linkGenerator.GetPathByAction("Index", "SatoraWithdraw", new { storeId = notification.StoreId }, options.RootPath);
        }
    }

    public static string Body(SatoraWalletNotification notification) => notification.Kind switch
    {
        Kinds.DestinationChanged =>
            $"Automatic withdrawals from the store's Satora wallet now go to {notification.Destination}. If no one who manages the store made this change, turn automatic withdrawal off on the withdrawals page and check who can change the store's settings.",
        Kinds.AutoWithdrawFailing =>
            $"Automatic withdrawals from the store's Satora wallet to {notification.Destination} failed twice in a row ({notification.Error}). Nothing was sent, and the funds are still in the wallet. BTCPay keeps retrying, at most 6 hours apart.",
        Kinds.AutoWithdrawInterrupted =>
            $"An automatic withdrawal from the store's Satora wallet to {notification.Destination} was interrupted, most likely by a BTCPay restart, and its outcome is unknown. Check the wallet's balance and mark it sent or failed on the withdrawals page. No withdrawal, automatic or manual, can start until then.",
        _ =>
            $"Settling the recoverable funds of the store's Satora wallet failed twice in a row ({notification.Error}). Check that the Arkade server is reachable. Until the funds are settled they cannot be withdrawn, and getting them back depends on the Arkade server. BTCPay keeps retrying, at most 6 hours apart."
    };

    public string StoreId { get; set; } = null!;
    public Kinds Kind { get; set; }
    public string? Destination { get; set; }
    // Why the last attempt failed.
    public string? Error { get; set; }
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
