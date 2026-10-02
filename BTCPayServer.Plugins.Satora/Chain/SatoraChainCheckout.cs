using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using NBitcoin;
using NBitcoin.DataEncoders;

namespace BTCPayServer.Plugins.Satora.Chain;

// The BIP21 link to the swap's HTLC address for what is due, as BitcoinPaymentLinkExtension builds it.
public class SatoraChainPaymentLinkExtension(BTCPayNetwork network) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId => SatoraChainPaymentMethod.Id;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper) =>
        Payable(prompt) ? network.GenerateBIP21(prompt.Destination, prompt.Calculate().Due).ToString() : null;

    // The swap's HTLC takes its deposit and nothing else: after another payment, and until the prompt
    // is renewed, its address is not shown for an amount it would refuse.
    public static bool Payable(PaymentPrompt prompt) =>
        prompt.Destination is not null &&
        SatoraChainPromptDetails.Of(prompt) is { } details &&
        prompt.Calculate().Due == Money.Satoshis(details.DepositSats).ToDecimal(MoneyUnit.BTC);
}

// Shaped after BitcoinCheckoutModelExtension, with the checkout body from SatoraChainMethodCheckout.cshtml.
public class SatoraChainCheckoutModelExtension(BTCPayNetwork network, IEnumerable<IPaymentLinkExtension> paymentLinkExtensions)
    : ICheckoutModelExtension
{
    public const string CheckoutBodyComponentName = "SatoraChainCheckoutBody";

    private readonly IPaymentLinkExtension _paymentLink = paymentLinkExtensions.Single(p => p.PaymentMethodId == SatoraChainPaymentMethod.Id);

    public PaymentMethodId PaymentMethodId => SatoraChainPaymentMethod.Id;

    public string Image => network.CryptoImagePath;

    public string Badge => "";

    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context.Handler is not SatoraChainPaymentHandler)
            return;
        context.Model.CheckoutBodyComponentName = CheckoutBodyComponentName;
        var bip21 = _paymentLink.GetPaymentLink(context.Prompt, context.UrlHelper) ?? "";
        if (bip21 == "")
            context.Model.Address = null;
        context.Model.InvoiceBitcoinUrl = bip21;
        context.Model.InvoiceBitcoinUrlQR = ForQrCode(bip21, context.Model.Address, network.NBitcoinNetwork);
    }

    // As BitcoinCheckoutModelExtension: an uppercase bech32 address makes a denser QR code, while the
    // URI scheme stays lowercase.
    public static string ForQrCode(string bip21, string? address, Network network)
    {
        var hrp = network.GetBech32Encoder(Bech32Type.WITNESS_PUBKEY_ADDRESS, false) is { } encoder
            ? Encoders.ASCII.EncodeData(encoder.HumanReadablePart)
            : null;
        if (string.IsNullOrEmpty(address) || hrp is null || !address.StartsWith(hrp, StringComparison.OrdinalIgnoreCase))
            return bip21;
        return bip21.Replace($"{network.UriScheme}:{address}", $"{network.UriScheme}:{address.ToUpperInvariant()}", StringComparison.OrdinalIgnoreCase);
    }
}
