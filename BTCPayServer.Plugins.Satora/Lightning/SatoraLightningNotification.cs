using System.Globalization;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Models;
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
        StatusUnavailable,
        FundingUnresolved,
        RefundFailing,
        OutgoingStatusUnavailable,
        RefundedWithoutServer,
        RefundWithoutServerFailing,
        RefundLocktimeNotMature,
        FundingFailing
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
            vm.ActionLink = ClosedOnWithdrawalsPage(notification.Problem)
                ? linkGenerator.GetPathByAction("Index", "SatoraWithdraw", new { storeId = notification.StoreId }, options.RootPath)
                : notification.InvoiceId is { } invoiceId
                    ? linkGenerator.GetPathByAction("Invoice", "UIInvoice", new { invoiceId }, options.RootPath)
                    : linkGenerator.GetPathByAction("Index", "SatoraPlugin", new { storeId = notification.StoreId }, options.RootPath);
        }
    }

    public static bool ClosedOnWithdrawalsPage(Problems problem) =>
        problem is Problems.RefundedWithoutServer or Problems.RefundWithoutServerFailing or Problems.RefundLocktimeNotMature or Problems.FundingFailing;

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
            Problems.FundingUnresolved =>
                $"The Satora wallet may already have paid Lightning payment {notification.SwapId}, as an earlier attempt to fund it is not resolved yet. Do not pay its invoice another way until then. The wallet will not pay it twice.",
            Problems.RefundFailing =>
                $"Lightning payment {notification.SwapId} failed, and refunding it to the Satora wallet failed twice. BTCPay keeps retrying. From about an hour after {notification.ClaimCutoff?.ToString("u", CultureInfo.InvariantCulture) ?? "the swap's refund locktime"}, the refund is also tried without the swap server.",
            Problems.OutgoingStatusUnavailable =>
                $"The status of the store's Satora Lightning payments could not be read for several minutes, so it is unknown whether they went through. Check that the Satora swap server is reachable. From about an hour after the earliest refund locktime, {cutoff}, unpaid funds return to the Satora wallet without the swap server.",
            Problems.RefundedWithoutServer =>
                $"The funds of Lightning payment {notification.SwapId} were refunded to the Satora wallet without the swap server, so it is unknown whether the payee was paid. Ask the payee whether the payment arrived, then close it on the withdrawals page as paid or failed.",
            Problems.RefundWithoutServerFailing =>
                $"Refunding Lightning payment {notification.SwapId} to the Satora wallet without the swap server keeps failing. {Held(notification)}",
            Problems.RefundLocktimeNotMature =>
                $"Refunding Lightning payment {notification.SwapId} to the Satora wallet without the swap server is still refused {SatoraChainActions.MedianTimeLag.TotalHours:0} hours after its refund locktime, {cutoff}. Check that the esplora server is reachable and in sync. {Held(notification)}",
            Problems.FundingFailing when InRefundState(notification) =>
                $"Funding Lightning payment {notification.SwapId} from the Satora wallet keeps failing ({notification.Error}). The swap server has given up on the payment, so BTCPay was told it failed and its payout, if any, is cancelled. An earlier attempt may have funded it, so it stays open until its funds are back in the wallet. If it is still open after its refund locktime, {cutoff}, check the wallet's balance and close it on the withdrawals page.",
            Problems.FundingFailing =>
                $"Funding Lightning payment {notification.SwapId} from the Satora wallet keeps failing ({notification.Error}). Funding is retried, and BTCPay is not told the payment failed, because an earlier attempt may have paid it. If it is still open after its refund locktime, {cutoff}, check the wallet's balance and close it on the withdrawals page.",
            _ => $"Satora {swap} reached an unexpected status, {notification.Status}. Nothing was moved. Contact Satora support at support@satora.io with the swap id."
        };
    }

    // What the swap's VHTLC held when the alert was sent decides whether the store may close the payment.
    private static string Held(SatoraLightningNotification notification) => notification.HeldSats switch
    {
        null => "What the swap holds could not be read, so leave the payment open. The withdrawals page shows the amount once it can be read.",
        0 => "The swap holds nothing, so either the funds never left the wallet or the payee was paid. Check the wallet's balance and ask the payee, then close the payment on the withdrawals page as failed if the funds are still in the wallet, or as paid if the payee received it.",
        var sats => $"The swap still holds {sats} sats, which BTCPay keeps trying to refund to the wallet. Leave the payment open."
    };

    // BTCPay hears that a payment failed once the server gives its swap up (SatoraOutgoingActions.PaymentStatus).
    private static bool InRefundState(SatoraLightningNotification notification) =>
        Enum.TryParse<SatoraSwapStatus>(notification.Status, out var status) && SatoraOutgoingActions.IsRefundState(status);

    public string StoreId { get; set; } = null!;
    // Null for a problem with the store's swaps as a whole.
    public string? SwapId { get; set; }
    // The invoice an incoming swap pays; null for a payment out of the wallet, for the store's swaps as
    // a whole, for a swap no invoice tracks, and in alerts sent before it was named.
    public string? InvoiceId { get; set; }
    public Problems Problem { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset? ClaimCutoff { get; set; }
    // Spendable and recoverable sats at the swap's VHTLC when the alert was sent; null when unread.
    public long? HeldSats { get; set; }
    // The SDK's refusal, for a funding that keeps failing.
    public string? Error { get; set; }
    public override string Identifier => TYPE;
    public override string NotificationType => TYPE;
}
