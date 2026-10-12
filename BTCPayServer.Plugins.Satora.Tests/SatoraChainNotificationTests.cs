using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Satora.Chain;
using Xunit;
using Problems = BTCPayServer.Plugins.Satora.Chain.SatoraChainNotification.Problems;

namespace BTCPayServer.Plugins.Satora.Tests;

// The plugin's own page lists no on-chain swaps, so an alert names the swap's invoice and leads to it,
// or for a refund to the refunds page, which lists the swap with its invoice.
public class SatoraChainNotificationTests
{
    [Theory]
    [InlineData(Problems.Overpaid, "invoice-1", "UIInvoice/Invoice/invoice-1")]
    [InlineData(Problems.PaidAfterPaidInFull, "invoice-1", "UIInvoice/Invoice/invoice-1")]
    [InlineData(Problems.NeedsRefund, "invoice-1", "SatoraChainRefund/Index/store")]
    // Sent before alerts named the invoice.
    [InlineData(Problems.Overpaid, null, "SatoraPlugin/Index/store")]
    public void AnAlertAboutASwapNamesAndLeadsToItsInvoice(Problems problem, string? invoiceId, string link)
    {
        INotificationHandler handler = new SatoraChainNotification.Handler(new RouteLinks(), new BTCPayServerOptions { RootPath = "/" });
        var vm = new NotificationViewModel { Created = DateTimeOffset.UtcNow };

        handler.FillViewModel(new SatoraChainNotification { StoreId = "store", SwapId = "swap", InvoiceId = invoiceId, Problem = problem, ExcessSats = 1_000 }, vm);

        Assert.Equal(link, vm.ActionLink);
        Assert.Contains(invoiceId is null ? "swap swap " : "swap swap of invoice invoice-1", vm.Body);
    }

    // The swap may be one a renewal replaced or the invoice's own, paid after another method settled
    // the invoice: the alert says what the plugin knows, not which.
    [Fact]
    public void AnAlertAboutAPaymentAfterThePaidInFullSaysOnlyWhatIsKnown()
    {
        var body = SatoraChainNotification.Body(new SatoraChainNotification
        {
            StoreId = "store",
            SwapId = "swap",
            InvoiceId = "invoice-1",
            Problem = Problems.PaidAfterPaidInFull,
            ExcessSats = 20_000
        }, DateTimeOffset.UtcNow);

        Assert.Equal("Satora on-chain swap swap of invoice invoice-1 was claimed into the Satora wallet after the invoice's other payments had paid it in full. Its 20000 sats are credited on the invoice and are in the wallet, but BTCPay does not show a settled invoice as overpaid. Settle them with the customer.",
            body);
    }
}
