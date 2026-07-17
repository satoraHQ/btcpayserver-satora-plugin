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
    public async Task MarkProcessingAsync(string invoiceId, string swapId, ulong expectedSats)
    {
        if (string.IsNullOrEmpty(invoiceId) || string.IsNullOrEmpty(swapId))
            return;

        var invoice = await invoiceRepository.GetInvoice(invoiceId);
        if (invoice is null)
        {
            logger.LogWarning("SatoraSettlement: invoice {InvoiceId} not found while marking swap {SwapId} processing", invoiceId, swapId);
            return;
        }

        if (!handlers.TryGetValue(SatoraPaymentMethod.PaymentMethodId, out var handler) ||
            invoice.GetPaymentPrompt(SatoraPaymentMethod.PaymentMethodId) is null)
        {
            logger.LogWarning("SatoraSettlement: no Satora handler/prompt for invoice {InvoiceId}; cannot mark swap {SwapId} processing", invoiceId, swapId);
            return;
        }

        var paymentId = GetPaymentId(swapId, null);
        var details = new SatoraPaymentData
        {
            SwapId = swapId,
            Outpoint = paymentId,
            Destination = invoice.GetPaymentPrompt(SatoraPaymentMethod.PaymentMethodId)?.Destination
        };
        var paymentData = new PaymentData
        {
            Status = PaymentStatus.Processing,
            Amount = Money.Satoshis((long)expectedSats).ToDecimal(MoneyUnit.BTC),
            Created = DateTimeOffset.UtcNow,
            Id = paymentId,
            Currency = "BTC",
        }.Set(invoice, handler, details);

        var payment = await paymentService.AddPayment(paymentData);
        if (payment is null)
        {
            logger.LogDebug("SatoraSettlement: processing payment {PaymentId} already registered for invoice {InvoiceId}", paymentId, invoiceId);
            return;
        }

        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        logger.LogInformation("SatoraSettlement: registered processing payment for swap {SwapId} on invoice {InvoiceId}", swapId, invoiceId);
    }

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
        var outpoint = GetPaymentId(swapId, arkTxid);

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
            var existing = invoice.GetPayments(false)
                .SingleOrDefault(p => p.Id == outpoint && p.PaymentMethodId == SatoraPaymentMethod.PaymentMethodId);
            if (existing is null)
            {
                logger.LogInformation("SatoraSettlement: payment {Outpoint} already registered for invoice {InvoiceId}", outpoint, invoiceId);
                return;
            }

            if (existing.Status is PaymentStatus.Settled)
            {
                logger.LogInformation("SatoraSettlement: payment {Outpoint} already settled for invoice {InvoiceId}", outpoint, invoiceId);
                return;
            }

            existing.Status = PaymentStatus.Settled;
            existing.Value = Money.Satoshis((long)claimSats).ToDecimal(MoneyUnit.BTC);
            existing.Currency = "BTC";
            existing.ReceivedTime = DateTimeOffset.UtcNow;
            existing.SetDetails(handler, details);
            await paymentService.UpdatePayments([existing]);
            eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
            logger.LogInformation("SatoraSettlement: updated processing payment {Outpoint} to settled for invoice {InvoiceId}", outpoint, invoiceId);
            return;
        }

        // Nudge the invoice watcher to re-evaluate so it flips to Settled
        // once payments cover the due (mirrors ArkPayServer).
        eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
        logger.LogInformation("SatoraSettlement: registered {Sats} sats Satora payment (txid {Txid}) for invoice {InvoiceId}", claimSats, arkTxid, invoiceId);
    }

    private static string GetPaymentId(string? swapId, string? arkTxid)
    {
        return !string.IsNullOrEmpty(swapId) ? $"satora-swap:{swapId}" : $"{arkTxid}:0";
    }
}
