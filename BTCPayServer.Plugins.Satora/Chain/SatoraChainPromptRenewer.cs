using BTCPayServer.Client.Models;
using BTCPayServer.Events;
using BTCPayServer.HostedServices;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Satora.Chain;

// After a partial payment the checkout shows a smaller due, but the prompt's HTLC still expects the
// deposit for the full amount, and the server refuses anything less; a payment taken off later can
// leave more due than a renewed prompt's swap takes. LightningListener renews only Lightning
// prompts, so this renews the Satora one through InvoiceActivator (forceNew), which makes a new swap
// for what is due. The old swap's row stays, for a customer who pays it anyway or needs a refund. A
// renewal the swap server refuses leaves the prompt inactive, so the old address is not shown for an
// amount its swap does not take.
public class SatoraChainPromptRenewer(
    EventAggregator eventAggregator,
    InvoiceRepository invoiceRepository,
    InvoiceActivator invoiceActivator,
    SatoraChainCredit credit,
    ILogger<SatoraChainPromptRenewer> logger) : EventHostedServiceBase(eventAggregator, logger)
{
    protected override void SubscribeToEvents()
    {
        Subscribe<InvoiceEvent>();
        Subscribe<InvoiceDataChangedEvent>();
    }

    // A partial payment raises both events a few ms apart. Taken one at a time from one queue, with
    // the invoice read fresh, the second finds the prompt the first renewed and makes no second swap.
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
    public static bool NeedsRenewal(InvoiceEntity? invoice) =>
        invoice is { Status: InvoiceStatus.New } &&
        invoice.GetPaymentPrompt(SatoraChainPaymentMethod.Id) is { Activated: true, Destination: not null } prompt &&
        prompt.Calculate().Due > 0m &&
        !SatoraChainPaymentLinkExtension.Payable(prompt);

    internal async Task RenewAsync(InvoiceEntity? invoice)
    {
        if (!NeedsRenewal(invoice))
            return;
        var swapId = SatoraChainPromptDetails.Of(invoice!.GetPaymentPrompt(SatoraChainPaymentMethod.Id))?.SwapId;
        try
        {
            bool renewed;
            using (await credit.LockPromptAsync(invoice.Id))
                renewed = await RenewPromptAsync(invoice.Id);
            if (renewed)
                return;
            Logs.PayServer.LogWarning("Could not renew the Satora on-chain prompt of invoice {InvoiceId} for what is due", invoice.Id);
        }
        catch (Exception e)
        {
            Logs.PayServer.LogError(e, "Could not renew the Satora on-chain prompt of invoice {InvoiceId} for what is due", invoice.Id);
        }
        if (swapId is not null)
            await credit.DeactivatePromptAsync(invoice.Id, swapId);
    }

    protected virtual Task<bool> RenewPromptAsync(string invoiceId) =>
        invoiceActivator.ActivateInvoicePaymentMethod(invoiceId, SatoraChainPaymentMethod.Id, forceNew: true);

    protected virtual async Task<InvoiceEntity?> ReadInvoiceAsync(string invoiceId) => await invoiceRepository.GetInvoice(invoiceId);
}
