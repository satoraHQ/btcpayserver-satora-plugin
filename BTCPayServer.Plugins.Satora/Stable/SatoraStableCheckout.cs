using System.Numerics;
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Satora.Stable;

// The EIP-681 link asking the customer's wallet for the token transfer still owed to the prompt's
// deposit address, as SatoraChainPaymentLinkExtension builds the BIP21 link.
public class SatoraStablePaymentLinkExtension(SatoraStablecoin coin) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId => coin.Id;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper) =>
        Payable(prompt) && SatoraStablePromptDetails.Of(prompt) is { } details && ToSend(details) is { } units
            ? Eip681(details.TokenAddress, details.ChainId, prompt.Destination!, units)
            : null;

    // The swap pays the wallet what was due when it was made: after another payment, and until the
    // prompt is renewed, its address is not shown for an amount the swap would not pay.
    public static bool Payable(PaymentPrompt prompt) =>
        prompt.Destination is not null &&
        SatoraStablePromptDetails.Of(prompt) is { } details &&
        prompt.Calculate().Due == Money.Satoshis(details.ReceiveSats).ToDecimal(MoneyUnit.BTC);

    // The deposit less what the worker last found at the address, so a customer who sent too little is
    // asked for the rest. Null once the address holds the whole deposit.
    public static BigInteger? ToSend(SatoraStablePromptDetails details) =>
        SatoraStableActions.Units(details.Units) is { } deposit &&
        deposit - (SatoraStableActions.Units(details.ReceivedUnits) ?? BigInteger.Zero) is var left && left > 0
            ? left
            : null;

    public static string Eip681(string token, long chainId, string to, BigInteger units) =>
        $"ethereum:{token}@{chainId}/transfer?address={to}&uint256={units}";
}

// Shaped after SatoraChainCheckoutModelExtension, with the checkout body from SatoraStableMethodCheckout.cshtml.
public class SatoraStableCheckoutModelExtension(SatoraStablecoin coin, IEnumerable<IPaymentLinkExtension> paymentLinkExtensions)
    : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "SatoraStableCheckoutBody";
    // The checkout model's own fields are bitcoin's; what the body shows in the token goes under this key.
    public const string ModelKey = "satoraStable";

    private readonly IPaymentLinkExtension _paymentLink = paymentLinkExtensions.Single(p => p.PaymentMethodId == coin.Id);

    public PaymentMethodId PaymentMethodId => coin.Id;

    public string Image => coin.Image;

    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler.PaymentMethodId != coin.Id || SatoraStablePromptDetails.Of(context.Prompt) is not { } details)
            return;
        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        var link = _paymentLink.GetPaymentLink(context.Prompt, context.UrlHelper) ?? "";
        if (!SatoraStablePaymentLinkExtension.Payable(context.Prompt))
            context.Model.Address = null;
        context.Model.InvoiceBitcoinUrl = link;
        context.Model.InvoiceBitcoinUrlQR = link;
        context.Model.AdditionalData[ModelKey] = Describe(coin, details);
    }

    public static JObject Describe(SatoraStablecoin coin, SatoraStablePromptDetails details)
    {
        var deposit = SatoraStableActions.Units(details.Units) ?? BigInteger.Zero;
        var received = BigInteger.Min(SatoraStableActions.Units(details.ReceivedUnits) ?? BigInteger.Zero, deposit);
        string Format(BigInteger units) => SatoraStableActions.FormatUnits(units, details.Decimals);
        return new JObject
        {
            ["symbol"] = coin.Symbol,
            ["network"] = coin.ChainName,
            ["contract"] = details.TokenAddress,
            ["amount"] = Format(deposit),
            ["received"] = received > 0 ? Format(received) : JValue.CreateNull(),
            ["toSend"] = Format(deposit - received),
            // The whole deposit is at the address and the swap is being funded and converted.
            ["converting"] = deposit > 0 && received == deposit
        };
    }
}
