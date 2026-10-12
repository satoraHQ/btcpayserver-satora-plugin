using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// What SatoraChainCredit and SatoraPromptRenewer write to an invoice, decided over the invoice alone.
public class SatoraChainCreditTests
{
    public static TheoryData<PaymentStatus?, PaymentStatus, SatoraChainPaymentWrite> Writes => new()
    {
        { null, PaymentStatus.Processing, SatoraChainPaymentWrite.Add },
        { null, PaymentStatus.Settled, SatoraChainPaymentWrite.Add },
        { PaymentStatus.Processing, PaymentStatus.Processing, SatoraChainPaymentWrite.None },
        { PaymentStatus.Processing, PaymentStatus.Settled, SatoraChainPaymentWrite.Settle },
        // A claim settles a payment whose funding was taken off meanwhile.
        { PaymentStatus.Unaccounted, PaymentStatus.Settled, SatoraChainPaymentWrite.Settle },
        // A funding taken off as replaced and seen again.
        { PaymentStatus.Unaccounted, PaymentStatus.Processing, SatoraChainPaymentWrite.Account },
        { PaymentStatus.Settled, PaymentStatus.Processing, SatoraChainPaymentWrite.None },
        { PaymentStatus.Settled, PaymentStatus.Settled, SatoraChainPaymentWrite.None }
    };

    // A note comes after the refund step it reports was saved, which a database that cannot take it must not fail.
    [Fact]
    public async Task ANoteTheDatabaseCannotTakeIsLoggedAndDoesNotThrow()
    {
        var unreachable = new ApplicationDbContextFactory(
            Options.Create(new DatabaseOptions { ConnectionString = "Host=127.0.0.1;Port=1;Username=none;Database=none;Timeout=2" }), NullLoggerFactory.Instance);
        var logs = new RecordingLogger<SatoraChainCredit>();
        var credit = new SatoraChainCredit(new InvoiceRepository(unreachable, new EventAggregator(new Logs())), null!, null!, null!, logs);

        await credit.NoteAsync(SatoraChainActionsTests.Swap(), "Satora on-chain swap swap needs a refund (Underpaid).", InvoiceEventData.EventSeverity.Warning);

        Assert.Equal([(LogLevel.Warning, "Could not note Satora on-chain swap swap on invoice invoice-swap")], logs.Entries);
    }

    [Theory]
    [MemberData(nameof(Writes))]
    public void WritesOnlyWhatMovesThePaymentForward(PaymentStatus? existing, PaymentStatus asked, SatoraChainPaymentWrite expected)
    {
        var payment = existing is { } status ? Payment(status) : null;

        Assert.Equal(expected, SatoraChainCredit.Write(payment, asked));
    }

    // AddPayment also returns null when saving failed for another reason; only a payment the invoice
    // read again has counts as recorded.
    [Fact]
    public void AFailedAddFallsBackToThePaymentTheInvoiceReadAgainHas()
    {
        var recorded = Payment(PaymentStatus.Processing);

        Assert.Same(recorded, SatoraChainCredit.NotAdded(Invoice(recorded), "swap", "invoice"));
        Assert.Throws<InvalidOperationException>(() => SatoraChainCredit.NotAdded(Invoice(), "swap", "invoice"));
        Assert.Throws<InvalidOperationException>(() => SatoraChainCredit.NotAdded(Invoice(Payment(PaymentStatus.Processing, "other")), "swap", "invoice"));
        Assert.Throws<InvalidOperationException>(() => SatoraChainCredit.NotAdded(null, "swap", "invoice"));
    }

