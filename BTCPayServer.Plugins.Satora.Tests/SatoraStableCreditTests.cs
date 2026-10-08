using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// What SatoraStableCredit and SatoraPromptRenewer write to an invoice for a stablecoin swap, against
// BTCPay's own invoice accounting.
public class SatoraStableCreditTests
{
    private static readonly SatoraStablecoin Usdc = SatoraStablePaymentMethod.UsdcArbitrum;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    // 4000 USD at 34000 USD/BTC, as the prompt asks for it.
    private const long DueSats = 11_764_706;

    // The prompt is the amount due in bitcoin and has no fee of its own: the customer's tokens carry the swap's.
    [Fact]
    public async Task TheFundingsPaymentPaysTheInvoiceExactly()
    {
        var (handler, invoice, swap) = await Prompted();

        AddPayment(invoice, SatoraStableCredit.ToPaymentData(swap, handler, PaymentStatus.Processing, Now).GetBlob());

        Assert.Equal((false, false, 0m), (invoice.IsUnderPaid, invoice.IsOverPaid, invoice.NetDue));
        var payment = Assert.Single(invoice.GetPayments(false));
        Assert.Equal(("satora-stable:store-swap1", swap.DepositAddress, 0m, "store-swap1"),
            (payment.Id, payment.Destination, payment.PaymentMethodFee, handler.ParsePaymentDetails(payment.Details).SwapId));
    }

    // The DEX funding delivered less than quoted, so the VHTLC held less than the swap was made for.
    [Fact]
    public async Task TheClaimSettlesThePaymentWithWhatTheVhtlcHeld()
    {
        var (handler, invoice, swap) = await Prompted();
        var payment = SatoraStableCredit.ToPaymentData(swap, handler, PaymentStatus.Processing, Now).GetBlob();
        AddPayment(invoice, payment);
        swap.ClaimTxId = "ark";
        swap.VhtlcSats = DueSats - 50_000;

        SatoraStableCredit.Settle(payment, swap, handler);
        invoice.UpdateTotals();

        Assert.Equal((PaymentStatus.Settled, (DueSats - 50_000) / 100_000_000m, "ark"),
            (payment.Status, payment.Value, handler.ParsePaymentDetails(payment.Details).ClaimTxId));
        Assert.True(invoice.IsUnderPaid);
        Assert.Equal(50_000 / 100_000_000m, invoice.GetPaymentPrompt(Usdc.Id)!.Calculate().Due);
        // The store's tolerance, which turning the coin on requires to cover the swap's fees, absorbs it.
        invoice.PaymentTolerance = 1;
        invoice.UpdateTotals();
        Assert.False(invoice.IsUnderPaid);
    }

    // The swap server passes a DEX surplus on into the VHTLC; the invoice was paid exactly.
    [Fact]
    public async Task TheClaimSettlesThePaymentAtTheAmountDueWhenTheVhtlcHeldMore()
    {
        var (handler, invoice, swap) = await Prompted();
        var payment = SatoraStableCredit.ToPaymentData(swap, handler, PaymentStatus.Processing, Now).GetBlob();
        AddPayment(invoice, payment);
        swap.ClaimTxId = "ark";
        swap.VhtlcSats = DueSats + 1;

        SatoraStableCredit.Settle(payment, swap, handler);
        invoice.UpdateTotals();

        Assert.Equal((PaymentStatus.Settled, DueSats / 100_000_000m), (payment.Status, payment.Value));
        Assert.False(invoice.IsOverPaid);
        Assert.Equal(1, SatoraStableCredit.SurplusSats(swap));
        Assert.Equal(DueSats / 100_000_000m, SatoraStableCredit.ToPaymentData(swap, handler, PaymentStatus.Settled, Now).Amount);
    }

    // A claim recorded in one write, as when the worker missed the funding.
    [Fact]
    public async Task ASettledPaymentAddedAtOnceCarriesWhatTheVhtlcHeld()
    {
        var (handler, _, swap) = await Prompted();
        swap.ClaimTxId = "ark";
        swap.VhtlcSats = DueSats - 1;

        Assert.Equal((DueSats - 1) / 100_000_000m, SatoraStableCredit.ToPaymentData(swap, handler, PaymentStatus.Settled, Now).Amount);
        Assert.Equal(DueSats / 100_000_000m, SatoraStableCredit.ToPaymentData(swap, handler, PaymentStatus.Processing, Now).Amount);
    }

    [Fact]
    public async Task FindsOnlyTheSwapsOwnPaymentOnItsMethod()
    {
        var (handler, invoice, swap) = await Prompted();
        AddPayment(invoice, SatoraStableCredit.ToPaymentData(swap, handler, PaymentStatus.Processing, Now).GetBlob());
        AddPayment(invoice, new PaymentEntity { Id = "satora-stable:other", Currency = "BTC", PaymentMethodId = SatoraChainPaymentMethod.Id });
        var other = SatoraStableIntegrationTests.Swap("store", "other");

        Assert.Equal("satora-stable:store-swap1", SatoraStableCredit.Find(invoice, swap)?.Id);
        Assert.Null(SatoraStableCredit.Find(invoice, other));
        Assert.Null(SatoraStableCredit.Find(null, swap));
    }

