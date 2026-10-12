using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Invoices;
using NBitcoin;

namespace BTCPayServer.Plugins.Satora.Chain;

public enum SatoraChainPaymentWrite
{
    Add,
    // Already on the invoice as asked.
    None,
    Settle,
    // A funding taken off as replaced is back in the mempool.
    Account
}

// Records an on-chain swap on its invoice the way NBXplorerListener records an on-chain payment: added
// as Processing when the customer's output is seen, moved to Settled (here, once this wallet has
// claimed), and to Unaccounted when it will not pay the invoice after all, as for a replaced
// transaction. Every payment carries Value = the deposit and the swap's fee as its payment method fee,
// so its net is exactly the amount due the swap was made for.
public class SatoraChainCredit(
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    PaymentMethodHandlerDictionary handlers,
    EventAggregator eventAggregator,
    ILogger<SatoraChainCredit> logger)
{
    public const string PaymentIdPrefix = "satora-swap:";

    // BTCPay's UpdatePrompt writes back the whole prompt it is given, and the prompt renewer replaces
    // the prompt with a new swap's. One change at a time per invoice, so a deactivation never writes
    // back a prompt a renewal replaced after the deactivation read it.
    private readonly SatoraKeyedLock _promptLocks = new();

    public Task<IDisposable> LockPromptAsync(string invoiceId) => _promptLocks.EnterAsync(invoiceId);

    public static string PaymentId(string swapId) => PaymentIdPrefix + swapId;

    // Built from the swap rather than with PaymentDataExtensions.Set, which copies the invoice's current
    // prompt: after a renewal that is another swap, with another address and fee.
    public static PaymentData ToPaymentData(SatoraChainSwap swap, IPaymentMethodHandler handler, PaymentStatus status, DateTimeOffset at,
        bool afterPaidInFull = false)
    {
        var payment = new PaymentData
        {
            Id = PaymentId(swap.SwapId),
            Created = at,
            Status = status,
            Currency = SatoraChainPaymentMethod.CryptoCode,
            Amount = Money.Satoshis(swap.DepositSats).ToDecimal(MoneyUnit.BTC),
            InvoiceDataId = swap.InvoiceId
        };
        return payment.SetBlob(handler.PaymentMethodId, new PaymentBlob
        {
            Destination = swap.HtlcAddress,
            PaymentMethodFee = SatoraChainPaymentHandler.FeeBtc(swap),
            Divisibility = SatoraChainPaymentHandler.Divisibility
        }.SetDetails(handler, Details(swap, afterPaidInFull)));
    }

    public static SatoraChainPaymentData Details(SatoraChainSwap swap, bool afterPaidInFull) =>
        new() { SwapId = swap.SwapId, ClaimTxId = swap.ClaimTxId, AfterPaidInFull = afterPaidInFull };

    // Settled with the claim, whatever the payment showed before.
    public static void Settle(PaymentEntity payment, SatoraChainSwap swap, IPaymentMethodHandler handler, bool afterPaidInFull)
    {
        payment.Status = PaymentStatus.Settled;
        payment.Value = Money.Satoshis(swap.DepositSats).ToDecimal(MoneyUnit.BTC);
        payment.SetDetails(handler, Details(swap, afterPaidInFull));
    }

    // A Settled payment stays settled; a Processing one settles with the claim, and comes back from
    // Unaccounted when its funding reappears.
    public static SatoraChainPaymentWrite Write(PaymentEntity? existing, PaymentStatus status) =>
        existing is null ? SatoraChainPaymentWrite.Add
        : existing.Status == PaymentStatus.Settled ? SatoraChainPaymentWrite.None
        : status == PaymentStatus.Settled ? SatoraChainPaymentWrite.Settle
        : existing.Status == PaymentStatus.Unaccounted ? SatoraChainPaymentWrite.Account
        : SatoraChainPaymentWrite.None;

    // AddPayment returns null for any failed save, not only for the duplicate key, so only the invoice
    // read again tells whether the payment is on it.
    public static PaymentEntity NotAdded(InvoiceEntity? reread, string swapId, string invoiceId) =>
        Find(reread, swapId) ?? throw new InvalidOperationException($"The payment of Satora on-chain swap {swapId} could not be recorded on invoice {invoiceId}");

    // Only a payment the invoice still counts is taken off it.
    public static PaymentEntity? ToUnaccount(InvoiceEntity? invoice, string swapId) =>
        Find(invoice, swapId) is { Accounted: true } payment ? payment : null;

    // status is Processing for a funding, Settled for a claim.
    public virtual async Task<SatoraCreditOutcome> RecordAsync(SatoraChainSwap swap, PaymentStatus status, DateTimeOffset at)
    {
        var invoice = await invoiceRepository.GetInvoice(swap.InvoiceId);
        if (invoice?.GetPaymentPrompt(SatoraChainPaymentMethod.Id) is null || !handlers.TryGetValue(SatoraChainPaymentMethod.Id, out var handler))
        {
            logger.LogWarning("Satora on-chain swap {SwapId} has no BTCPay invoice {InvoiceId} to record its payment on", swap.SwapId, swap.InvoiceId);
            return SatoraCreditOutcome.NoInvoice;
        }
        var existing = Find(invoice, swap.SwapId);
        // Decided by the write that settles the payment, from the invoice as it was before, so that a
        // credit repeated after a failure, or another swap's credited in between, cannot change it.
        var afterPaidInFull = status == PaymentStatus.Settled && PaidInFullWithout(invoice, swap.SwapId);
        if (existing is null)
        {
            var added = await paymentService.AddPayment(ToPaymentData(swap, handler, status, at, afterPaidInFull), [swap.HtlcAddress]);
            if (added is not null)
            {
                if (await invoiceRepository.GetInvoice(invoice.Id) is { } updated)
                    eventAggregator.Publish(new InvoiceEvent(updated, InvoiceEvent.ReceivedPayment) { Payment = added });
                return SatoraCreditOutcome.Recorded;
            }
            existing = NotAdded(await invoiceRepository.GetInvoice(invoice.Id), swap.SwapId, invoice.Id);
        }
        switch (Write(existing, status))
        {
            case SatoraChainPaymentWrite.Settle:
                Settle(existing, swap, handler, afterPaidInFull);
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

    // Whether the invoice's other settled payments pay it in full as BTCPay's MinimumNetDue counts, so
    // that all of the swap's payment is beyond what was due. One still processing does not count, as it
    // can still be replaced and leave the swap's payment needed after all. Of two swaps that each pay
    // the invoice in full, the one settled second is thus the one beyond it.
    public static bool PaidInFullWithout(InvoiceEntity invoice, string swapId)
    {
        var own = Find(invoice, swapId);
        var settled = invoice.GetPayments(false).Where(p => p.Status == PaymentStatus.Settled && p != own).Sum(p => p.InvoicePaidAmount.Net);
        return invoice.Price * (1m - (decimal)invoice.PaymentTolerance / 100m) <= settled;
    }

    // Whether the swap's payment settled on an invoice its other payments had already paid in full.
    public virtual async Task<bool> SettledAfterPaidInFullAsync(SatoraChainSwap swap) =>
        Find(await invoiceRepository.GetInvoice(swap.InvoiceId), swap.SwapId) is { Status: PaymentStatus.Settled } payment &&
        handlers.TryGetValue(SatoraChainPaymentMethod.Id, out var handler) &&
        payment.GetDetails<SatoraChainPaymentData>(handler) is { AfterPaidInFull: true };

    // The invoice is due again, or Invalid once BTCPay stops monitoring it.
    public virtual async Task UnaccountAsync(SatoraChainSwap swap)
    {
        var invoice = await invoiceRepository.GetInvoice(swap.InvoiceId);
        if (ToUnaccount(invoice, swap.SwapId) is not { } payment)
            return;
        payment.Status = PaymentStatus.Unaccounted;
        await paymentService.UpdatePayments([payment]);
        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice!.Id));
    }

    // The invoice's open prompt whose swap can no longer take what is due. Once it is inactive, BTCPay
    // activates the method again with a new swap when the customer picks it, or leaves it out if that
    // is refused. A Processing invoice is included: its payment was just taken off, and only
    // InvoiceWatcher moves it back to New afterwards.
    public static PaymentPrompt? ToDeactivate(InvoiceEntity? invoice, string swapId) =>
        invoice is { Status: InvoiceStatus.New or InvoiceStatus.Processing } &&
        invoice.GetPaymentPrompt(SatoraChainPaymentMethod.Id) is { Activated: true } prompt &&
        SatoraChainPromptDetails.Of(prompt)?.SwapId == swapId
            ? prompt
            : null;

    // As LightningListener changes a prompt: saved with UpdatePrompt, then announced, as an open
    // checkout fetches the prompt again only on that event (the invoice's state does not change).
    public virtual async Task DeactivatePromptAsync(string invoiceId, string swapId)
    {
        using var promptLock = await LockPromptAsync(invoiceId);
        if (ToDeactivate(await ReadInvoiceAsync(invoiceId), swapId) is not { } prompt)
            return;
        prompt.Inactive = true;
        await invoiceRepository.UpdatePrompt(invoiceId, prompt);
        eventAggregator.Publish(new InvoiceNewPaymentDetailsEvent(invoiceId, SatoraChainPromptDetails.Of(prompt), SatoraChainPaymentMethod.Id));
        logger.LogInformation("Satora on-chain prompt of invoice {InvoiceId} deactivated, as its swap {SwapId} can no longer be paid", invoiceId, swapId);
    }

    protected virtual async Task<InvoiceEntity?> ReadInvoiceAsync(string invoiceId) => await invoiceRepository.GetInvoice(invoiceId);

    // An underpayment or bitcoin sent after the swap ended never reaches the invoice, and the refunds
    // page drops a swap once it closes, so the invoice's Events are where its refund stays on record. A
    // note that cannot be written is only logged: it must not hold up the refund it reports, and BTCPay
    // drops one for a deleted invoice by itself.
    public virtual async Task NoteAsync(SatoraChainSwap swap, string message, InvoiceEventData.EventSeverity severity)
    {
        try
        {
            await invoiceRepository.AddInvoiceEvent(swap.InvoiceId, message, severity);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not note Satora on-chain swap {SwapId} on invoice {InvoiceId}", swap.SwapId, swap.InvoiceId);
        }
    }

    public static PaymentEntity? Find(InvoiceEntity? invoice, string swapId) =>
        invoice?.GetPayments(false).FirstOrDefault(p => p.Id == PaymentId(swapId) && p.PaymentMethodId == SatoraChainPaymentMethod.Id);
}
