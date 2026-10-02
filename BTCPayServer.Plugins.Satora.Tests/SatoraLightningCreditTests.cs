using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraLightningCreditTests
{
    // A real mainnet invoice for 12 345 sats (from frontend/apps/lendaswap/src/utils/bip321.test.ts in satoraHQ/swap).
    // Its payment hash, decoded from the bech32 "p" field by hand: 18063f20...b179.
    internal const string Bolt11 =
        "lnbc123450n1p5awwanpp5rqrr7gqqzrdnmnwgsnl7j82v9p02rxdzahj3zdqy5p403wugk9usdquf35kw6r5de5kueeqf9h8vmmfvdjscqz3txqyyzzssp55wxglttqtecewcmhurnvyl4jvjj0rn3cdrqdn3knkwh7f8x4w9fs9qxpqysgqu8fwg4vgdugzp6u32c7n8lpfe80a2vlul0nkxjqk2pqndrhwvghsnrlys0mwp54ljq4lm290dfq48dvadmm2vuwn38zruc6ev6dpfxqq0zzkya";
    internal const string PaymentHash = "18063f200010db3dcdc884ffe91d4c285ea199a2ede5113404a06af8bb88b179";

    // The payment id must be the one LightningListener.AddPaymentCore gives the same payment, or BTCPay
    // would count it twice; and the hash must be what LightningLikePaymentHandler tracked as the prompt's
    // destination, or the invoice would not be found.
    [Fact]
    public void UsesTheListenersPaymentIdAndTheTrackedDestination()
    {
        var stored = BOLT11PaymentRequest.Parse(Bolt11, Network.Main).PaymentHash!.ToString();
        var reported = new LightningInvoice { Id = "swap", PaymentHash = stored, BOLT11 = Bolt11 };

        Assert.Equal(PaymentHash, stored);
        Assert.Equal(reported.GetPaymentHash(Network.Main)!.ToString(), SatoraLightningCredit.PaymentId(stored));
        Assert.Equal(new LightningInvoice { BOLT11 = Bolt11 }.GetPaymentHash(Network.Main)!.ToString(), SatoraLightningCredit.PaymentId(stored));
    }

    [Fact]
    public void RecordsASettledPaymentOfTheBolt11Amount()
    {
        var swap = new SatoraLightningSwap
        {
            SwapId = "swap",
            StoreId = "wallet-store",
            PaymentHash = PaymentHash,
            Bolt11 = Bolt11,
            AmountMsat = BOLT11PaymentRequest.Parse(Bolt11, Network.Main).MinimumAmount.MilliSatoshi,
            ReceiveSats = 12_000
        };
        var paidAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

        var payment = SatoraLightningCredit.ToPaymentData(swap, "invoice", paidAt);

        Assert.Equal(PaymentHash, payment.Id);
        Assert.Equal(PaymentStatus.Settled, payment.Status);
        Assert.Equal("BTC", payment.Currency);
        Assert.Equal("invoice", payment.InvoiceDataId);
        Assert.Equal(paidAt, payment.Created);
        Assert.Equal(0.00012345m, payment.Amount);
        Assert.Equal("BTC-LN", SatoraLightningCredit.PaymentMethodId.ToString());
    }

    // AddPayment also returns null when saving failed for another reason; only a payment already on
    // the invoice counts as recorded.
    [Fact]
    public void CountsAPaymentAsRecordedOnlyIfTheInvoiceHasItAsALightningPayment()
    {
        var lightning = new PaymentEntity { Id = PaymentHash, PaymentMethodId = SatoraLightningCredit.PaymentMethodId };

        Assert.True(SatoraLightningCredit.HasPayment(Invoice(lightning), PaymentHash));
        Assert.False(SatoraLightningCredit.HasPayment(Invoice(), PaymentHash));
        Assert.False(SatoraLightningCredit.HasPayment(null, PaymentHash));
        Assert.False(SatoraLightningCredit.HasPayment(Invoice(new PaymentEntity { Id = new string('b', 64), PaymentMethodId = SatoraLightningCredit.PaymentMethodId }), PaymentHash));
        Assert.False(SatoraLightningCredit.HasPayment(Invoice(new PaymentEntity { Id = PaymentHash, PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC") }), PaymentHash));
    }

    // The worker marks a swap credited only when crediting returns; a throw makes it try again.
    [Fact]
    public void AFailedAddIsAlreadyRecordedOnlyIfTheInvoiceReadAgainHasThePayment()
    {
        var recorded = Invoice(new PaymentEntity { Id = PaymentHash, PaymentMethodId = SatoraLightningCredit.PaymentMethodId });

        Assert.Equal(SatoraCreditOutcome.AlreadyRecorded, SatoraLightningCredit.NotAdded(recorded, PaymentHash, "swap", "invoice"));
        Assert.Throws<InvalidOperationException>(() => SatoraLightningCredit.NotAdded(Invoice(), PaymentHash, "swap", "invoice"));
        Assert.Throws<InvalidOperationException>(() => SatoraLightningCredit.NotAdded(null, PaymentHash, "swap", "invoice"));
    }

    private static InvoiceEntity Invoice(params PaymentEntity[] payments) => new() { Payments = payments.ToList() };
}
