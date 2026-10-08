using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Satora.Services;

// A Satora payment method whose prompt is a swap made for a fixed amount: Payable tells whether that
// is still what is due.
public sealed record SatoraSwapPrompt(PaymentMethodId Id, Func<PaymentPrompt, bool> Payable, Func<PaymentPrompt, string?> SwapId);

// After a partial payment the checkout shows a smaller due, but a Satora prompt's swap still takes
// what it was made for: the on-chain HTLC refuses less, and a stablecoin swap would pay the wallet
// more than is due. A payment taken off later can leave more due than a renewed prompt's swap takes.
// LightningListener renews only Lightning prompts, so this renews each Satora one through
// InvoiceActivator (forceNew), which makes a new swap for what is due. The old swap's row stays, for a
// customer who pays it anyway or needs a refund. A renewal the swap server refuses leaves the prompt
// inactive, so the old address is not shown for an amount its swap does not take.
public class SatoraPromptRenewer(
    EventAggregator eventAggregator,
    InvoiceRepository invoiceRepository,
    InvoiceActivator invoiceActivator,
    SatoraChainCredit chainCredit,
    SatoraStableCredit stableCredit,
    ILogger<SatoraPromptRenewer> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    public static readonly IReadOnlyList<SatoraSwapPrompt> SwapPrompts =
    [
        new(SatoraChainPaymentMethod.Id, SatoraChainPaymentLinkExtension.Payable, prompt => SatoraChainPromptDetails.Of(prompt)?.SwapId),
        .. SatoraStablePaymentMethod.All.Select(coin =>
            new SatoraSwapPrompt(coin.Id, SatoraStablePaymentLinkExtension.Payable, prompt => SatoraStablePromptDetails.Of(prompt)?.SwapId))
    ];

    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        Subscribe<InvoiceDataChangedEvent>();
    }

    // A partial payment raises both events a few ms apart. Taken one at a time from one queue, with
    // the invoice read fresh, the second finds the prompts the first renewed and makes no second swap.
    protected override async Task ProcessEvent(object evt, CancellationToken cancellationToken)
    {
        var invoiceId = evt switch
        {
            InvoiceEvent { Name: InvoiceEvent.ReceivedPayment } e => e.Invoice.Id,
            InvoiceDataChangedEvent { State.Status: InvoiceStatus.New } e => e.InvoiceId,
            _ => null
        };
        if (invoiceId is not null)
            await RenewAsync(await ReadInvoiceAsync(invoiceId));
    }

    // A prompt never activated (lazy payment methods) gets the current due when the customer picks it.
    public static bool NeedsRenewal(InvoiceEntity? invoice, SatoraSwapPrompt method) =>
        invoice is { Status: InvoiceStatus.New } &&
        invoice.GetPaymentPrompt(method.Id) is { Activated: true, Destination: not null } prompt &&
        prompt.Calculate().Due > 0m &&
        !method.Payable(prompt);

    // Renewing one prompt changes no other prompt's due, so the invoice read once decides for all.
    internal async Task RenewAsync(InvoiceEntity? invoice)
    {
        foreach (var method in SwapPrompts.Where(method => NeedsRenewal(invoice, method)))
            await RenewAsync(invoice!, method);
    }

    private async Task RenewAsync(InvoiceEntity invoice, SatoraSwapPrompt method)
    {
        var swapId = method.SwapId(invoice.GetPaymentPrompt(method.Id)!);
        try
        {
            bool renewed;
            using (await LockPromptAsync(invoice.Id, method.Id))
                renewed = await RenewPromptAsync(invoice.Id, method.Id);
            if (renewed)
                return;
            Logs.PayServer.LogWarning("Could not renew the {PaymentMethodId} prompt of invoice {InvoiceId} for what is due", method.Id, invoice.Id);
        }
        catch (Exception e)
        {
            Logs.PayServer.LogError(e, "Could not renew the {PaymentMethodId} prompt of invoice {InvoiceId} for what is due", method.Id, invoice.Id);
        }
        if (swapId is not null)
            await DeactivatePromptAsync(invoice.Id, method.Id, swapId);
    }

    // The credit class that writes the method's prompt holds its lock, so a renewal never races its
    // deactivation or a deposit progress update.
    private Task<IDisposable> LockPromptAsync(string invoiceId, PaymentMethodId method) =>
        method == SatoraChainPaymentMethod.Id ? chainCredit.LockPromptAsync(invoiceId) : stableCredit.LockPromptAsync(invoiceId);

    private Task DeactivatePromptAsync(string invoiceId, PaymentMethodId method, string swapId) =>
        method == SatoraChainPaymentMethod.Id
            ? chainCredit.DeactivatePromptAsync(invoiceId, swapId)
            : stableCredit.DeactivatePromptAsync(invoiceId, method, swapId);

    protected virtual Task<bool> RenewPromptAsync(string invoiceId, PaymentMethodId method) =>
        invoiceActivator.ActivateInvoicePaymentMethod(invoiceId, method, forceNew: true);

    protected virtual async Task<InvoiceEntity?> ReadInvoiceAsync(string invoiceId) => await invoiceRepository.GetInvoice(invoiceId);
}