    [Fact]
    public async Task ChangesOnlyTheOpenInvoicesActivePromptForThatSwap()
    {
        var (_, invoice, swap) = await Prompted();

        Assert.NotNull(SatoraStableCredit.OpenPrompt(invoice, Usdc.Id, swap.SwapId));
        Assert.Null(SatoraStableCredit.OpenPrompt(invoice, Usdc.Id, "renewed-since"));
        Assert.Null(SatoraStableCredit.OpenPrompt(invoice, SatoraChainPaymentMethod.Id, swap.SwapId));
        invoice.Status = InvoiceStatus.Processing;
        Assert.NotNull(SatoraStableCredit.OpenPrompt(invoice, Usdc.Id, swap.SwapId));
        invoice.Status = InvoiceStatus.Expired;
        Assert.Null(SatoraStableCredit.OpenPrompt(invoice, Usdc.Id, swap.SwapId));
        invoice.Status = InvoiceStatus.New;
        Deactivate(invoice);
        Assert.Null(SatoraStableCredit.OpenPrompt(invoice, Usdc.Id, swap.SwapId));
        Assert.Null(SatoraStableCredit.OpenPrompt(null, Usdc.Id, swap.SwapId));
    }

    // Paid in part over Lightning: the stablecoin swap would pay the wallet more than is now due.
    [Fact]
    public async Task APartialPaymentRenewsTheStablecoinPrompt()
    {
        var (_, invoice, _) = await Prompted();
        var renewer = new TestRenewer(new RecordingStableCredit(), () => true);
        Assert.False(SatoraPromptRenewer.NeedsRenewal(invoice, StablePrompt));

        AddPayment(invoice, LightningPayment());
        await renewer.RenewAsync(invoice);

        Assert.True(SatoraPromptRenewer.NeedsRenewal(invoice, StablePrompt));
        Assert.Equal([("invoice", Usdc.Id)], renewer.Renewed);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("threw")]
    public async Task AStablecoinRenewalTheServerRefusesLeavesTheOldPromptInactive(string outcome)
    {
        var (_, invoice, _) = await Prompted();
        AddPayment(invoice, LightningPayment());
        var credit = new RecordingStableCredit();
        var renewer = new TestRenewer(credit, () => outcome == "threw" ? throw new InvalidOperationException("swap server down") : false);

        await renewer.RenewAsync(invoice);

        Assert.Equal([("invoice", Usdc.Id, "store-swap1")], credit.Deactivated);
    }

    // A prompt still waiting for the customer's pick gets the due of the moment it is picked.
    [Fact]
    public async Task APromptNeverActivatedIsNotRenewed()
    {
        var (_, invoice, _) = await Prompted();
        AddPayment(invoice, LightningPayment());
        Deactivate(invoice);
        var renewer = new TestRenewer(new RecordingStableCredit(), () => true);

        await renewer.RenewAsync(invoice);

        Assert.Empty(renewer.Renewed);
    }

    private static readonly SatoraSwapPrompt StablePrompt = SatoraPromptRenewer.SwapPrompts.Single(p => p.Id == Usdc.Id);

    // Created waiting for the customer's pick, then activated, as BTCPay does both.
    private static async Task<(SatoraStablePaymentHandler Handler, InvoiceEntity Invoice, SatoraStableSwap Swap)> Prompted()
    {
        var handler = new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc);
        var invoice = SatoraStablePaymentTests.Invoice();
        invoice.Status = InvoiceStatus.New;
        invoice.SetPaymentPrompt(Usdc.Id, (await SatoraStablePaymentTests.Create(handler, invoice)).Prompt);
        invoice.SetPaymentPrompt(Usdc.Id, (await SatoraStablePaymentTests.Configure(handler, invoice)).Prompt);
        return (handler, invoice, handler.Swaps.Single());
    }

    // The invoice keeps its prompts serialized, so a change is written back.
    private static void Deactivate(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(Usdc.Id)!;
        prompt.Inactive = true;
        invoice.SetPaymentPrompt(Usdc.Id, prompt);
    }

    private static PaymentEntity LightningPayment() =>
        new() { Id = "lightning", Currency = "BTC", Value = 0.05m, Status = PaymentStatus.Settled, PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC") };

    private static void AddPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
#pragma warning disable CS0618
        invoice.Payments.Add(payment);
#pragma warning restore CS0618
        invoice.UpdateTotals();
    }

    private sealed class RecordingStableCredit() : SatoraStableCredit(null!, null!, null!, null!, NullLogger<SatoraStableCredit>.Instance)
    {
        public List<(string InvoiceId, PaymentMethodId Method, string SwapId)> Deactivated { get; } = [];

        public override Task DeactivatePromptAsync(string invoiceId, PaymentMethodId method, string swapId)
        {
            Deactivated.Add((invoiceId, method, swapId));
            return Task.CompletedTask;
        }
    }

    private sealed class TestRenewer(SatoraStableCredit credit, Func<bool> renew)
        : SatoraPromptRenewer(null!, null!, null!, null!, credit, NullLogger<SatoraPromptRenewer>.Instance)
    {
        public List<(string InvoiceId, PaymentMethodId Method)> Renewed { get; } = [];

        protected override Task<bool> RenewPromptAsync(string invoiceId, PaymentMethodId method)
        {
            Renewed.Add((invoiceId, method));
            return Task.FromResult(renew());
        }
    }
}
