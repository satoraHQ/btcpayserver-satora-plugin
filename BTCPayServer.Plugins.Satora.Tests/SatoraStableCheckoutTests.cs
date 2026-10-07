using System.Numerics;
using BTCPayServer.Data;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services.Invoices;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// What the checkout and the payment link show for a stablecoin prompt, over a prompt the handler made.
public class SatoraStableCheckoutTests
{
    private static readonly SatoraStablecoin Usdc = SatoraStablePaymentMethod.UsdcArbitrum;
    // The fake handler's first swap: 10.13 USDC to its own deposit address.
    private static readonly string Deposit = $"0x{1:x40}";

    [Fact]
    public async Task TheLinkAsksTheWalletForTheWholeDepositAsAnEip681TokenTransfer()
    {
        var (_, prompt) = await Prompted();

        Assert.Equal($"ethereum:0xaf88d065e77c8cc2239327c5edb3a432268e5831@42161/transfer?address={Deposit}&uint256=10130000",
            new SatoraStablePaymentLinkExtension(Usdc).GetPaymentLink(prompt, null));
    }

    // The customer sent too little; the worker wrote what the address holds into the prompt.
    [Theory]
    [InlineData("10000000", "130000")]
    [InlineData("0", "10130000")]
    [InlineData("10130000", null)]
    [InlineData("12000000", null)]
    public async Task TheLinkAsksOnlyForWhatTheAddressStillLacks(string received, string? toSend)
    {
        var (invoice, prompt) = await Prompted(received);

        var link = new SatoraStablePaymentLinkExtension(Usdc).GetPaymentLink(prompt, null);

        Assert.Equal(toSend is null ? null : $"ethereum:{Usdc.Contract}@42161/transfer?address={Deposit}&uint256={toSend}", link);
        Assert.True(SatoraStablePaymentLinkExtension.Payable(invoice.GetPaymentPrompt(Usdc.Id)!));
    }

    // A partial payment through another method, until the prompt is renewed, or a renewal the server refused.
    [Fact]
    public async Task NeitherTheLinkNorTheCheckoutShowTheAddressForAnAmountTheSwapWouldNotPay()
    {
        var (invoice, prompt) = await Prompted();
        var link = new SatoraStablePaymentLinkExtension(Usdc);
        var checkout = new SatoraStableCheckoutModelExtension(Usdc, [link]);
        CheckoutModel Shown()
        {
            var model = new CheckoutModel { Address = prompt.Destination };
            checkout.ModifyCheckoutModel(new CheckoutModelContext(model, null!, null!, invoice, null!, prompt, new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc)));
            return model;
        }

        Assert.Equal(Deposit, Shown().Address);
#pragma warning disable CS0618
        invoice.Payments.Add(new PaymentEntity { Id = "lightning", Currency = "BTC", Value = 0.05m, Status = PaymentStatus.Settled, PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC") });
#pragma warning restore CS0618
        invoice.UpdateTotals();

        Assert.True(prompt.Calculate().Due > 0m);
        Assert.False(SatoraStablePaymentLinkExtension.Payable(prompt));
        Assert.Null(link.GetPaymentLink(prompt, null));
        Assert.True(Shown() is { Address: null, InvoiceBitcoinUrl: "", InvoiceBitcoinUrlQR: "" });
    }

    [Fact]
    public async Task TheCheckoutBodyGetsTheTokenAmountItsNetworkAndItsContract()
    {
        var (invoice, prompt) = await Prompted();
        var link = new SatoraStablePaymentLinkExtension(Usdc);
        var model = new CheckoutModel { Address = prompt.Destination };

        new SatoraStableCheckoutModelExtension(Usdc, [link]).ModifyCheckoutModel(
            new CheckoutModelContext(model, null!, null!, invoice, null!, prompt, new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc)));

        Assert.Equal(SatoraStableCheckoutModelExtension.CheckoutBodyComponentName, model.CheckoutBodyComponentName);
        Assert.Equal(link.GetPaymentLink(prompt, null), model.InvoiceBitcoinUrl);
        Assert.Equal(model.InvoiceBitcoinUrl, model.InvoiceBitcoinUrlQR);
        Assert.True(JToken.DeepEquals(JObject.FromObject(new
        {
            symbol = "USDC",
            network = "Arbitrum One",
            contract = Usdc.Contract,
            amount = "10.130000",
            received = (string?)null,
            toSend = "10.130000",
            converting = false
        }), model.AdditionalData[SatoraStableCheckoutModelExtension.ModelKey]));
        Assert.Equal("Resources/ico-satora/USDC.webp", new SatoraStableCheckoutModelExtension(Usdc, [link]).Image);
    }

