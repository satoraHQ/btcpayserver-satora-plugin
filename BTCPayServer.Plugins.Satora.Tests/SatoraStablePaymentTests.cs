using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using NBXplorer;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using Xunit;
using SdkSwapDirection = uniffi.satora_sdk_ffi.SwapDirection;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;
using SdkTokenId = uniffi.satora_sdk_ffi.TokenId;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Satora.Tests;

// The stablecoin prompts, the swap row behind each and the store's choice of methods, against BTCPay's
// own invoice creation and accounting.
public class SatoraStablePaymentTests
{
    internal static readonly BTCPayNetwork Btc = new()
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
    private static readonly SatoraStablecoin Usdc = SatoraStablePaymentMethod.UsdcArbitrum;
    private const string Deposit = "0x1111111111111111111111111111111111111111";
    private const long VhtlcLocktime = 1_800_000_000;
    // On regtest the last funding starts 1140 s before the VHTLC locktime: the 300 s claim margins of
    // the SDK and the plugin, 120 s for the funding to be seen, the shortest permit deadline and its margin.
    private static readonly DateTimeOffset RegtestFundingStartCutoff = DateTimeOffset.FromUnixTimeSeconds(VhtlcLocktime - 1140);
    private static readonly DateTimeOffset Expiry = RegtestFundingStartCutoff - TimeSpan.FromHours(1);

    // 4000 USD at 34000 USD/BTC is 0.11764706 BTC once BTCPay rounds it up to whole sats.
    private const long DueSats = 11_764_706;
    // What the fake quote's rate and fees ask for 99 000 sats: (99 000 * 1.0025 + 300) sats at 100 units each.
    private const long Units = 9_954_750;

    [Fact]
    public async Task ThePromptWaitsForTheCustomersPickWhenTheInvoiceIsCreated()
    {
        var handler = new SatoraStableFakes.Handler(Btc);
        var invoice = Invoice();

        var created = await Create(handler, invoice);

        Assert.Equal(PaymentMethodContext.ContextStatus.WaitingForActivation, created.Status);
        Assert.True(created.Prompt is { Inactive: true, Currency: "BTC", Divisibility: 8, PaymentMethodFee: 0m, Destination: null });
        Assert.Empty(handler.Requested);
    }

    // As InvoiceActivator activates or renews it: a new context on the invoice that stores the prompt.
    [Fact]
    public async Task APromptTheInvoiceStoresIsConfiguredForWhatIsDue()
    {
        var handler = new SatoraStableFakes.Handler(Btc);
        var invoice = Invoice();
        invoice.SetPaymentPrompt(Usdc.Id, (await Create(handler, invoice)).Prompt);

        var context = await Configure(handler, invoice);

        Assert.Equal([DueSats], handler.Requested);
        var swap = handler.Swaps.Single();
        Assert.True(context.Prompt is { Activated: true, Currency: "BTC", Divisibility: 8, PaymentMethodFee: 0m });
        Assert.Equal(swap.DepositAddress, context.Prompt.Destination);
        Assert.Equal([swap.DepositAddress], context.TrackedDestinations);
        var details = handler.ParsePaymentPromptDetails(context.Prompt.Details);
        Assert.Equal((swap.SwapId, 42161L, "USDC", Usdc.Contract, 6, swap.SourceUnits, DueSats, null),
            (details.SwapId, details.ChainId, details.Token, details.TokenAddress, details.Decimals, details.Units, details.ReceiveSats, details.ReceivedUnits));
        Assert.Equal(invoice.ExpirationTime, swap.InvoiceExpiresAt);
        // The swap pays exactly what is due; the customer's token amount carries its fees.
        invoice.SetPaymentPrompt(Usdc.Id, context.Prompt);
        Assert.Equal(DueSats / 100_000_000m, context.Prompt.Calculate().Due);
    }

