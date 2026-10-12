using System.Globalization;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;

namespace BTCPayServer.Plugins.Satora.Lightning;

// Shaped like SatoraWalletLockedNotification.
public class SatoraLightningNotification : BaseNotification
{
    private const string TYPE = "satora-lightning-swap";

    public enum Problems
    {
        ClaimFailing,
        NearClaimCutoff,
        UnexpectedStatus,
        UnknownToServer,
        RefundedUnclaimed,
        ClaimUnverified,
        StatusUnavailable
    }

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options) : NotificationHandler<SatoraLightningNotification>
    {
        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta => [(TYPE, "Satora Lightning payment needs attention")];

        protected override void FillViewModel(SatoraLightningNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            // BTCPay sets Created, when the alert was sent, before it calls this (NotificationManager.ToViewModel).
            vm.Body = Body(notification, vm.Created);
            // The plugin's own page lists no swaps.
            vm.ActionLink = notification.InvoiceId is { } invoiceId
                ? linkGenerator.GetPathByAction("Invoice", "UIInvoice", new { invoiceId }, options.RootPath)
                : linkGenerator.GetPathByAction("Index", "SatoraPlugin", new { storeId = notification.StoreId }, options.RootPath);
        }
    }

    // An alert sent at or after the claim deadline, as for a claim still being finished then, says it passed.
    public static string Body(SatoraLightningNotification notification, DateTimeOffset created)
    {
        var cutoff = notification.ClaimCutoff?.ToString("u", CultureInfo.InvariantCulture) ?? "its claim deadline";
        var swap = notification.InvoiceId is null ? $"swap {notification.SwapId}" : $"swap {notification.SwapId} of invoice {notification.InvoiceId}";
        return notification.Problem switch
        {
            Problems.ClaimFailing =>
                $"Claiming the Lightning payment of Satora {swap} into the Satora wallet failed twice. Check that the Arkade server is reachable. BTCPay keeps retrying until {cutoff}.",
            Problems.NearClaimCutoff when created >= notification.ClaimCutoff =>
                $"Satora {swap} is paid but was not claimed into the Satora wallet by its claim deadline, {cutoff}. Unless a claim already in progress completes, the payment goes back to the payer and does not pay the invoice. The return can take hours.",
            Problems.NearClaimCutoff =>
                $"Satora {swap} is paid but not yet claimed into the Satora wallet. If it is not claimed by {cutoff}, the payment goes back to the payer and does not pay the invoice.",
            Problems.UnknownToServer =>
                $"The Satora swap server does not know {swap}. Nothing was moved.",
            Problems.RefundedUnclaimed =>
                $"Satora {swap} was paid, but the swap server took it back before the Satora wallet claimed it. The payment goes back to the payer and does not pay the invoice.",
            Problems.ClaimUnverified =>
                $"The Satora swap server reports {swap} as claimed, but the Satora wallet has no receipt for the claim and cannot start a new one. If the invoice does not settle on its own, check the wallet's balance before you mark it settled.",
            Problems.StatusUnavailable =>
                $"The status of the store's Satora Lightning payments could not be read for several minutes, so payments are not claimed into the wallet. Check that the Satora swap server is reachable before the earliest claim deadline, {cutoff}.",
            _ => $"Satora {swap} reached an unexpected status, {notification.Status}. Nothing was moved. Contact Satora support at support@satora.io with the swap id."
        };
    }

    public string StoreId { get; set; } = null!;
    // Null for a problem with the store's swaps as a whole.
    public string? SwapId { get; set; }
    // The invoice an incoming swap pays; null for the store's swaps as a whole, for a swap no invoice
    // tracks, and in alerts sent before it was named.
    public string? InvoiceId { get; set; }
    public Problems Problem { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? ClaimCutoff { get; set; }
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
