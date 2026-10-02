using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using NBitcoin;

namespace BTCPayServer.Plugins.Satora.Lightning;

public enum SatoraCreditOutcome
{
    Recorded,
    AlreadyRecorded,
    NoInvoice
}

// Records a claimed Lightning payment the way BTCPay's LightningListener (AddPaymentCore) does, with
// the same payment id, so whichever of the two records it first, the payments primary key
// (Id, PaymentMethodId) keeps the other from counting it again. Recording it here rather than
// waiting for the listener lands a claim that completes after BTCPay stopped listening (invoice
// expired, restart) as a late payment instead of losing it.
public class SatoraLightningCredit(
    InvoiceRepository invoiceRepository,
    PaymentService paymentService,
    PaymentMethodHandlerDictionary handlers,
    EventAggregator eventAggregator,
    ApplicationDbContextFactory contexts,
    ILogger<SatoraLightningCredit> logger)
{
    public const string CryptoCode = "BTC";

    public static PaymentMethodId PaymentMethodId => PaymentTypes.LN.GetPaymentMethodId(CryptoCode);

    // LightningListener: Id = paymentHash.ToString(), the BOLT11's payment hash as BTCPay displays a uint256.
    public static string PaymentId(string paymentHash) => uint256.Parse(paymentHash).ToString();

    public static PaymentData ToPaymentData(SatoraLightningSwap swap, string invoiceId, DateTimeOffset paidAt) => new()
    {
        Id = PaymentId(swap.PaymentHash),
        Created = paidAt,
        Status = PaymentStatus.Settled,
        Currency = CryptoCode,
        InvoiceDataId = invoiceId,
        // The BOLT11 amount, as the listener reports it: the swap fee is the merchant's, so a smaller
        // amount would make BTCPay see a partial payment and ask the payer for the difference.
        Amount = LightMoney.MilliSatoshis(swap.AmountMsat).ToDecimal(LightMoneyUnit.BTC)
    };

    public virtual async Task<SatoraCreditOutcome> CreditAsync(SatoraLightningSwap swap, DateTimeOffset paidAt)
    {
        var pmi = PaymentMethodId;
        // LightningLikePaymentHandler tracks the payment hash as the prompt's destination, for the
        // first prompt and for every renewal after a partial payment.
        var invoice = await invoiceRepository.GetInvoiceFromAddress(pmi, PaymentId(swap.PaymentHash));
        if (invoice?.GetPaymentPrompt(pmi) is null || !handlers.TryGetValue(pmi, out var handler))
        {
            // As the listener does when the invoice is gone: nothing to record it on.
            logger.LogWarning("Satora Lightning swap {SwapId} was paid and claimed, but no BTCPay invoice tracks its payment hash", swap.SwapId);
            return SatoraCreditOutcome.NoInvoice;
        }
        var paymentData = ToPaymentData(swap, invoice.Id, paidAt).Set(invoice, handler,
            new LightningLikePaymentData { PaymentHash = uint256.Parse(swap.PaymentHash) });
        var payment = await paymentService.AddPayment(paymentData, [swap.Bolt11]);
        if (payment is null)
        {
            var outcome = NotAdded(await invoiceRepository.GetInvoice(invoice.Id), paymentData.Id, swap.SwapId, invoice.Id);
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
            return outcome;
        }
        var updated = await invoiceRepository.GetInvoice(invoice.Id);
        if (updated is not null)
            eventAggregator.Publish(new InvoiceEvent(updated, InvoiceEvent.ReceivedPayment) { Payment = payment });
        return SatoraCreditOutcome.Recorded;
    }

    // The invoices tracking these payment hashes, as CreditAsync finds each one, keyed by payment hash.
    // A swap whose prompt BTCPay never saved, as when it stopped waiting for the swap, has none.
    public virtual async Task<IReadOnlyDictionary<string, string>> FindInvoiceIdsAsync(IReadOnlyCollection<string> paymentHashes)
    {
        var addresses = paymentHashes
            .Select(hash => uint256.TryParse(hash, out var parsed) ? parsed.ToString() : null)
            .OfType<string>()
            .Distinct()
            .ToList();
        if (addresses.Count == 0)
            return new Dictionary<string, string>();
        var pmi = PaymentMethodId.ToString();
        await using var ctx = contexts.CreateContext();
        return await ctx.AddressInvoices
            .Where(a => a.PaymentMethodId == pmi && addresses.Contains(a.Address))
            .ToDictionaryAsync(a => a.Address, a => a.InvoiceDataId, StringComparer.OrdinalIgnoreCase);
    }

    // AddPayment returns null for any failed save, not only for the duplicate key, so only the
    // invoice read again tells whether the payment is on it.
    internal static SatoraCreditOutcome NotAdded(InvoiceEntity? reread, string paymentId, string swapId, string invoiceId) =>
        HasPayment(reread, paymentId)
            ? SatoraCreditOutcome.AlreadyRecorded
            : throw new InvalidOperationException($"The payment of Satora Lightning swap {swapId} could not be recorded on invoice {invoiceId}");

    public static bool HasPayment(InvoiceEntity? invoice, string paymentId) =>
        invoice?.GetPayments(false).Any(p => p.Id == paymentId && p.PaymentMethodId == PaymentMethodId) is true;
}