    [Theory]
    [InlineData("4000000", "4.000000", "6.130000", false)]
    [InlineData("10130000", "10.130000", "0.000000", true)]
    // An overpayment shows the deposit as received; the excess is the worker's to report.
    [InlineData("20000000", "10.130000", "0.000000", true)]
    public async Task TheCheckoutBodyShowsWhatArrivedAndWhatIsLeft(string received, string shownReceived, string toSend, bool converting)
    {
        var (_, prompt) = await Prompted(received);

        var described = SatoraStableCheckoutModelExtension.Describe(Usdc, SatoraStablePromptDetails.Of(prompt)!);

        Assert.Equal((shownReceived, toSend, converting),
            (described["received"]!.Value<string>(), described["toSend"]!.Value<string>(), described["converting"]!.Value<bool>()));
    }

    [Fact]
    public async Task ThePromptOfAnotherMethodIsLeftAlone()
    {
        var (invoice, prompt) = await Prompted();
        var model = new CheckoutModel { Address = prompt.Destination };

        new SatoraStableCheckoutModelExtension(Usdc, [new SatoraStablePaymentLinkExtension(Usdc)]).ModifyCheckoutModel(
            new CheckoutModelContext(model, null!, null!, invoice, null!, prompt, new SatoraChainPaymentHandler(SatoraStablePaymentTests.Btc, null!, null!)));

        Assert.True(model is { CheckoutBodyComponentName: null, InvoiceBitcoinUrl: null });
        Assert.Empty(model.AdditionalData);
    }

    [Theory]
    [InlineData("10130000", 6, "10.130000")]
    [InlineData("1", 6, "0.000001")]
    [InlineData("0", 6, "0.000000")]
    [InlineData("123456789012345678901234567890", 18, "123456789012.345678901234567890")]
    [InlineData("42", 0, "42")]
    public void TokenAmountsShowEveryDecimalTheTokenHas(string units, int decimals, string shown) =>
        Assert.Equal(shown, SatoraStableActions.FormatUnits(BigInteger.Parse(units), decimals));

    [Theory]
    [InlineData("10130000", true)]
    [InlineData("0", true)]
    [InlineData("-1", false)]
    [InlineData("1.5", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TokenAmountsAreDecimalIntegers(string? units, bool parsed) =>
        Assert.Equal(parsed, SatoraStableActions.Units(units) is not null);

    // As BTCPay activates the prompt, then with what the worker last found at the deposit address.
    private static async Task<(InvoiceEntity Invoice, PaymentPrompt Prompt)> Prompted(string? received = null)
    {
        var handler = new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc);
        var invoice = SatoraStablePaymentTests.Invoice();
        var prompt = (await SatoraStablePaymentTests.Configure(handler, invoice)).Prompt;
        if (received is not null)
        {
            var details = handler.ParsePaymentPromptDetails(prompt.Details);
            details.ReceivedUnits = received;
            prompt.Details = JObject.FromObject(details, handler.Serializer);
        }
        invoice.SetPaymentPrompt(Usdc.Id, prompt);
        return (invoice, prompt);
    }
}