    [Fact]
    public async Task ATopUpInvoiceHasNoAmountForASwap()
    {
        var handler = new SatoraStableFakes.Handler(Btc);
        var invoice = Invoice();
        invoice.Type = InvoiceType.TopUp;

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Configure(handler, invoice));
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task AnInvoiceGetsNoSwapOnceTheStoreStoppedOfferingTheMethod()
    {
        var handler = new SatoraStableFakes.Handler(Btc);

        await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Configure(handler, Invoice(), offered: false));
        Assert.Empty(handler.Requested);
    }

    // Known only on mainnet: 12 hours less the funding cutoff's 37 minutes and an hour for the payment to arrive.
    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public async Task OnMainnetAnInvoiceExpiringTooLateGetsNoSwap(int minutesFromTheLimit, bool offered)
    {
        var invoice = Invoice();
        invoice.ExpirationTime = DateTimeOffset.UtcNow + TimeSpan.FromHours(12) - TimeSpan.FromMinutes(37) - TimeSpan.FromHours(1) + TimeSpan.FromMinutes(minutesFromTheLimit);
        var handler = new SatoraStableFakes.Handler(Mainnet);

        if (offered)
            await Configure(handler, invoice);
        else
            await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Configure(handler, invoice));

        Assert.Equal(offered ? 1 : 0, handler.Requested.Count);
    }

    // Off mainnet the swap server's VHTLC window is known from the last swap it made: on the regtest
    // server, 1800 s, which leaves 10 minutes for the invoice and a minute for the payment to arrive.
    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public async Task OffMainnetAnInvoiceExpiringTooLateForTheLastSwapsWindowGetsNoSwap(int minutesFromTheLimit, bool offered)
    {
        var invoice = Invoice();
        invoice.ExpirationTime = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10 + minutesFromTheLimit);
        var handler = new SatoraStableFakes.Handler(Btc, swapTable: new SatoraStableFakes.SwapTable { LastSwap = (DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1800)) });

        if (offered)
            await Configure(handler, invoice);
        else
            Assert.Contains("too late for a payment to be funded", (await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Configure(handler, invoice))).Message);

        Assert.Equal(offered ? 1 : 0, handler.Requested.Count);
    }

    // A window seen long ago, as before the regtest server was restarted with a longer one, refuses
    // nothing: the swap made for the invoice decides, and shows the window again. On mainnet the
    // server's known 12 hours decide whatever a swap had.
    [Theory]
    [InlineData(false, 61)]
    [InlineData(true, 0)]
    public async Task AnInvoiceIsNotRefusedForAWindowSeenLongAgoOrOnMainnet(bool mainnet, int minutesAgo)
    {
        var invoice = Invoice();
        invoice.ExpirationTime = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
        var lastSwap = (DateTimeOffset.UtcNow - TimeSpan.FromMinutes(minutesAgo), TimeSpan.FromSeconds(1800));
        var handler = new SatoraStableFakes.Handler(mainnet ? Mainnet : Btc, swapTable: new SatoraStableFakes.SwapTable { LastSwap = lastSwap });

        await Configure(handler, invoice);

        Assert.Single(handler.Requested);
    }

    // Off mainnet the chain is a test fork, which the SDK's public nodes do not serve.
    [Fact]
    public async Task OffMainnetAMethodWithoutItsChainsNodeGetsNoSwap()
    {
        var handler = new SatoraStablePaymentHandler(Usdc, Btc, null!, new SatoraStableFakes.SwapTable(), null!, new ConfigurationBuilder().Build());

        var refused = await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => Configure(handler, Invoice()));

        Assert.Contains(SatoraNetworks.EvmRpcArbitrumKey, refused.Message);
    }

    [Theory]
    [InlineData("0.00000001", 1)]
    [InlineData("0.000000011", 2)]
    [InlineData("0.11764706", DueSats)]
    public void TheSwapIsAskedForTheDueInWholeSatsRoundedUp(string dueBtc, long sats) =>
        Assert.Equal(sats, SatoraChainPaymentHandler.DueSats(decimal.Parse(dueBtc, System.Globalization.CultureInfo.InvariantCulture)));

    // Worded as the Lightning and on-chain refusals of the same invoice are.
    [Theory]
    [InlineData(999, "Satora takes stablecoin payments of at least 1000 sats (0.00001000 BTC); this one is 999 sats.")]
    [InlineData(1_000, null)]
    [InlineData(100_000_000, null)]
    [InlineData(100_000_001, "Satora takes stablecoin payments of at most 100000000 sats (1.00000000 BTC); this one is 100000001 sats.")]
    public void OnlyAnAmountInTheRoutesRangeGetsASwap(long sats, string? refusal)
    {
        var quote = SatoraStableFakes.Quote(minAmount: 1_000, maxAmount: 100_000_000);

        if (refusal is null)
            SatoraStablePaymentHandler.CheckRange(sats, quote);
        else
            Assert.Equal(refusal, Assert.Throws<PaymentMethodUnavailableException>(() => SatoraStablePaymentHandler.CheckRange(sats, quote)).Message);
    }

    [Fact]
    public void RecordsTheSwapTheServerReturned()
    {
        var row = ToRow(Created(), out var refusal);

        Assert.Null(refusal);
        Assert.Equal(("swap", "store", "invoice", Usdc.Id.ToString(), 42161L, "USDC", Usdc.Contract, 6),
            (row.SwapId, row.StoreId, row.InvoiceId, row.PaymentMethodId, row.ChainId, row.Token, row.TokenAddress, row.Decimals));
        Assert.Equal((Deposit, Units.ToString(), 99_000L, SatoraSwapStatus.Pending), (row.DepositAddress, row.SourceUnits, row.ReceiveSats, row.Status));
        Assert.Equal((DateTimeOffset.FromUnixTimeSeconds(VhtlcLocktime), DateTimeOffset.FromUnixTimeSeconds(VhtlcLocktime + 43_200), RegtestFundingStartCutoff),
            (row.VhtlcRefundLocktime, row.EvmRefundLocktime, row.FundingStartCutoff));
        Assert.Equal((Expiry, Now, (DateTimeOffset?)null), (row.InvoiceExpiresAt, row.CreatedAt, row.CompletedAt));
    }

    public static TheoryData<string> Refused => ["token", "receive", "unparsable", "zero", "dear", "cheap"];

    // Nobody sees the address of a refused swap, so its row is closed from the start.
    [Theory]
    [MemberData(nameof(Refused))]
    public void ASwapThatMustNotBeShownIsRecordedClosedAndRefused(string flaw)
    {
        var created = flaw switch
        {
            "token" => Created() with { DepositToken = new SdkTokenId.Usdt0Arbitrum() },
            "receive" => Created() with { ReceiveAmount = "98999" },
            "unparsable" => Created() with { DepositAmount = "9 954 750" },
            "zero" => Created() with { DepositAmount = "0" },
            // More than 5% either side of what the quote asks.
            "dear" => Created() with { DepositAmount = ((long)(Units * 1.0501m)).ToString() },
            _ => Created() with { DepositAmount = ((long)(Units * 0.9499m)).ToString() }
        };

        var row = ToRow(created, out var refusal);

        Assert.IsType<InvalidOperationException>(refusal);
        Assert.Equal(Now, row.CompletedAt);
    }

    [Theory]
    [InlineData(0.951)]
    [InlineData(1.049)]
    public void AcceptsATokenAmountWithinFivePercentOfTheQuote(double ofQuote)
    {
        ToRow(Created() with { DepositAmount = ((long)(Units * (decimal)ofQuote)).ToString() }, out var refusal);

        Assert.Null(refusal);
    }

    // A payment sent as the invoice expires has a minute on regtest to arrive before the last funding starts.
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    public void ASwapWhoseFundingCutoffComesTooSoonAfterTheInvoiceExpiresIsRecordedClosedAndRefused(int secondsPastTheAllowance, bool refused)
    {
        var expiry = RegtestFundingStartCutoff - SatoraStableActions.TestDepositAllowance + TimeSpan.FromSeconds(secondsPastTheAllowance);

        var row = SatoraStablePaymentHandler.ToRow(Created(), Usdc, "store", "invoice", Network.RegTest, 99_000, SatoraStableFakes.Quote(), expiry, Now, out var refusal);

        Assert.Equal(refused, refusal is InvalidOperationException);
        Assert.Equal(refused ? Now : null, row.CompletedAt);
    }

    public static TheoryData<string> Unusable => ["direction", "funding", "address", "vhtlc", "evm"];

    [Theory]
    [MemberData(nameof(Unusable))]
    public void AResponseNobodyCouldPayIsRefusedBeforeAnythingIsRecorded(string flaw)
    {
        var created = flaw switch
        {
            "direction" => Created() with { Direction = new SdkSwapDirection.EvmToLightning() },
            "funding" => Created() with { Funding = new SdkSwapFunding.UserSubmitted() },
            "address" => Created() with { Funding = new SdkSwapFunding.Gasless("0x11111111111111111111111111111111111111") },
            "vhtlc" => Created() with { VhtlcRefundLocktime = null },
            _ => Created() with { EvmRefundLocktime = null }
        };

        Assert.Throws<InvalidOperationException>(() => ToRow(created, out _));
    }

    [Fact]
    public void TheStoreOffersExactlyTheMethodsTurnedOnAndKeepsItsOtherMethods()
    {
        var otherCoin = Usdc with { Id = new PaymentMethodId("BTC-SATORA-OTHER") };
        var handlers = new[] { Usdc, otherCoin }.Select(c => new SatoraStablePaymentHandler(c, Btc, null!, null!, null!, null!)).ToList();
        var store = new StoreData { Id = "store" };
        var onChain = PaymentTypes.CHAIN.GetPaymentMethodId("BTC");
        store.SetPaymentMethodConfig(onChain, JToken.Parse("""{"accountDerivation":"tpub"}"""));
        HashSet<PaymentMethodId> arbitrum = [Usdc.Id];

        Assert.True(SatoraStoreStableConfig.Apply(store, handlers, arbitrum));
        Assert.False(SatoraStoreStableConfig.Apply(store, handlers, arbitrum));
        Assert.True(JToken.DeepEquals(new JObject(), store.GetPaymentMethodConfig(Usdc.Id)));
        Assert.Null(store.GetPaymentMethodConfig(otherCoin.Id));
        Assert.True(SatoraStoreStableConfig.Apply(store, handlers, new HashSet<PaymentMethodId>()));
        Assert.False(SatoraStoreStableConfig.Apply(store, handlers, new HashSet<PaymentMethodId>()));

        Assert.Null(store.GetPaymentMethodConfig(Usdc.Id));
        Assert.Equal("tpub", store.GetPaymentMethodConfig(onChain)?["accountDerivation"]?.ToString());
    }

    [Fact]
    public void TheOwnersChoiceIsStoredByMethodIdAndAnIdThisVersionDoesNotOfferIsDropped()
    {
        Assert.Equal("BTC-SATORA-USDC-ARBITRUM", SatoraStablePaymentMethod.Format([Usdc]));
        Assert.Null(SatoraStablePaymentMethod.Format([]));
        Assert.Equal([Usdc], SatoraStablePaymentMethod.Parse("BTC-SATORA-USDC-POLYGON, BTC-SATORA-USDC-ARBITRUM"));
        Assert.Empty(SatoraStablePaymentMethod.Parse(null));
    }

    // A funding started at the cutoff with the shortest deadline, or earlier with a deadline that ends in
    // time, lands with room left for the server to fund the VHTLC and the wallet to claim it.
    [Fact]
    public void AFundingStartsOnlyWhileItCanLandBeforeTheClaimWindowCloses()
    {
        var vhtlc = DateTimeOffset.FromUnixTimeSeconds(VhtlcLocktime);
        var mainnet = SatoraStableActions.FundingStartCutoff(vhtlc, Network.Main);
        Assert.Equal(vhtlc - TimeSpan.FromMinutes(37), mainnet);
        Assert.Equal(RegtestFundingStartCutoff, SatoraStableActions.FundingStartCutoff(vhtlc, Network.RegTest));

        var swap = SatoraStableIntegrationTests.Swap("store", "deadline");
        swap.VhtlcRefundLocktime = vhtlc;
        swap.FundingStartCutoff = mainnet;
        var landBy = SatoraStableActions.LandingDeadline(vhtlc, Network.Main);
        Assert.Equal(swap.ClaimCutoff - SatoraClaimWindow.SafetyMargin - TimeSpan.FromMinutes(20), landBy);
        foreach (var before in new[] { TimeSpan.FromHours(10), TimeSpan.FromMinutes(25), TimeSpan.Zero })
        {
            var start = mainnet - before;
            var deadline = SatoraStableActions.PermitDeadline(swap, start);
            Assert.InRange(deadline, SatoraStableActions.MinPermitDeadline, SatoraStableActions.MaxPermitDeadline);
            Assert.True(start + deadline + SatoraStableActions.PermitExpiryMargin <= landBy);
        }
        Assert.Equal(SatoraStableActions.MaxPermitDeadline, SatoraStableActions.PermitDeadline(swap, mainnet - TimeSpan.FromHours(10)));
        Assert.Equal(SatoraStableActions.MinPermitDeadline, SatoraStableActions.PermitDeadline(swap, mainnet));
    }

    // A shortfall from the DEX is at most the swap's fees, here on a payment of 100 000 sats.
    [Theory]
    [InlineData(0.0025, 300, 1)]
    [InlineData(0.003, 700, 1)]
    [InlineData(0.003, 701, 1.1)]
    [InlineData(0.013, 2000, 3.3)]
    public void TheToleranceFloorIsTheSwapsFeesOnATypicalPaymentAndAtLeastOnePercent(double feeRate, long networkFee, double floor) =>
        Assert.Equal((decimal)floor, SatoraStableActions.ToleranceFloorPercent(SatoraStableFakes.Quote(protocolFeeRate: feeRate, networkFee: (ulong)networkFee)));

    [Theory]
    [InlineData(" http://anvil-arbitrum:8545 ", "http://anvil-arbitrum:8545")]
    [InlineData(" ", null)]
    public void TheChainsNodeComesFromItsOwnKey(string value, string? url)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [SatoraNetworks.EvmRpcArbitrumKey] = value
        }).Build();

        Assert.Equal(url, SatoraNetworks.EvmRpcUrl(configuration, Usdc.ChainId));
        Assert.Null(SatoraNetworks.EvmRpcUrl(configuration, 1));
        Assert.False(SatoraNetworks.HasEndpointOverrides(configuration));
    }

    [Fact]
    public async Task EachCoinsQuoteIsReadAtMostOnceAMinute()
    {
        var quotes = new ClockedQuotes { Now = Now };
        var otherCoin = Usdc with { Id = new PaymentMethodId("BTC-SATORA-OTHER") };

        await quotes.GetAsync("store", Usdc, CancellationToken.None);
        await quotes.GetAsync("other-store", Usdc, CancellationToken.None);
        await quotes.GetAsync("store", otherCoin, CancellationToken.None);
        quotes.Now += SatoraStableQuotes.MaxAge - TimeSpan.FromSeconds(1);
        await quotes.GetAsync("store", Usdc, CancellationToken.None);
        Assert.Equal([Usdc, otherCoin], quotes.Fetches);

        quotes.Now += TimeSpan.FromSeconds(1);
        await quotes.GetAsync("store", Usdc, CancellationToken.None);
        Assert.Equal([Usdc, otherCoin, Usdc], quotes.Fetches);
    }

    [Fact]
    public async Task TheLastQuoteReadIsKeptPastItsAgeForThePageThatAsksNothing()
    {
        var quotes = new ClockedQuotes { Now = Now };
        Assert.Null(quotes.LastRead(Usdc));

        await quotes.GetAsync("store", Usdc, CancellationToken.None);
        quotes.Now += SatoraStableQuotes.MaxAge * 10;

        Assert.Equal(SatoraStableFakes.Quote(), quotes.LastRead(Usdc));
        Assert.Equal([Usdc], quotes.Fetches);
    }

    private sealed class ClockedQuotes() : SatoraStableQuotes(null!)
    {
        public DateTimeOffset Now { get; set; }

        public List<SatoraStablecoin> Fetches { get; } = [];

        protected override DateTimeOffset UtcNow => Now;

        protected override Task<Quote> FetchAsync(string storeId, SatoraStablecoin coin, CancellationToken cancellationToken)
        {
            Fetches.Add(coin);
            return Task.FromResult(SatoraStableFakes.Quote());
        }
    }

    internal static InvoiceEntity Invoice()
    {
        var invoice = new InvoiceEntity { Id = "invoice", Currency = "USD", Price = 4000, StoreId = "store", ExpirationTime = Now.AddMinutes(15) };
#pragma warning disable CS0618
        invoice.Payments = [];
        invoice.Rates["BTC"] = 34_000m;
#pragma warning restore CS0618
        invoice.UpdateTotals();
        return invoice;
    }

    // As UIInvoiceController creates an invoice, with the store's lazy payment methods off.
    private static async Task<PaymentMethodContext> Create(IPaymentMethodHandler handler, InvoiceEntity invoice)
    {
        var store = new StoreData { Id = "store" };
        store.SetPaymentMethodConfig(handler, new SatoraStablePaymentMethodConfig());
        var creation = new InvoiceCreationContext(store, store.GetStoreBlob(), invoice, new InvoiceLogs(), new PaymentMethodHandlerDictionary([handler]), null);
        creation.SetLazyActivation(false);
        await creation.BeforeFetchingRates();
        await creation.CreatePaymentPrompts();
        return creation.PaymentMethodContexts[handler.PaymentMethodId];
    }

    // Without the store's config, as for a method the store stopped offering after the invoice was made.
    internal static async Task<PaymentMethodContext> Configure(IPaymentMethodHandler handler, InvoiceEntity invoice, bool offered = true)
    {
        var context = new PaymentMethodContext(new StoreData { Id = "store" }, new StoreBlob(), offered ? new JObject() : null!, handler, invoice, new InvoiceLogs());
        await handler.BeforeFetchingRates(context);
        await handler.ConfigurePrompt(context);
        return context;
    }

    private static SatoraStableSwap ToRow(SwapDetails created, out Exception? refusal) =>
        SatoraStablePaymentHandler.ToRow(created, Usdc, "store", "invoice", Network.RegTest, 99_000, SatoraStableFakes.Quote(), Expiry, Now, out refusal);

    private static SwapDetails Created() => new(
        "swap",
        new SdkSwapStatus.Pending(),
        new SdkSwapDirection.EvmToArkade(),
        new SdkSwapFunding.Gasless(Deposit),
        Units.ToString(),
        new SdkTokenId.UsdcArbitrum(),
        "tark1receive",
        "99000",
        new SdkTokenId.Btc(),
        "2027-01-15T08:00:00Z",
        VhtlcLocktime,
        null,
        VhtlcLocktime + 43_200,
        null);
}
