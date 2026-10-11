using BTCPayServer.Payments;
using BTCPayServer.Services;

namespace BTCPayServer.Plugins.Satora.Services;

// Some store pages list payment methods by raw id, BTC-SATORA-CHAIN rather than Bitcoin (Satora): the
// create invoice form (UIInvoiceController.GetPaymentMethodsSelectList), the checkout settings and an
// invoice's payments. The SatoraPaymentMethodNames partial relabels them with these names. Every method
// BTCPay has a handler for is named, not only the store's own, because turning the Satora wallet off
// removes its method while the store's invoices still list it.
public static class SatoraPaymentMethodNames
{
    // The partial relabels an element of these whose whole text is an id, by rewriting the one text node
    // that holds it, so an input inside the element stays.
    public const string Labels = "option[value], .form-check-label, td, th";

    // Text a form submits, which is never relabelled: a textarea's, and an option's when it has no value
    // attribute.
    public const string FormText = "textarea, option:not([value])";

    // Empty outside a store's pages, so the partial renders nothing there.
    public static IReadOnlyDictionary<string, string> For(string? storeId, IEnumerable<PaymentMethodId> paymentMethodIds, PrettyNameProvider prettyNames)
    {
        if (storeId is null)
            return new Dictionary<string, string>();
        var names = new Dictionary<string, string>();
        foreach (var id in paymentMethodIds)
        {
            var name = prettyNames.PrettyName(id);
            if (name != id.ToString())
                names[id.ToString()] = name;
        }
        return names;
    }
}
