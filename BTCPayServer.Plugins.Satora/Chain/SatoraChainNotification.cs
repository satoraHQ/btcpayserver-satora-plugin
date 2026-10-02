using System.Globalization;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;

namespace BTCPayServer.Plugins.Satora.Chain;

// Shaped like SatoraLightningNotification.
public class SatoraChainNotification : BaseNotification
{
    private const string TYPE = "satora-chain-swap";

    public enum Problems
    {
        NeedsRefund,
        LeftoverOutputs,
        ClaimFailing,
        NearClaimCutoff,
        ClaimUnverified,
        UnexpectedStatus,
        UnknownToServer,
        StatusUnavailable,
        RefundFailing,
        NothingToRefund,
        Overpaid,
        RefundHeld,
        PaidAfterPaidInFull
    }

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options) : NotificationHandler<SatoraChainNotification>
    {
        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta => [(TYPE, "Satora on-chain payment needs attention")];

        protected override void FillViewModel(SatoraChainNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            // BTCPay sets Created, when the alert was sent, before it calls this (NotificationManager.ToViewModel).
            vm.Body = Body(notification, vm.Created);
            // The plugin's own page lists no on-chain swaps; the refunds page lists those it names.
            vm.ActionLink = notification.Problem is Problems.NeedsRefund or Problems.LeftoverOutputs or Problems.RefundFailing or Problems.RefundHeld
                ? linkGenerator.GetPathByAction("Index", "SatoraChainRefund", new { storeId = notification.StoreId }, options.RootPath)
                : notification.InvoiceId is { } invoiceId
                    ? linkGenerator.GetPathByAction("Invoice", "UIInvoice", new { invoiceId }, options.RootPath)
                    : linkGenerator.GetPathByAction("Index", "SatoraPlugin", new { storeId = notification.StoreId }, options.RootPath);
        }
    }

    // An alert sent at or after the claim deadline, as for a claim still being finished then, says it passed.
    public static string Body(SatoraChainNotification notification, DateTimeOffset created)
    {
        string At(DateTimeOffset? time, string otherwise) => time?.ToString("u", CultureInfo.InvariantCulture) ?? otherwise;
        var cutoff = At(notification.Deadline, "its claim deadline");
        // Alerts sent before they named the invoice name only the swap.
        var swap = notification.InvoiceId is null ? $"swap {notification.SwapId}" : $"swap {notification.SwapId} of invoice {notification.InvoiceId}";
        return notification.Problem switch
        {
            Problems.NeedsRefund =>
                $"The customer's bitcoin for Satora on-chain {swap} has to be refunded ({SatoraChainRefunds.Describe(notification.Status)}) and does not pay the invoice. Enter the customer's refund address on the On-chain refunds page. The refund goes out from about {At(notification.Deadline, "a day after the swap was created")}, and the address can be changed until then.",
            Problems.LeftoverOutputs =>
                $"Bitcoin is still at the address of Satora on-chain {swap} after the swap ended, most likely from a second payment. Enter the customer's refund address on the On-chain refunds page. The refund goes out from about {At(notification.Deadline, "a day after the swap was created")}, and the address can be changed until then.",
            Problems.ClaimFailing =>
                $"Claiming Satora on-chain {swap} into the Satora wallet failed twice. Check that the Arkade server is reachable. BTCPay keeps retrying until {cutoff}.",
            Problems.NearClaimCutoff when created >= notification.Deadline =>
                $"Satora on-chain {swap} is paid but was not claimed into the Satora wallet by its claim deadline, {cutoff}. Unless a claim already in progress completes, the customer's bitcoin has to be refunded.",
            Problems.NearClaimCutoff =>
                $"Satora on-chain {swap} is paid but not yet claimed into the Satora wallet. If it is not claimed by {cutoff}, the customer's bitcoin has to be refunded.",
            Problems.ClaimUnverified =>
                $"The Satora swap server reports on-chain {swap} as claimed, but the Satora wallet has no receipt for the claim and cannot start a new one. If the invoice does not settle on its own, check the wallet's balance before you mark it settled.",
            Problems.UnknownToServer =>
                $"The Satora swap server does not know on-chain {swap}. Nothing was moved. After {cutoff}, any bitcoin at its address is listed on the On-chain refunds page.",
            Problems.StatusUnavailable =>
                $"The status of the store's Satora on-chain payments could not be read for half an hour, so none are recorded or claimed. Check that the Satora swap server is reachable before the earliest claim deadline, {cutoff}.",
            Problems.RefundFailing =>
                $"Refunding Satora on-chain {swap} failed twice. Check that the esplora server is reachable and that the address still holds the customer's bitcoin. BTCPay keeps retrying.",
            Problems.NothingToRefund when notification.Status == SatoraChainRefunds.LeftBehindStatus =>
                $"The refund of Satora on-chain {swap} confirmed, but its address now holds less than the network fee to refund it, most likely from a later payment. The swap is closed without sending it. To refund it after all, a server admin can list the swap on the On-chain refunds page again by running this in BTCPay's database: {SatoraChainRefunds.ReopenStatement(notification.SwapId!)};",
            Problems.NothingToRefund =>
                $"Satora on-chain {swap} closed without a refund because its address {(notification.Status == SatoraChainRefunds.EmptyReason ? "holds nothing" : "holds less than the network fee to refund it")}. To refund it after all, a server admin can list the swap on the On-chain refunds page again by running this in BTCPay's database: {SatoraChainRefunds.ReopenStatement(notification.SwapId!)};",
            Problems.Overpaid =>
                $"The customer paid {notification.ExcessSats} sats more than Satora on-chain {swap} asked for. Only the amount due reached the Satora wallet, and the swap server kept the rest. Settle the difference with the customer.",
            Problems.PaidAfterPaidInFull =>
                $"Satora on-chain {swap} was claimed into the Satora wallet after the invoice's other payments had paid it in full. Its {notification.ExcessSats} sats are credited on the invoice and are in the wallet, but BTCPay does not show a settled invoice as overpaid. Settle them with the customer.",
            Problems.RefundHeld =>
                $"The refund of Satora on-chain {swap} is on hold because the Satora wallet can still complete its claim. This is checked again before each refund attempt. If the refund stays on hold, contact Satora support at support@satora.io.",
            _ => $"Satora on-chain {swap} reached an unexpected status, {notification.Status}. Nothing was moved. Contact Satora support at support@satora.io with the swap id."
        };
    }

    public string StoreId { get; set; } = null!;
    // Null for a problem with the store's swaps as a whole.
    public string? SwapId { get; set; }
    // The swap's invoice; null for the store's swaps as a whole, and in alerts sent before it was named.
    public string? InvoiceId { get; set; }
    public Problems Problem { get; set; }
    public string? Status { get; set; }
    // The claim cutoff, or for a refund its locktime, when the plugin first tries it.
    public DateTimeOffset? Deadline { get; set; }
    // What an overpayment paid beyond the deposit, or for a swap claimed after the invoice was paid
    // in full, what reached the store's wallet.
    public long? ExcessSats { get; set; }
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
