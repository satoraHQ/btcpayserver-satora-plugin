using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Satora.Stable;

// Records a stablecoin swap on its invoice as SatoraChainCredit records an on-chain one: added as
// Processing once the swap server sees the relay funding in the swap's HTLC, moved to Settled once this
// wallet has claimed the VHTLC, and to Unaccounted when the swap will not pay the invoice after all.
// The Processing payment is the amount the swap was made for; the Settled one is what the VHTLC held,
// which the DEX funding can make a little less, and at most that amount.
public class SatoraStableCredit(
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    PaymentMethodHandlerDictionary handlers,
    EventAggregator eventAggregator,
    ILogger<SatoraStableCredit> logger)
{
    public const string PaymentIdPrefix = "satora-stable:";

    // As SatoraChainCredit's: BTCPay's UpdatePrompt writes back the whole prompt it is given, and the
    // prompt renewer replaces the prompt with a new swap's.
    private readonly SatoraKeyedLock _promptLocks = new();

    public Task<IDisposable> LockPromptAsync(string invoiceId) => _promptLocks.EnterAsync(invoiceId);

    public static string PaymentId(string swapId) => PaymentIdPrefix + swapId;

    public static PaymentMethodId MethodOf(SatoraStableSwap swap) => PaymentMethodId.Parse(swap.PaymentMethodId);

    // What a payment of the swap pays the invoice: the claimed VHTLC once it settles, but no more than
    // was due. The swap server passes a DEX surplus on into the VHTLC, which would otherwise show an
    // exactly paid invoice as paid over.
    public static long Sats(SatoraStableSwap swap, PaymentStatus status) =>
        status == PaymentStatus.Settled && swap.VhtlcSats is { } claimed ? Math.Min(claimed, swap.ReceiveSats) : swap.ReceiveSats;

    // What the claimed VHTLC held beyond the amount due, which stays in the wallet.
    public static long SurplusSats(SatoraStableSwap swap) => Math.Max(0, (swap.VhtlcSats ?? 0) - swap.ReceiveSats);

    // What the claimed VHTLC fell short of the amount due by.
    public static long ShortfallSats(SatoraStableSwap swap) => swap.VhtlcSats is { } claimed ? Math.Max(0, swap.ReceiveSats - claimed) : 0;

    // Built from the swap rather than with PaymentDataExtensions.Set, which copies the invoice's current
    // prompt: after a renewal that is another swap, with another deposit address.
    public static PaymentData ToPaymentData(SatoraStableSwap swap, IPaymentMethodHandler handler, PaymentStatus status, DateTimeOffset at)
    {
        var payment = new PaymentData
        {
            Id = PaymentId(swap.SwapId),
            Created = at,
            Status = status,
            Currency = SatoraStablePaymentMethod.CryptoCode,
            Amount = Money.Satoshis(Sats(swap, status)).ToDecimal(MoneyUnit.BTC),
            InvoiceDataId = swap.InvoiceId
        };
        return payment.SetBlob(handler.PaymentMethodId, new PaymentBlob
        {
            Destination = swap.DepositAddress,
            PaymentMethodFee = 0m,
            Divisibility = SatoraChainPaymentHandler.Divisibility
        }.SetDetails(handler, Details(swap)));
    }

    public static SatoraStablePaymentData Details(SatoraStableSwap swap) => new() { SwapId = swap.SwapId, ClaimTxId = swap.ClaimTxId };

    // Settled with the claim and what it took from the VHTLC, whatever the payment showed before.
    public static void Settle(PaymentEntity payment, SatoraStableSwap swap, IPaymentMethodHandler handler)
    {
        payment.Status = PaymentStatus.Settled;
        payment.Value = Money.Satoshis(Sats(swap, PaymentStatus.Settled)).ToDecimal(MoneyUnit.BTC);
        payment.SetDetails(handler, Details(swap));
    }

    // status is Processing for the funding, Settled for the claim.
    public virtual async Task<SatoraCreditOutcome> RecordAsync(SatoraStableSwap swap, PaymentStatus status, DateTimeOffset at)
    {
        var method = MethodOf(swap);
        var invoice = await invoiceRepository.GetInvoice(swap.InvoiceId);
        if (invoice?.GetPaymentPrompt(method) is null || !handlers.TryGetValue(method, out var handler))
        {
            logger.LogWarning("Satora stablecoin swap {SwapId} has no BTCPay invoice {InvoiceId} to record its payment on", swap.SwapId, swap.InvoiceId);
            return SatoraCreditOutcome.NoInvoice;
        }
        var existing = Find(invoice, swap);
        if (existing is null)
        {
            var added = await paymentService.AddPayment(ToPaymentData(swap, handler, status, at), [swap.DepositAddress]);
            if (added is not null)
            {
                if (await invoiceRepository.GetInvoice(invoice.Id) is { } updated)
                    eventAggregator.Publish(new InvoiceEvent(updated, InvoiceEvent.ReceivedPayment) { Payment = added });
                return SatoraCreditOutcome.Recorded;
            }
            // AddPayment returns null for any failed save, not only for the duplicate key.
            existing = Find(await invoiceRepository.GetInvoice(invoice.Id), swap) ??
                       throw new InvalidOperationException($"The payment of Satora stablecoin swap {swap.SwapId} could not be recorded on invoice {invoice.Id}");
        }
        switch (SatoraChainCredit.Write(existing, status))
        {
            case SatoraChainPaymentWrite.Settle:
                Settle(existing, swap, handler);
                break;
            case SatoraChainPaymentWrite.Account:
                existing.Status = PaymentStatus.Processing;
                break;
            default:
                return SatoraCreditOutcome.AlreadyRecorded;
        }
        await paymentService.UpdatePayments([existing]);
        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        return SatoraCreditOutcome.Recorded;
    }

    // The invoice is due again, or Invalid once BTCPay stops monitoring it.
    public virtual async Task UnaccountAsync(SatoraStableSwap swap)
    {
        var invoice = await invoiceRepository.GetInvoice(swap.InvoiceId);
        if (Find(invoice, swap) is not { Accounted: true } payment)
            return;
        payment.Status = PaymentStatus.Unaccounted;
        await paymentService.UpdatePayments([payment]);
        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice!.Id));
    }

    // The invoice's open prompt for this swap, as SatoraChainCredit.ToDeactivate picks it.
    public static PaymentPrompt? OpenPrompt(InvoiceEntity? invoice, PaymentMethodId method, string swapId) =>
        invoice is { Status: InvoiceStatus.New or InvoiceStatus.Processing } &&
        invoice.GetPaymentPrompt(method) is { Activated: true } prompt &&
        SatoraStablePromptDetails.Of(prompt)?.SwapId == swapId
            ? prompt
            : null;

    // Once it is inactive, BTCPay activates the method again with a new swap when the customer picks
    // it, or leaves it out if that is refused.
    public virtual Task DeactivatePromptAsync(string invoiceId, PaymentMethodId method, string swapId) =>
        ChangePromptAsync(invoiceId, method, swapId, (_, prompt, _) =>
        {
            prompt.Inactive = true;
            logger.LogInformation("Satora stablecoin prompt of invoice {InvoiceId} deactivated, as its swap {SwapId} can no longer be paid", invoiceId, swapId);
            return true;
        });

    // What the deposit address holds, for the checkout to show what arrived and ask for the rest.
    // Written only on a change, and only to an invoice still waiting for its payment.
    public virtual Task SetDepositProgressAsync(string invoiceId, PaymentMethodId method, string swapId, string receivedUnits) =>
        ChangePromptAsync(invoiceId, method, swapId, (invoice, prompt, details) =>
        {
            if (details.ReceivedUnits == receivedUnits || invoice.Status != InvoiceStatus.New)
                return false;
            details.ReceivedUnits = receivedUnits;
            prompt.Details = JObject.FromObject(details, handlers[method].Serializer);
            return true;
        });

    // As LightningListener changes a prompt: saved with UpdatePrompt, then announced, as an open
    // checkout fetches the prompt again only on that event (the invoice's state does not change).
    private async Task ChangePromptAsync(string invoiceId, PaymentMethodId method, string swapId,
        Func<InvoiceEntity, PaymentPrompt, SatoraStablePromptDetails, bool> change)
    {
        using var promptLock = await LockPromptAsync(invoiceId);
        var invoice = await ReadInvoiceAsync(invoiceId);
        if (OpenPrompt(invoice, method, swapId) is not { } prompt ||
            SatoraStablePromptDetails.Of(prompt) is not { } details || !change(invoice!, prompt, details))
            return;
        await invoiceRepository.UpdatePrompt(invoiceId, prompt);
        eventAggregator.Publish(new InvoiceNewPaymentDetailsEvent(invoiceId, SatoraStablePromptDetails.Of(prompt), method));
    }

    protected virtual async Task<InvoiceEntity?> ReadInvoiceAsync(string invoiceId) => await invoiceRepository.GetInvoice(invoiceId);

    // The tolerance BTCPay settles the swap's invoice against, which the payment handler checked against
    // the swap's fees before it made the swap. Null without the invoice.
    public virtual async Task<decimal?> PaymentToleranceAsync(SatoraStableSwap swap) =>
        await ReadInvoiceAsync(swap.InvoiceId) is { } invoice ? (decimal)invoice.PaymentTolerance : null;

    // The customer paid the swap's invoice some other way, so its deposit address is unlikely to get anything.
    public virtual async Task<bool> SettledOtherwiseAsync(SatoraStableSwap swap) =>
        await ReadInvoiceAsync(swap.InvoiceId) is { Status: InvoiceStatus.Settled } invoice && Find(invoice, swap) is null;

    // As SatoraChainCredit.NoteAsync: a deposit that has to go back never reaches the invoice, so its
    // Events are where it stays on record. A note that cannot be written is only logged.
    public virtual async Task NoteAsync(SatoraStableSwap swap, string message, InvoiceEventData.EventSeverity severity)
    {
        try
        {
            await invoiceRepository.AddInvoiceEvent(swap.InvoiceId, message, severity);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not note Satora stablecoin swap {SwapId} on invoice {InvoiceId}", swap.SwapId, swap.InvoiceId);
        }
    }

    public static PaymentEntity? Find(InvoiceEntity? invoice, SatoraStableSwap swap) =>
        invoice?.GetPayments(false).FirstOrDefault(p => p.Id == PaymentId(swap.SwapId) && p.PaymentMethodId == MethodOf(swap));
}
