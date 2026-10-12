using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using StoreData = BTCPayServer.Data.StoreData;
using BTCPayServer.Logging;
using BTCPayServer.Models.InvoicingModels;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Services.Invoices;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using Xunit;
using SdkSwapDirection = uniffi.satora_sdk_ffi.SwapDirection;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;
using SdkTokenId = uniffi.satora_sdk_ffi.TokenId;

namespace BTCPayServer.Plugins.Satora.Tests;

// The prompt, the swap row behind it and the payments the worker records, against BTCPay's own invoice accounting.
public class SatoraChainPaymentTests
{
    private static readonly BTCPayNetwork Btc = new()
    {
        CryptoCode = "BTC",
        NBXplorerNetwork = new NBXplorerNetworkProvider(ChainName.Regtest).GetFromCryptoCode("BTC")
    };
    private static readonly BTCPayNetwork Mainnet = new()
    {
        CryptoCode = "BTC",
        NBXplorerNetwork = new NBXplorerNetworkProvider(ChainName.Mainnet).GetFromCryptoCode("BTC")
    };
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    // Created() is claimable until 1 800 000 000 less the 300 s claim margin.
    private static readonly DateTimeOffset Expiry = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 - 300 - 3600);
    private static readonly string Htlc = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();
    private const long Fee = 235;

    // 4000 USD at 34000 USD/BTC is 0.11764706 BTC once BTCPay rounds it up to whole sats.
    private const long DueSats = 11_764_706;

    [Fact]
    public async Task ThePromptAsksForTheDepositAndTheDepositPaysTheInvoiceExactly()
    {
        var invoice = Invoice();
        var handler = new FakeSwapHandler();

        var prompt = await Configure(handler, invoice);

        // The swap pays exactly what is due into the wallet; the customer pays its fee on top.
        Assert.Equal([DueSats], handler.Requested);
        Assert.Equal(Htlc, prompt.Destination);
        Assert.Equal(8, prompt.Divisibility);
        Assert.Equal(Fee / 100_000_000m, prompt.PaymentMethodFee);
        Assert.Equal((DueSats + Fee) / 100_000_000m, prompt.Calculate().Due);
        Assert.Equal(("swap", DueSats + Fee), (handler.ParsePaymentPromptDetails(prompt.Details).SwapId, handler.ParsePaymentPromptDetails(prompt.Details).DepositSats));
        Assert.Equal(invoice.ExpirationTime, handler.Expiries.Single());

        AddPayment(invoice, SatoraChainCredit.ToPaymentData(handler.Swap!, handler, PaymentStatus.Processing, Now));

        Assert.Equal((false, false, 0m), (invoice.IsUnderPaid, invoice.IsOverPaid, invoice.NetDue));
        Assert.Equal(0m, prompt.Calculate().Due);
    }

    // The claim lands the receive amount in the wallet; recording that instead of the deposit would
    // leave the swap fee due and ask the customer for it again.
    [Fact]
    public async Task APaymentOfTheClaimedAmountWouldLeaveTheInvoiceUnderpaid()
    {
        var invoice = Invoice();
        var handler = new FakeSwapHandler();
        await Configure(handler, invoice);
        var claimed = SatoraChainCredit.ToPaymentData(handler.Swap!, handler, PaymentStatus.Settled, Now);
        claimed.Amount = handler.Swap!.ReceiveSats / 100_000_000m;

        AddPayment(invoice, claimed);

        Assert.True(invoice.IsUnderPaid);
    }

    [Fact]
    public async Task APromptRenewedAfterAPartialPaymentAsksForWhatIsLeft()
    {
        var invoice = Invoice();
        var other = new PaymentEntity
        {
            Id = "lightning",
            Currency = "BTC",
            Value = 0.05m,
            Status = PaymentStatus.Settled,
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC")
        };
#pragma warning disable CS0618
        invoice.Payments.Add(other);
#pragma warning restore CS0618
        invoice.UpdateTotals();
        var handler = new FakeSwapHandler();

        await Configure(handler, invoice);

        Assert.Equal([DueSats - 5_000_000], handler.Requested);
    }

    [Fact]
    public async Task ATopUpInvoiceHasNoAmountForASwap()
    {
        var invoice = Invoice();
        invoice.Type = InvoiceType.TopUp;
        var handler = new FakeSwapHandler();

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Configure(handler, invoice));
        Assert.Empty(handler.Requested);
    }

    // BTCPay leaves a method out of the invoice on PaymentMethodUnavailableException, and logs anything else as unexpected.
    [Fact]
    public async Task ARefusedOrSlowSwapMakesTheMethodUnavailable()
    {
        var refused = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => SatoraChainPaymentHandler.AsUnavailable<SatoraChainSwap>(
            () => throw new SdkException(SdkErrorKind.Api, "amount below the route minimum", 400), TimeSpan.FromSeconds(5)));
        Assert.Contains("amount below the route minimum", refused.Message);

        var slow = new TaskCompletionSource<SatoraChainSwap>();
        var timedOut = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() =>
            SatoraChainPaymentHandler.AsUnavailable(() => slow.Task, TimeSpan.FromMilliseconds(50)));
        Assert.Contains("in time", timedOut.Message);

        var disabled = new PaymentMethodUnavailableException("The store's Satora wallet is not enabled.");
        Assert.Same(disabled, await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() =>
            SatoraChainPaymentHandler.AsUnavailable<SatoraChainSwap>(() => throw disabled, TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public void RecordsTheSwapTheServerReturned()
    {
        var row = SatoraChainPaymentHandler.ToRow(Created(), "store", "invoice", Network.RegTest, 99_000, Expiry, Now, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(("swap", "store", "invoice", Htlc, 99_000L + Fee, 99_000L, SatoraSwapStatus.Pending), (row.SwapId, row.StoreId, row.InvoiceId, row.HtlcAddress, row.DepositSats, row.ReceiveSats, row.Status));
        Assert.Equal((DateTimeOffset.FromUnixTimeSeconds(1_800_000_000), DateTimeOffset.FromUnixTimeSeconds(1_800_043_200)), (row.VhtlcRefundLocktime, row.BtcRefundLocktime));
        Assert.Equal((Now, (DateTimeOffset?)null), (row.CreatedAt, row.CompletedAt));
    }

    public static TheoryData<string, string> Refused => new()
    {
        { "98999", "99235" },
        { "99000", "98999" },
        // Past 3% plus 2000 sats of the amount due.
        { "99000", "103971" },
        { "99 000", "99235" },
        { "", "99235" }
    };

    // Nobody sees the address of a refused swap, so its row is closed from the start.
    [Theory]
    [MemberData(nameof(Refused))]
    public void ASwapWithTheWrongAmountsIsRecordedClosedAndRefused(string receive, string deposit)
    {
        var created = Created() with { ReceiveAmount = receive, DepositAmount = deposit };

        var row = SatoraChainPaymentHandler.ToRow(created, "store", "invoice", Network.RegTest, 99_000, Expiry, Now, out var refusal);

        Assert.IsType<InvalidOperationException>(refusal);
        Assert.Equal(Now, row.CompletedAt);
    }

    [Fact]
    public void AcceptsAFeeUpToTheCeiling()
    {
        var created = Created() with { DepositAmount = (99_000 + SatoraChainPaymentHandler.MaxFeeSats(99_000)).ToString() };

        var row = SatoraChainPaymentHandler.ToRow(created, "store", "invoice", Network.RegTest, 99_000, Expiry, Now, out var refusal);

        Assert.Null(refusal);
        Assert.Equal(103_970, row.DepositSats);
    }

    // A payment made at the invoice's expiry could not confirm before the claim cutoff, and the server
    // would never fund it.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ASwapClaimableOnlyUntilShortlyAfterTheInvoiceExpiresIsRecordedClosedAndRefused(int secondsPastTheAllowance, bool refused)
    {
        var cutoff = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 - 300);
        var expiry = cutoff - SatoraChainActions.ConfirmationAllowance(Network.RegTest) + TimeSpan.FromSeconds(secondsPastTheAllowance);

        var row = SatoraChainPaymentHandler.ToRow(Created(), "store", "invoice", Network.RegTest, 99_000, expiry, Now, out var refusal);

        Assert.Equal(refused, refusal is InvalidOperationException);
        Assert.Equal(refused ? Now : null, row.CompletedAt);
        Assert.Equal(expiry, row.InvoiceExpiresAt);
    }

    public static TheoryData<string> Unusable => ["direction", "funding", "network", "vhtlc", "htlc"];

    [Theory]
    [MemberData(nameof(Unusable))]
    public void AResponseNobodyCouldPayIsRefusedBeforeAnythingIsRecorded(string flaw)
    {
        var created = flaw switch
        {
            "direction" => Created() with { Direction = new SdkSwapDirection.LightningToArkade() },
            "funding" => Created() with { Funding = new SdkSwapFunding.Bolt11Invoice("lnbcrt1") },
            "network" => Created() with { Funding = new SdkSwapFunding.BitcoinAddress(new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.Main).ToString()) },
            "vhtlc" => Created() with { VhtlcRefundLocktime = null },
            _ => Created() with { BtcRefundLocktime = null }
        };

        Assert.Throws<InvalidOperationException>(() => SatoraChainPaymentHandler.ToRow(created, "store", "invoice", Network.RegTest, 99_000, Expiry, Now, out _));
    }

    [Fact]
    public void EveryPaymentCarriesTheDepositAndTheSwapsOwnAddressAndFee()
    {
        var handler = new FakeSwapHandler();
        var swap = SatoraChainActionsTests.Swap();

        var payment = SatoraChainCredit.ToPaymentData(swap, handler, PaymentStatus.Processing, Now).GetBlob();

        Assert.Equal(("satora-swap:swap", SatoraChainPaymentMethod.Id, PaymentStatus.Processing, "BTC", 0.001m),
            (payment.Id, payment.PaymentMethodId, payment.Status, payment.Currency, payment.Value));
        Assert.Equal(("htlc-swap", 0.00000500m, 8), (payment.Destination, payment.PaymentMethodFee, payment.Divisibility));
        Assert.Equal("swap", handler.ParsePaymentDetails(payment.Details).SwapId);

        swap.ClaimTxId = "ark";
        payment.Value = 0.000995m;
        SatoraChainCredit.Settle(payment, swap, handler, afterPaidInFull: false);

        Assert.Equal((PaymentStatus.Settled, 0.001m, "ark"), (payment.Status, payment.Value, handler.ParsePaymentDetails(payment.Details).ClaimTxId));
    }

    [Fact]
    public void FindsOnlyTheSwapsOwnPaymentOnThisMethod()
    {
        var invoice = Invoice();
        var handler = new FakeSwapHandler();
        invoice.SetPaymentPrompt(SatoraChainPaymentMethod.Id, new PaymentPrompt { Currency = "BTC", Divisibility = 8 });
        AddPayment(invoice, SatoraChainCredit.ToPaymentData(SatoraChainActionsTests.Swap(), handler, PaymentStatus.Processing, Now));
#pragma warning disable CS0618
        invoice.Payments.Add(new PaymentEntity { Id = "satora-swap:other", Currency = "BTC", PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC") });
#pragma warning restore CS0618

        Assert.Equal("satora-swap:swap", SatoraChainCredit.Find(invoice, "swap")?.Id);
        Assert.Null(SatoraChainCredit.Find(invoice, "other"));
        Assert.Null(SatoraChainCredit.Find(null, "swap"));
    }

    // As when the address of a swap a renewal replaced is paid too: only the invoice's other settled
    // payments count, as one still processing can be replaced, and the swap's own never does.
    [Fact]
    public void AnInvoiceIsPaidInFullWithoutASwapOnlyByItsOtherSettledPayments()
    {
        var invoice = Invoice();
        var handler = new FakeSwapHandler();
        invoice.SetPaymentPrompt(SatoraChainPaymentMethod.Id, new PaymentPrompt { Currency = "BTC", Divisibility = 8 });
        var first = SatoraChainActionsTests.Swap(swapId: "first");
        (first.DepositSats, first.ReceiveSats) = (DueSats + Fee, DueSats);
        AddPayment(invoice, SatoraChainCredit.ToPaymentData(first, handler, PaymentStatus.Processing, Now));

        Assert.False(SatoraChainCredit.PaidInFullWithout(invoice, "second"));

        SatoraChainCredit.Settle(SatoraChainCredit.Find(invoice, "first")!, first, handler, afterPaidInFull: false);
        invoice.UpdateTotals();

        Assert.True(SatoraChainCredit.PaidInFullWithout(invoice, "second"));
        Assert.False(SatoraChainCredit.PaidInFullWithout(invoice, "first"));
    }

    // As BTCPay judges it, with the invoice's payment tolerance.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ASettledPaymentShortByLessThanTheToleranceHasPaidTheInvoiceInFull(double tolerance, bool paidInFull)
    {
        var invoice = Invoice();
        invoice.PaymentTolerance = tolerance;
        AddPayment(invoice, new PaymentEntity
        {
            Id = "lightning",
            Currency = "BTC",
            Value = DueSats * 0.995m / 100_000_000m,
            Status = PaymentStatus.Settled,
            PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC")
        });

        Assert.Equal((paidInFull, paidInFull), (SatoraChainCredit.PaidInFullWithout(invoice, "swap"), !invoice.IsUnderPaid));
    }

    // A partial payment through another method, until the prompt is renewed, or a renewal the server refused.
    [Fact]
    public async Task NeitherTheLinkNorTheCheckoutShowTheAddressForAnAmountTheSwapWouldRefuse()
    {
        var invoice = Invoice();
        var handler = new FakeSwapHandler();
        var prompt = await Configure(handler, invoice);
        var link = new SatoraChainPaymentLinkExtension(Btc);
        var checkout = new SatoraChainCheckoutModelExtension(Btc, [link]);
        string? Shown()
        {
            var model = new CheckoutModel { Address = prompt.Destination };
            checkout.ModifyCheckoutModel(new CheckoutModelContext(model, null!, null!, invoice, null!, prompt, handler));
            return model.Address;
        }

        Assert.Equal(Htlc, Shown());
#pragma warning disable CS0618
        invoice.Payments.Add(new PaymentEntity { Id = "lightning", Currency = "BTC", Value = 0.05m, Status = PaymentStatus.Settled, PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC") });
#pragma warning restore CS0618
        invoice.UpdateTotals();

        Assert.True(prompt.Calculate().Due > 0m);
        Assert.Null(link.GetPaymentLink(prompt, null));
        Assert.Null(Shown());
    }

    [Fact]
    public async Task TheCheckoutLinksTheHtlcAddressForTheDepositAndUppercasesItForTheQrCode()
    {
        var prompt = await Configure(new FakeSwapHandler(), Invoice());

        var bip21 = new SatoraChainPaymentLinkExtension(Btc).GetPaymentLink(prompt, null);

        Assert.Equal($"bitcoin:{Htlc}?amount=0.11764941", bip21);
        Assert.Equal($"bitcoin:{Htlc.ToUpperInvariant()}?amount=0.11764941", SatoraChainCheckoutModelExtension.ForQrCode(bip21!, Htlc, Network.RegTest));
        var legacy = new Key().PubKey.GetAddress(ScriptPubKeyType.Legacy, Network.RegTest).ToString();
        Assert.Equal($"bitcoin:{legacy}", SatoraChainCheckoutModelExtension.ForQrCode($"bitcoin:{legacy}", legacy, Network.RegTest));
    }

    [Fact]
    public void TheStoreOffersTheMethodOnlyWhileTheWalletIsEnabledAndKeepsItsOwnOnChainWallet()
    {
        var handler = new FakeSwapHandler();
        var store = new StoreData { Id = "store" };
        var onChain = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        store.SetPaymentMethodConfig(onChain, JToken.Parse("""{"accountDerivation":"tpub"}"""));

        Assert.True(SatoraStoreChainConfig.Apply(store, handler, enabled: true));
        Assert.False(SatoraStoreChainConfig.Apply(store, handler, enabled: true));
        Assert.True(JToken.DeepEquals(new JObject(), store.GetPaymentMethodConfig(SatoraChainPaymentMethod.Id)));
        Assert.True(SatoraStoreChainConfig.Apply(store, handler, enabled: false));
        Assert.False(SatoraStoreChainConfig.Apply(store, handler, enabled: false));

        Assert.Null(store.GetPaymentMethodConfig(SatoraChainPaymentMethod.Id));
        Assert.Equal("tpub", store.GetPaymentMethodConfig(onChain)?["accountDerivation"]?.ToString());
    }

    [Fact]
    public async Task RenewsAnActivePromptOnlyWhileItsSwapDoesNotTakeWhatIsDue()
    {
        var invoice = Invoice();
        var prompt = await Configure(new FakeSwapHandler(), invoice);
        Assert.False(SatoraChainPromptRenewer.NeedsRenewal(invoice));

        AddPayment(invoice, new PaymentEntity { Id = "lightning", Currency = "BTC", Value = 0.05m, Status = PaymentStatus.Settled, PaymentMethodId = PaymentTypes.LN.GetPaymentMethodId("BTC") });
        Assert.True(SatoraChainPromptRenewer.NeedsRenewal(invoice));

        invoice.Status = InvoiceStatus.Processing;
        Assert.False(SatoraChainPromptRenewer.NeedsRenewal(invoice));
        invoice.Status = InvoiceStatus.New;
        // A lazy prompt gets the current due when the customer picks it.
        prompt.Inactive = true;
        invoice.SetPaymentPrompt(SatoraChainPaymentMethod.Id, prompt);
        Assert.False(SatoraChainPromptRenewer.NeedsRenewal(invoice));
        Assert.False(SatoraChainPromptRenewer.NeedsRenewal(Invoice()));
        Assert.False(SatoraChainPromptRenewer.NeedsRenewal(null));
    }

    // The renewed swap takes what was left after an on-chain payment that a fee bump then replaced:
    // the invoice is due in full again, with no partial payment left on it.
    [Fact]
    public async Task RenewsAgainWhenAPartialPaymentIsTakenOffAfterARenewal()
    {
        var invoice = Invoice();
        var partial = new PaymentEntity { Id = "chain", Currency = "BTC", Value = 0.05m, Status = PaymentStatus.Processing, PaymentMethodId = PaymentTypes.CHAIN.GetPaymentMethodId("BTC") };
        AddPayment(invoice, partial);
        invoice.ExceptionStatus = InvoiceExceptionStatus.PaidPartial;
        var handler = new FakeSwapHandler();
        await Configure(handler, invoice);
        Assert.Equal([DueSats - 5_000_000], handler.Requested);
        Assert.False(SatoraChainPromptRenewer.NeedsRenewal(invoice));

        partial.Status = PaymentStatus.Unaccounted;
        invoice.UpdateTotals();
        // As InvoiceWatcher marks an invoice with nothing paid.
        invoice.ExceptionStatus = InvoiceExceptionStatus.None;

        Assert.True(SatoraChainPromptRenewer.NeedsRenewal(invoice));
    }

    // Known only on mainnet: 12 hours less the claim margin and the confirmation allowance.
    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public async Task OnMainnetAnInvoiceExpiringTooLateGetsNoSwap(int minutesFromTheLimit, bool offered)
    {
        var invoice = Invoice();
        invoice.ExpirationTime = DateTimeOffset.UtcNow + TimeSpan.FromHours(12) - TimeSpan.FromMinutes(5) - TimeSpan.FromHours(2) + TimeSpan.FromMinutes(minutesFromTheLimit);
        var handler = new FakeSwapHandler(Mainnet);

        if (offered)
            await Configure(handler, invoice);
        else
            await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Configure(handler, invoice));

        Assert.Equal(offered ? 1 : 0, handler.Requested.Count);
    }

    private static InvoiceEntity Invoice()
    {
        var invoice = new InvoiceEntity { Id = "invoice", Currency = "USD", Price = 4000, StoreId = "store", ExpirationTime = Now.AddMinutes(15) };
#pragma warning disable CS0618
        invoice.Payments = [];
        invoice.Rates["BTC"] = 34_000m;
#pragma warning restore CS0618
        invoice.UpdateTotals();
        return invoice;
    }

    private static async Task<PaymentPrompt> Configure(FakeSwapHandler handler, InvoiceEntity invoice)
    {
        var context = new PaymentMethodContext(new StoreData { Id = "store" }, new StoreBlob(), new JObject(), handler, invoice, new InvoiceLogs());
        await handler.BeforeFetchingRates(context);
        await handler.ConfigurePrompt(context);
        Assert.Equal([Htlc], context.TrackedDestinations);
        invoice.SetPaymentPrompt(SatoraChainPaymentMethod.Id, context.Prompt);
        return context.Prompt;
    }

    private static void AddPayment(InvoiceEntity invoice, PaymentData payment) => AddPayment(invoice, payment.GetBlob());

    private static void AddPayment(InvoiceEntity invoice, PaymentEntity payment)
    {
#pragma warning disable CS0618
        invoice.Payments.Add(payment);
#pragma warning restore CS0618
        invoice.UpdateTotals();
    }

    private static SwapDetails Created() => new(
        "swap",
        new SdkSwapStatus.Pending(),
        new SdkSwapDirection.BtcToArkade(),
        new SdkSwapFunding.BitcoinAddress(Htlc),
        (99_000 + Fee).ToString(),
        new SdkTokenId.Btc(),
        "tark1receive",
        "99000",
        new SdkTokenId.Btc(),
        "2027-01-15T08:00:00Z",
        1_800_000_000,
        1_800_043_200,
        null,
        null);

    // The swap creation, which needs the store's wallet and the swap server, answers as the server would.
    private sealed class FakeSwapHandler(BTCPayNetwork? network = null) : SatoraChainPaymentHandler(network ?? Btc, null!, null!)
    {
        public List<long> Requested { get; } = [];
        public List<DateTimeOffset> Expiries { get; } = [];
        public SatoraChainSwap? Swap { get; private set; }

        protected override Task<SatoraChainSwap> CreateSwapAsync(string storeId, string invoiceId, long sats, DateTimeOffset invoiceExpiresAt)
        {
            Requested.Add(sats);
            Expiries.Add(invoiceExpiresAt);
            Swap = SatoraChainActionsTests.Swap(swapId: "swap", storeId: storeId);
            Swap.InvoiceId = invoiceId;
            Swap.HtlcAddress = Htlc;
            Swap.ReceiveSats = sats;
            Swap.DepositSats = sats + Fee;
            return Task.FromResult(Swap);
        }
    }
}
