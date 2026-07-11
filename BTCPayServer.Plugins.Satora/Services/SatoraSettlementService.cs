using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Payments;
using BTCPayServer.Services.Invoices;
using NBitcoin;

namespace BTCPayServer.Plugins.Satora.Services;

// Settles a BTCPay invoice once a Satora swap's BTC has been claimed into
// the plugin's per-store Arkade wallet. The payment record is owned by the
// Satora payment method, so the invoice ledger no longer depends on another
// plugin's ARKADE handler being installed.
public class SatoraSettlementService(
    PaymentService paymentService,
    InvoiceRepository invoiceRepository,
    PaymentMethodHandlerDictionary handlers,
    EventAggregator eventAggregator,
    ILogger<SatoraSettlementService> logger)
{
    public async Task SettleAsync(string invoiceId, string arkTxid, ulong claimSats, string? swapId = null)
    {
        if (string.IsNullOrEmpty(invoiceId))
        {
            logger.LogWarning("SatoraSettlement: no invoice id, nothing to settle");
            return;
        }

        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null)
        {
            logger.LogWarning("SatoraSettlement: invoice {InvoiceId} not found", invoiceId);
            return;
        }

        // Payment id keyed on the claim txid so re-running is idempotent
        // (AddPayment returns null on a duplicate). The Arkade VHTLC claim
        // produces a single output, so index 0.
        var outpoint = $"{arkTxid}:0";

        if (!handlers.TryGetValue(SatoraPaymentMethod.PaymentMethodId, out var handler) ||
            invoice.GetPaymentPrompt(SatoraPaymentMethod.PaymentMethodId) is null)
        {
            logger.LogWarning("SatoraSettlement: no Satora handler/prompt for invoice {InvoiceId}; marking settled without a payment record", invoiceId);
            await invoiceRepository.MarkInvoiceStatus(invoiceId, InvoiceStatus.Settled);
            return;
        }

        var details = new SatoraPaymentData
        {
            SwapId = swapId,
            ArkadeTxId = arkTxid,
            Outpoint = outpoint,
            Destination = invoice.GetPaymentPrompt(SatoraPaymentMethod.PaymentMethodId)?.Destination
        };
        var paymentData = new PaymentData
        {
            Status = PaymentStatus.Settled,
            Amount = Money.Satoshis((long)claimSats).ToDecimal(MoneyUnit.BTC),
            Created = DateTimeOffset.UtcNow,
            Id = outpoint,
            Currency = "BTC",
        }.Set(invoice, handler, details);

        var payment = await paymentService.AddPayment(paymentData);
        if (payment is null)
        {
            // Already registered on a prior call — idempotent no-op.
            logger.LogInformation("SatoraSettlement: payment {Outpoint} already registered for invoice {InvoiceId}", outpoint, invoiceId);
            return;
        }

        // Nudge the invoice watcher to re-evaluate so it flips to Settled
        // once payments cover the due (mirrors ArkPayServer).
        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        logger.LogInformation("SatoraSettlement: registered {Sats} sats Satora payment (txid {Txid}) for invoice {InvoiceId}", claimSats, arkTxid, invoiceId);
    }
}
