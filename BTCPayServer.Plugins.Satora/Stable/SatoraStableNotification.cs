using System.Globalization;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Services.Notifications;

namespace BTCPayServer.Plugins.Satora.Stable;

// Shaped like SatoraChainNotification.
public class SatoraStableNotification : BaseNotification
{
    private const string TYPE = "satora-stable-swap";

    public enum Problems
    {
        FundingFailing,
        Overpaid,
        NeedsSweep,
        GasNeeded,
        SweepFailing,
        NeedsHtlcRefund,
        ClaimFailing,
        NearClaimCutoff,
        ClaimUnverified,
        UnexpectedStatus,
        UnknownToServer,
        StatusUnavailable
    }

    internal class Handler(LinkGenerator linkGenerator, BTCPayServerOptions options) : NotificationHandler<SatoraStableNotification>
    {
        public override string NotificationType => TYPE;

        public override (string identifier, string name)[] Meta => [(TYPE, "Satora stablecoin payment needs attention")];

        protected override void FillViewModel(SatoraStableNotification notification, NotificationViewModel vm)
        {
            vm.Identifier = notification.Identifier;
            vm.Type = notification.NotificationType;
            vm.StoreId = notification.StoreId;
            // BTCPay sets Created, when the alert was sent, before it calls this (NotificationManager.ToViewModel).
            vm.Body = Body(notification, vm.Created);
            // The plugin's own page lists no swaps; the refunds page lists those it names.
            vm.ActionLink = notification.Problem is Problems.NeedsSweep or Problems.GasNeeded or Problems.SweepFailing or Problems.NeedsHtlcRefund
                ? linkGenerator.GetPathByAction("Index", "SatoraStableRefund", new { storeId = notification.StoreId }, options.RootPath)
                : notification.InvoiceId is { } invoiceId
                    ? linkGenerator.GetPathByAction("Invoice", "UIInvoice", new { invoiceId }, options.RootPath)
                    : linkGenerator.GetPathByAction("Index", "SatoraPlugin", new { storeId = notification.StoreId }, options.RootPath);
        }
    }

    // An alert sent at or after the claim deadline, as for a claim still being finished then, says it passed.
    public static string Body(SatoraStableNotification notification, DateTimeOffset created)
    {
        string At(DateTimeOffset? time, string otherwise) => time?.ToString("u", CultureInfo.InvariantCulture) ?? otherwise;
        var cutoff = At(notification.Deadline, "its claim deadline");
        // Alerts sent before they named the invoice name only the swap.
        var id = notification.InvoiceId is null ? notification.SwapId : $"{notification.SwapId} of invoice {notification.InvoiceId}";
        var swap = $"Satora stablecoin swap {id}";
        return notification.Problem switch
        {
            Problems.FundingFailing =>
                $"The deposit for {swap} has arrived, but funding the swap failed twice. BTCPay keeps retrying until {At(notification.Deadline, "the swap's funding cutoff")}. After that, the deposit is listed on the Stablecoin refunds page.",
            Problems.Overpaid =>
                $"The customer sent {notification.Amount} more than {swap} asked for. The extra stays at the deposit address, which only the store's Satora wallet controls. If it is a hundredth of a token or more, refund it from the Stablecoin refunds page once the swap completes.",
            Problems.NeedsSweep =>
                $"Tokens at the deposit address of {swap} will not pay the invoice ({SatoraStableActions.Describe(notification.Status)}). They stay at that address, which only the store's Satora wallet controls. To refund them, enter the customer's address on the Stablecoin refunds page and send the deposit address a little gas.",
            Problems.GasNeeded =>
                $"Refunding the tokens at the deposit address of {swap} needs gas at that address, so send it about {notification.Amount} from any wallet. The refund goes out within a minute after the gas arrives.",
            Problems.SweepFailing =>
                $"Refunding the tokens at the deposit address of {swap} failed twice. BTCPay keeps retrying every minute, and its log has the reason, such as an EVM node that does not answer.",
            Problems.NeedsHtlcRefund =>
                $"The customer's deposit for {swap} is locked in the swap contract and will not pay the invoice ({SatoraStableActions.Describe(notification.Status)}). It can be refunded from {At(notification.Deadline, "a day after the swap was created")}, in tBTC rather than the token the customer sent. To make that refund, contact Satora support at support@satora.io with the swap id.",
            Problems.ClaimFailing =>
                $"Claiming {swap} into the Satora wallet failed twice. Check that the Arkade server and the Arbitrum node are reachable, and see BTCPay's log for the reason. BTCPay keeps retrying until {cutoff}. If the exchange delivered less than due by more than the invoice's payment tolerance, the claim is refused every time, and the log says so.",
            Problems.NearClaimCutoff when created >= notification.Deadline =>
                $"{swap} is funded but was not claimed into the Satora wallet by its claim deadline, {cutoff}. Unless a claim already in progress completes, the customer's deposit has to be refunded from the swap contract.",
            Problems.NearClaimCutoff =>
                $"{swap} is funded but not yet claimed into the Satora wallet. If it is not claimed by {cutoff}, the customer's deposit has to be refunded from the swap contract.",
            Problems.ClaimUnverified =>
                $"The Satora swap server reports stablecoin swap {id} as claimed, but the Satora wallet has no receipt for the claim and cannot start a new one. If the invoice does not settle on its own, check the wallet's balance before you mark it settled.",
            Problems.UnknownToServer =>
                $"The Satora swap server does not know stablecoin swap {id}. Nothing was moved. After {cutoff}, anything at its deposit address is listed on the Stablecoin refunds page.",
            Problems.StatusUnavailable =>
                $"The status of the store's Satora stablecoin payments could not be read for half an hour, so none are funded, recorded or claimed. Check that the Satora swap server is reachable before the earliest claim deadline, {cutoff}.",
            _ => $"{swap} reached an unexpected status, {notification.Status}. Nothing was moved. Contact Satora support at support@satora.io with the swap id."
        };
    }

    public string StoreId { get; set; } = null!;
    // Null for a problem with the store's swaps as a whole.
    public string? SwapId { get; set; }
    // The swap's invoice; null for the store's swaps as a whole, and in alerts sent before it was named.
    public string? InvoiceId { get; set; }
    public Problems Problem { get; set; }
    // The swap server's status, or why the swap needs the store.
    public string? Status { get; set; }
    // The claim cutoff, the funding cutoff, or for an HTLC refund the time it becomes possible.
    public DateTimeOffset? Deadline { get; set; }
    // What an overpayment sent beyond the deposit, in whole tokens with the symbol, or the gas a sweep needs.
    public string? Amount { get; set; }
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