    [Fact]
    public void TakesOffOnlyAPaymentTheInvoiceStillCounts()
    {
        Assert.NotNull(SatoraChainCredit.ToUnaccount(Invoice(Payment(PaymentStatus.Processing)), "swap"));
        Assert.NotNull(SatoraChainCredit.ToUnaccount(Invoice(Payment(PaymentStatus.Settled)), "swap"));
        Assert.Null(SatoraChainCredit.ToUnaccount(Invoice(Payment(PaymentStatus.Unaccounted)), "swap"));
        Assert.Null(SatoraChainCredit.ToUnaccount(Invoice(Payment(PaymentStatus.Processing, "other")), "swap"));
        Assert.Null(SatoraChainCredit.ToUnaccount(null, "swap"));
    }

    [Fact]
    public void DeactivatesOnlyTheOpenInvoicesActivePromptForThatSwap()
    {
        Assert.NotNull(SatoraChainCredit.ToDeactivate(Prompted(), "swap"));
        // Its payment was just taken off, and InvoiceWatcher has not moved it back to New yet.
        Assert.NotNull(SatoraChainCredit.ToDeactivate(Prompted(status: InvoiceStatus.Processing), "swap"));
        // Renewed meanwhile: the new swap's prompt stays.
        Assert.Null(SatoraChainCredit.ToDeactivate(Prompted(), "other"));
        Assert.Null(SatoraChainCredit.ToDeactivate(Prompted(active: false), "swap"));
        Assert.Null(SatoraChainCredit.ToDeactivate(Prompted(status: InvoiceStatus.Expired), "swap"));
        Assert.Null(SatoraChainCredit.ToDeactivate(Prompted(status: InvoiceStatus.Settled), "swap"));
        Assert.Null(SatoraChainCredit.ToDeactivate(Invoice(), "swap"));
        Assert.Null(SatoraChainCredit.ToDeactivate(null, "swap"));
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("threw")]
    public async Task ARenewalTheServerRefusesLeavesTheOldPromptInactive(string outcome)
    {
        var credit = new RecordingCredit();
        var renewer = new TestRenewer(credit, () => outcome == "threw" ? throw new InvalidOperationException("swap server down") : false);

        await renewer.RenewAsync(Partial());

        Assert.Equal([("invoice", "swap")], credit.Deactivated);
    }

    [Fact]
    public async Task ARenewedPromptStaysActive()
    {
        var credit = new RecordingCredit();
        var renewer = new TestRenewer(credit, () => true);

        await renewer.RenewAsync(Partial());
        await renewer.RenewAsync(Prompted());

        Assert.Equal(1, renewer.Renewals);
        Assert.Empty(credit.Deactivated);
    }

    // A payment taken off changes the invoice's state without a ReceivedPayment event.
    [Fact]
    public async Task ChecksAnOpenInvoiceAgainWheneverItsStateChanges()
    {
        var events = new EventAggregator(new Logs());
        var invoice = Partial();
        invoice.ExceptionStatus = InvoiceExceptionStatus.None;
        var renewer = new TestRenewer(new RecordingCredit(), () => true, events, invoice);
        await renewer.StartAsync(Ct);

        events.Publish(new InvoiceDataChangedEvent(invoice));

        Assert.Equal("invoice", await renewer.Renewed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await renewer.StopAsync(Ct);
    }

    // A partial payment raises ReceivedPayment and, a few ms later, InvoiceDataChangedEvent. The second
    // must find the prompt the first renewed, even while the swap server is still answering the first.
    // A payment event carries the invoice as it was then, so it never stands in for a fresh read.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APartialPaymentsTwoEventsRenewThePromptOnce(bool paymentEventLast)
    {
        var events = new EventAggregator(new Logs());
        var invoice = Partial();
        var renewer = new TestRenewer(new RecordingCredit(), () => Renew(invoice), events, invoice) { Delay = TimeSpan.FromMilliseconds(500) };
        await renewer.StartAsync(Ct);

        var paymentEvent = new InvoiceEvent(Partial(), InvoiceEvent.ReceivedPayment);
        if (!paymentEventLast)
            events.Publish(paymentEvent);
        events.Publish(new InvoiceDataChangedEvent(invoice));
        if (paymentEventLast)
            events.Publish(paymentEvent);
        await renewer.Finished.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await renewer.StopAsync(Ct);

        Assert.Equal(1, renewer.Renewals);
        Assert.False(SatoraPromptRenewer.NeedsRenewal(invoice, SatoraPromptRenewer.SwapPrompts[0]));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PaymentEntity Payment(PaymentStatus status, string swapId = "swap") =>
        new() { Id = SatoraChainCredit.PaymentId(swapId), Currency = "BTC", Value = 0.001m, Status = status, PaymentMethodId = SatoraChainPaymentMethod.Id };

    private static InvoiceEntity Invoice(params PaymentEntity[] payments)
    {
        var invoice = new InvoiceEntity { Id = "invoice", Currency = "USD", Price = 4000, StoreId = "store", Status = InvoiceStatus.New };
#pragma warning disable CS0618
        invoice.Payments = payments.ToList();
        invoice.Rates["BTC"] = 34_000m;
#pragma warning restore CS0618
        invoice.UpdateTotals();
        return invoice;
    }

    // Details as the handler stores them, in camelCase.
    private static InvoiceEntity Prompted(bool active = true, InvoiceStatus status = InvoiceStatus.New, params PaymentEntity[] payments)
    {
        var invoice = Invoice(payments);
        invoice.Status = status;
        invoice.SetPaymentPrompt(SatoraChainPaymentMethod.Id, new PaymentPrompt
        {
            Currency = "BTC",
            Divisibility = 8,
            Destination = "htlc-swap",
            Inactive = !active,
            PaymentMethodFee = 0.00000235m,
            Details = JObject.Parse("""{"swapId":"swap","depositSats":11764941}""")
        });
        return invoice;
    }

    // Paid in part over Lightning, so the swap's deposit is no longer what is due.
    private static InvoiceEntity Partial()
    {
        var invoice = Prompted(payments: [new PaymentEntity { Id = "lightning", Currency = "BTC", Value = 0.05m, Status = PaymentStatus.Settled, PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC") }]);
        invoice.ExceptionStatus = InvoiceExceptionStatus.PaidPartial;
        return invoice;
    }

    // As InvoiceActivator saves a renewal: a new swap whose deposit is what is due now.
    private static bool Renew(InvoiceEntity invoice)
    {
        var prompt = invoice.GetPaymentPrompt(SatoraChainPaymentMethod.Id)!;
        prompt.Details = JObject.FromObject(new { swapId = "renewed", depositSats = Money.Coins(prompt.Calculate().Due).Satoshi });
        invoice.SetPaymentPrompt(SatoraChainPaymentMethod.Id, prompt);
        return true;
    }

    private sealed class RecordingCredit() : SatoraChainCredit(null!, null!, null!, null!, NullLogger<SatoraChainCredit>.Instance)
    {
        public List<(string InvoiceId, string SwapId)> Deactivated { get; } = [];

        public override Task DeactivatePromptAsync(string invoiceId, string swapId)
        {
            Deactivated.Add((invoiceId, swapId));
            return Task.CompletedTask;
        }
    }

    private sealed class TestRenewer(SatoraChainCredit credit, Func<bool> renew, EventAggregator? events = null, InvoiceEntity? stored = null)
        : SatoraPromptRenewer(events!, null!, null!, credit, null!, NullLogger<SatoraPromptRenewer>.Instance)
    {
        public int Renewals { get; private set; }

        public TaskCompletionSource<string> Renewed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        // How long the swap server takes to answer.
        public TimeSpan Delay { get; init; }

        protected override async Task<bool> RenewPromptAsync(string invoiceId, PaymentMethodId method)
        {
            Renewals++;
            Renewed.TrySetResult(invoiceId);
            await Task.Delay(Delay);
            var renewed = renew();
            Finished.TrySetResult();
            return renewed;
        }

        protected override Task<InvoiceEntity?> ReadInvoiceAsync(string invoiceId) => Task.FromResult(stored);
    }
}
