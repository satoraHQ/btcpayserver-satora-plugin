using System.Collections.Concurrent;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against a real Postgres: set SATORA_TEST_POSTGRES as for SatoraWalletIntegrationTests. The
// fixture's database starts as master left it and takes every later migration, this table's included.
public class SatoraStableIntegrationTests(SatoraWalletIntegrationTests.Database db)
    : IClassFixture<SatoraWalletIntegrationTests.Database>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    private SatoraStableSwaps Swaps => new(db.Contexts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task KeepsOneSwapPerDepositAddressInAnyCase()
    {
        await Swaps.AddAsync(Swap("unique", "first", deposit: "0xAbCd000000000000000000000000000000000001"));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() =>
            Swaps.AddAsync(Swap("unique", "second", deposit: "0xabcd000000000000000000000000000000000001")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
    }

    // What the payment handler judges an invoice's expiry by before it makes a swap. A refused swap,
    // recorded closed, shows the window as well as any; one made before the time asked about does not.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ReadsTheVhtlcWindowOfTheLastSwapMadeForTheMethodSinceAGivenTime()
    {
        const string method = "BTC-SATORA-WINDOW-TEST";
        Assert.Null(await Swaps.LastVhtlcWindowAsync(method, Now.AddHours(-1)));
        var earlier = Swap("window", "earlier", createdAt: Now.AddMinutes(-10));
        earlier.PaymentMethodId = method;
        var last = Swap("window", "last");
        (last.PaymentMethodId, last.VhtlcRefundLocktime, last.CompletedAt) = (method, Now.AddSeconds(1800), Now);
        await Swaps.AddAsync(earlier);
        await Swaps.AddAsync(last);

        Assert.Equal(TimeSpan.FromSeconds(1800), await Swaps.LastVhtlcWindowAsync(method, Now.AddHours(-1)));
        Assert.Null(await Swaps.LastVhtlcWindowAsync(method, Now.AddSeconds(1)));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWorkerReadsOpenSwapsIncludingThoseWaitingForTheOwner()
    {
        var open = Swap("worker", "open");
        var sweep = Swap("worker", "sweep");
        var done = Swap("worker", "done");
        foreach (var swap in new[] { open, sweep, done, Swap("worker-other", "other") })
            await Swaps.AddAsync(swap);
        await Swaps.SetNeedsAttentionAsync(sweep.SwapId, null, SatoraStableAttention.Sweep, Now);
        await Swaps.SetCompletedAsync(done.SwapId, Now);

        Assert.Contains("worker", await Swaps.ListOpenStoresAsync(Ct));
        Assert.Equal([open.SwapId, sweep.SwapId], (await Swaps.ListOpenAsync("worker", Ct)).Select(s => s.SwapId).Order());
        var waiting = Assert.Single(await Swaps.ListNeedingAttentionAsync("worker"));
        Assert.Equal((sweep.SwapId, SatoraStableAttention.Sweep), (waiting.SwapId, waiting.AttentionReason));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task KeepsWhatTheWorkerWritesAndTheStatusAndReasonByName()
    {
        var swap = Swap("writes", "swap");
        await Swaps.AddAsync(swap);

        await Swaps.SetStatusAsync(swap.SwapId, SatoraSwapStatus.ClientFundingSeen);
        await Swaps.SetPaymentRecordedAsync(swap.SwapId, true);
        await Swaps.SetPaymentRecordedAsync(swap.SwapId, false);
        Assert.False((await Swaps.FindAsync("writes", swap.SwapId))!.PaymentRecorded);
        Assert.True(await Swaps.SetFundedAsync(swap.SwapId, "0xfund"));
        Assert.True(await Swaps.SetClaimedAsync(swap.SwapId, "ark", 99_100));
        await Swaps.SetCreditedAsync(swap.SwapId);
        Assert.True(await Swaps.SetNeedsAttentionAsync(swap.SwapId, "ark", SatoraStableAttention.Sweep, Now));

        var read = await Swaps.FindAsync("writes", swap.SwapId);
        Assert.True(read is
        {
            Status: SatoraSwapStatus.ClientFundingSeen, FundTxHash: "0xfund", ClaimTxId: "ark", VhtlcSats: 99_100,
            PaymentRecorded: true, Credited: true, AttentionReason: SatoraStableAttention.Sweep, CompletedAt: null
        });
        Assert.Equal((swap.SourceUnits, swap.ChainId, swap.Decimals, swap.FundingStartCutoff, swap.EvmRefundLocktime),
            (read.SourceUnits, read.ChainId, read.Decimals, read.FundingStartCutoff, read.EvmRefundLocktime));
        Assert.Null(await Swaps.FindAsync("writes-other", swap.SwapId));
        await using var ctx = db.Contexts.CreateContext();
        var stored = await ctx.Database.SqlQuery<string>(
            $"""SELECT "Status" || '/' || "AttentionReason" AS "Value" FROM "BTCPayServer.Plugins.Satora"."SatoraStableSwaps" WHERE "SwapId" = {swap.SwapId}""").SingleAsync(Ct);
        Assert.Equal("ClientFundingSeen/Sweep", stored);
    }

    // The flag says only that the deposit may have moved; the SDK's journal decides whether to fund.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AFundingAttemptIsMarkedOnlyBeforeTheCutoffOfAnOpenSwapAndKeepsTheFirstTime()
    {
        var open = Swap("fund", "open");
        var sweep = Swap("fund", "sweep");
        var closed = Swap("fund", "closed");
        foreach (var swap in new[] { open, sweep, closed })
            await Swaps.AddAsync(swap);
        await Swaps.SetNeedsAttentionAsync(sweep.SwapId, null, SatoraStableAttention.Sweep, Now);
        await Swaps.SetClosedAsync(closed.SwapId, "Empty", Now);

        Assert.True(await Swaps.MarkFundingAttemptedAsync(open.SwapId, Now));
        Assert.True(await Swaps.MarkFundingAttemptedAsync(open.SwapId, Now.AddMinutes(1)));
        Assert.True(await Swaps.MarkFundingAttemptedAsync(open.SwapId, open.FundingStartCutoff));
        Assert.False(await Swaps.MarkFundingAttemptedAsync(open.SwapId, open.FundingStartCutoff.AddSeconds(1)));
        Assert.False(await Swaps.MarkFundingAttemptedAsync(sweep.SwapId, Now));
        Assert.False(await Swaps.MarkFundingAttemptedAsync(closed.SwapId, Now));

        Assert.Equal(Now, (await Swaps.FindAsync("fund", open.SwapId))!.FundingAttemptedAt);
        Assert.Null((await Swaps.FindAsync("fund", sweep.SwapId))!.FundingAttemptedAt);
        Assert.Null((await Swaps.FindAsync("fund", closed.SwapId))!.FundingAttemptedAt);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheFundingAndTheClaimAreRecordedOnce()
    {
        var swap = Swap("once", "swap");
        await Swaps.AddAsync(swap);

        Assert.True(await Swaps.SetFundedAsync(swap.SwapId, "0xfirst"));
        Assert.False(await Swaps.SetFundedAsync(swap.SwapId, "0xsecond"));
        Assert.True(await Swaps.SetClaimedAsync(swap.SwapId, "ark-first", 99_000));
        Assert.False(await Swaps.SetClaimedAsync(swap.SwapId, "ark-second", 98_000));

        Assert.True(await Swaps.FindAsync("once", swap.SwapId) is { FundTxHash: "0xfirst", ClaimTxId: "ark-first", VhtlcSats: 99_000 });
    }

    // Only from the row the worker read: open, not yet waiting for the owner, with the same claim.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ASwapGoesToTheOwnerOnlyAsTheWorkerReadIt()
    {
        var unclaimed = Swap("attention", "unclaimed");
        var claimed = Swap("attention", "claimed");
        var done = Swap("attention", "done");
        foreach (var swap in new[] { unclaimed, claimed, done })
            await Swaps.AddAsync(swap);
        await Swaps.SetClaimedAsync(claimed.SwapId, "ark", 99_000);
        await Swaps.SetCompletedAsync(done.SwapId, Now);

        Assert.True(await Swaps.SetNeedsAttentionAsync(unclaimed.SwapId, null, SatoraStableAttention.Sweep, Now));
        Assert.False(await Swaps.SetNeedsAttentionAsync(unclaimed.SwapId, null, SatoraStableAttention.HtlcRefund, Now.AddMinutes(1)));
        Assert.False(await Swaps.SetNeedsAttentionAsync(claimed.SwapId, null, SatoraStableAttention.Sweep, Now));
        Assert.True(await Swaps.SetNeedsAttentionAsync(claimed.SwapId, "ark", SatoraStableAttention.Sweep, Now));
        Assert.False(await Swaps.SetNeedsAttentionAsync(done.SwapId, null, SatoraStableAttention.Sweep, Now));

        Assert.True(await Swaps.FindAsync("attention", unclaimed.SwapId) is { AttentionReason: SatoraStableAttention.Sweep, NeedsAttentionAt: not null });
        Assert.True(await Swaps.FindAsync("attention", claimed.SwapId) is { AttentionReason: SatoraStableAttention.Sweep, ClaimTxId: "ark" });
        Assert.True(await Swaps.FindAsync("attention", done.SwapId) is { NeedsAttentionAt: null, AttentionReason: null });
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheSweepCanChangeOnlyUntilTheWorkerStartsIt()
    {
        var swap = Swap("sweep", "swap");
        var refund = Swap("sweep", "refund");
        await Swaps.AddAsync(swap);
        await Swaps.AddAsync(refund);
        await Swaps.SetNeedsAttentionAsync(refund.SwapId, null, SatoraStableAttention.HtlcRefund, Now);

        Assert.False(await Swaps.RequestSweepAsync("sweep", swap.SwapId, "0xfirst", null));
        Assert.False(await Swaps.RequestSweepAsync("sweep", refund.SwapId, "0xfirst", null));
        await Swaps.SetNeedsAttentionAsync(swap.SwapId, null, SatoraStableAttention.Sweep, Now);
        Assert.False(await Swaps.RequestSweepAsync("sweep-other", swap.SwapId, "0xfirst", null));
        Assert.True(await Swaps.RequestSweepAsync("sweep", swap.SwapId, "0xfirst", null));
        Assert.True(await Swaps.RequestSweepAsync("sweep", swap.SwapId, "0xsecond", "0xtoken"));

        Assert.False(await Swaps.TryStartSweepAsync(swap.SwapId, "0xfirst", null));
        Assert.False(await Swaps.TryStartSweepAsync(swap.SwapId, "0xsecond", null));
        Assert.True(await Swaps.TryStartSweepAsync(swap.SwapId, "0xsecond", "0xtoken"));
        Assert.False(await Swaps.RequestSweepAsync("sweep", swap.SwapId, "0xthird", null));
        Assert.True(await Swaps.TryStartSweepAsync(swap.SwapId, "0xsecond", "0xtoken"));

        await Swaps.SetSweepTxHashAsync(swap.SwapId, "0xsweep");
        Assert.False(await Swaps.TryStartSweepAsync(swap.SwapId, "0xsecond", "0xtoken"));
        Assert.Single(await Swaps.ListNeedingAttentionAsync("sweep"), s => s.SwapId == swap.SwapId);
        await Swaps.SetCompletedAsync(swap.SwapId, Now);
        Assert.True(await Swaps.FindAsync("sweep", swap.SwapId) is
        {
            SweepDestination: "0xsecond", SweepToken: "0xtoken", SweepAttempted: true, SweepTxHash: "0xsweep", CompletedAt: not null
        });
        Assert.False(await Swaps.TryStartSweepAsync(swap.SwapId, "0xsecond", "0xtoken"));

        // A null token sweeps the swap's own.
        var ownToken = Swap("sweep", "own-token");
        await Swaps.AddAsync(ownToken);
        await Swaps.SetNeedsAttentionAsync(ownToken.SwapId, null, SatoraStableAttention.Sweep, Now);
        Assert.True(await Swaps.RequestSweepAsync("sweep", ownToken.SwapId, "0xdestination", null));
        Assert.False(await Swaps.TryStartSweepAsync(ownToken.SwapId, "0xdestination", "0xtoken"));
        Assert.True(await Swaps.TryStartSweepAsync(ownToken.SwapId, "0xdestination", null));
        Assert.True(await Swaps.FindAsync("sweep", ownToken.SwapId) is { SweepDestination: "0xdestination", SweepToken: null, SweepAttempted: true });
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWorkerClosesOnlyASwapStillOpen()
    {
        var empty = Swap("close", "empty");
        var done = Swap("close", "done");
        await Swaps.AddAsync(empty);
        await Swaps.AddAsync(done);

        Assert.True(await Swaps.SetClosedAsync(empty.SwapId, "Empty", Now));
        Assert.False(await Swaps.SetClosedAsync(empty.SwapId, "Abandoned", Now.AddMinutes(1)));
        Assert.False(await Swaps.SetCompletedAsync(empty.SwapId, Now.AddMinutes(1)));
        Assert.True(await Swaps.SetCompletedAsync(done.SwapId, Now));
        Assert.False(await Swaps.SetClosedAsync(done.SwapId, "Empty", Now.AddMinutes(1)));

        Assert.True(await Swaps.FindAsync("close", empty.SwapId) is { ClosedReason: "Empty" } e && e.CompletedAt == Now);
        Assert.True(await Swaps.FindAsync("close", done.SwapId) is { ClosedReason: null } d && d.CompletedAt == Now);
    }

    // A replace would strand them: only the store's current seed can fund, claim or sweep its swaps.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task CountsOpenStablecoinSwapsOfTheCurrentWalletAsNeedingTheSeed()
    {
        var (_, wallets) = db.Wallets();
        var (refusal, phrase) = await wallets.GenerateAsync("count-stable", null);
        Assert.Equal(SeedWriteRefusal.None, refusal);
        Assert.True(await wallets.ConfirmBackupAsync("count-stable", phrase));
        await Swaps.AddAsync(Swap("count-stable", "before", createdAt: Now.AddDays(-1)));
        var closed = Swap("count-stable", "closed", createdAt: DateTimeOffset.UtcNow);
        await Swaps.AddAsync(closed);
        await Swaps.SetClosedAsync(closed.SwapId, "Empty", Now);
        await Swaps.AddAsync(Swap("count-stable-other", "other"));
        Assert.Equal(0, await wallets.CountOpenOperationsAsync("count-stable"));

        var sweep = Swap("count-stable", "sweep", createdAt: DateTimeOffset.UtcNow);
        await Swaps.AddAsync(sweep);
        await Swaps.SetNeedsAttentionAsync(sweep.SwapId, null, SatoraStableAttention.Sweep, Now);

        Assert.Equal(1, await wallets.CountOpenOperationsAsync("count-stable"));
        Assert.Equal(SeedWriteRefusal.OpenOperations, (await wallets.GenerateAsync("count-stable", SeedWriteGuard.ConfirmationWord)).Refusal);
        Assert.Equal("stablecoin deposits to send back for invoices: invoice-sweep", await wallets.DescribeOpenOperationsAsync("count-stable"));
    }

    // The handler would make a swap through the store's wallet: no quote is read and no row written while
    // the wallet is not set up, or set up but disabled.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheHandlerAsksNothingOfTheSwapServerWhileTheWalletIsDisabled()
    {
        var (registry, wallets) = db.Wallets();
        var quotes = new SatoraStableFakes.Quotes();
        var handler = new SatoraStablePaymentHandler(SatoraStablePaymentMethod.UsdcArbitrum, SatoraStablePaymentTests.Btc, registry, Swaps, quotes, EvmRpc);

        await Refusal(handler, "stable-off");
        var (_, phrase) = await wallets.GenerateAsync("stable-off", null);
        Assert.True(await wallets.ConfirmBackupAsync("stable-off", phrase));
        await Refusal(handler, "stable-off");

        Assert.Empty(quotes.Reads);
        Assert.False(await HasRowsAsync("stable-off"));
    }

    // BTCPay settles against the invoice's own tolerance: the owner can lower the store's after turning
    // the coin on, and a Greenfield request can set an invoice's below the store's.
    [Theory(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    [InlineData(0.99, 0.99)]
    [InlineData(5, 0.99)]
    public async Task TheHandlerMakesNoSwapWhileTheInvoicesPaymentToleranceIsBelowTheFloor(double storeTolerance, double invoiceTolerance)
    {
        var storeId = $"stable-tolerance-{(int)storeTolerance}";
        var quotes = new SatoraStableFakes.Quotes();
        var handler = await EnabledWalletHandlerAsync(storeId, quotes);

        var refused = await Refusal(handler, storeId, invoiceTolerance, storeTolerance);

        Assert.Contains("invoice's payment tolerance of 0.99% is below the 1%", refused.Message);
        Assert.Equal([SatoraStablePaymentMethod.UsdcArbitrum], quotes.Reads);
        Assert.False(await HasRowsAsync(storeId));
    }

    // The invoice is due 11 764 706 sats, below this route's minimum. Its own tolerance covers the swap's
    // fees, so the store's, lowered since, does not refuse it first.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheHandlerMakesNoSwapForAnAmountOutsideTheRoutesRange()
    {
        var quotes = new SatoraStableFakes.Quotes { Quote = SatoraStableFakes.Quote(minAmount: 20_000_000) };
        var handler = await EnabledWalletHandlerAsync("stable-range", quotes);

        var refused = await Refusal(handler, "stable-range", invoiceTolerance: 5);

        Assert.Contains("Satora takes stablecoin payments of at least 20000000 sats (0.20000000 BTC); this one is 11764706 sats.", refused.Message);
        Assert.Equal([SatoraStablePaymentMethod.UsdcArbitrum], quotes.Reads);
        Assert.False(await HasRowsAsync("stable-range"));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheStoreOffersTheOwnersStablecoinsWhileTheWalletIsEnabled()
    {
        var (_, wallets) = db.Wallets();
        Assert.False(await wallets.SetStablecoinsAsync("stable-choice", [SatoraStablePaymentMethod.UsdcArbitrum]));
        var (_, phrase) = await wallets.GenerateAsync("stable-choice", null);
        Assert.True(await wallets.ConfirmBackupAsync("stable-choice", phrase));

        Assert.True(await wallets.SetStablecoinsAsync("stable-choice", [SatoraStablePaymentMethod.UsdcArbitrum]));
        Assert.True(await wallets.SetEnabledAsync("stable-choice", true));
        Assert.True(await wallets.SetEnabledAsync("stable-choice", false));

        Assert.Equal([SatoraStablePaymentMethod.UsdcArbitrum], await wallets.GetStablecoinsAsync("stable-choice"));
        Assert.Equal("BTC-SATORA-USDC-ARBITRUM", (await db.Settings("stable-choice")).StablecoinMethods);
        // A new wallet starts disabled with nothing chosen.
        Assert.Equal(["off:", "off:BTC-SATORA-USDC-ARBITRUM", "on:BTC-SATORA-USDC-ARBITRUM", "off:BTC-SATORA-USDC-ARBITRUM"],
            db.StableConfig.Calls("stable-choice"));
        Assert.True(await wallets.SetStablecoinsAsync("stable-choice", []));
        Assert.Empty(await wallets.GetStablecoinsAsync("stable-choice"));
    }

    private static readonly IConfiguration EvmRpc = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        [SatoraNetworks.EvmRpcArbitrumKey] = "http://127.0.0.1:1"
    }).Build();

    // Enabling needs no Arkade server; making a swap would.
    private async Task<SatoraStablePaymentHandler> EnabledWalletHandlerAsync(string storeId, SatoraStableQuotes quotes)
    {
        var (registry, wallets) = db.Wallets();
        var (_, phrase) = await wallets.GenerateAsync(storeId, null);
        Assert.True(await wallets.ConfirmBackupAsync(storeId, phrase));
        Assert.True(await wallets.SetEnabledAsync(storeId, true));
        return new SatoraStablePaymentHandler(SatoraStablePaymentMethod.UsdcArbitrum, SatoraStablePaymentTests.Btc, registry, Swaps, quotes, EvmRpc);
    }

    private static async Task<PaymentMethodUnavailableException> Refusal(IPaymentMethodHandler handler, string storeId, double invoiceTolerance = 0,
        double storeTolerance = 0)
    {
        var invoice = SatoraStablePaymentTests.Invoice();
        invoice.PaymentTolerance = invoiceTolerance;
        var context = new PaymentMethodContext(new StoreData { Id = storeId }, new StoreBlob { PaymentTolerance = storeTolerance }, new JObject(), handler,
            invoice, new InvoiceLogs());
        await handler.BeforeFetchingRates(context);
        return await Assert.ThrowsAsync<PaymentMethodUnavailableException>(() => handler.ConfigurePrompt(context));
    }

    private async Task<bool> HasRowsAsync(string storeId)
    {
        await using var ctx = db.Contexts.CreateContext();
        return await ctx.SatoraStableSwaps.AnyAsync(s => s.StoreId == storeId, Ct);
    }

    internal static SatoraStableSwap Swap(string storeId, string name, string? deposit = null, DateTimeOffset? createdAt = null) => new()
    {
        SwapId = $"{storeId}-{name}",
        StoreId = storeId,
        InvoiceId = "invoice-" + name,
        PaymentMethodId = "BTC-SATORA-USDC-ARBITRUM",
        ChainId = 42161,
        Token = "USDC",
        TokenAddress = "0xaf88d065e77c8cc2239327c5edb3a432268e5831",
        Decimals = 6,
        DepositAddress = deposit ?? $"0x{storeId}-{name}",
        SourceUnits = "10130000",
        ReceiveSats = 99_500,
        Status = SatoraSwapStatus.Pending,
        VhtlcRefundLocktime = Now.AddHours(12),
        EvmRefundLocktime = Now.AddHours(24),
        FundingStartCutoff = Now.AddHours(11),
        InvoiceExpiresAt = Now.AddMinutes(15),
        CreatedAt = createdAt ?? Now
    };
}

// BTCPay's own activation and renewal of a stablecoin prompt, through its InvoiceRepository and
// InvoiceActivator, opt-in as above.
public class SatoraStablePromptIntegrationTests(SatoraChainPromptIntegrationTests.BtcPayDatabase btcpay)
    : IClassFixture<SatoraChainPromptIntegrationTests.BtcPayDatabase>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    // The checkout activates the prompt the customer picks, and a renewal forces a new one; both must
    // reach ConfigurePrompt, which a prompt marked inactive again would never do.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task BtcpaysActivatorConfiguresTheWaitingPromptAndRenewsIt()
    {
        var events = new EventAggregator(new Logs());
        var invoices = new InvoiceRepository(btcpay.Contexts, events);
        var handler = new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc);
        var handlers = new PaymentMethodHandlerDictionary([handler]);
        StoreData store;
        await using (var ctx = btcpay.Contexts.CreateContext())
        {
            store = await ctx.Stores.SingleAsync(s => s.Id == "store", Ct);
            store.SetPaymentMethodConfig(handler, new SatoraStablePaymentMethodConfig());
            await ctx.SaveChangesAsync(Ct);
        }
        var invoice = SatoraStablePaymentTests.Invoice();
        invoice.Id = "invoice-stable";
        invoice.InvoiceTime = DateTimeOffset.UtcNow;
        invoice.MonitoringExpiration = invoice.ExpirationTime.AddHours(1);
        // As UIInvoiceController creates an invoice, with the store's lazy payment methods off.
        var creation = new InvoiceCreationContext(store, store.GetStoreBlob(), invoice, new InvoiceLogs(), handlers, null);
        creation.SetLazyActivation(false);
        await creation.BeforeFetchingRates();
        await creation.CreatePaymentPrompts();
        var created = creation.PaymentMethodContexts[handler.PaymentMethodId];
        Assert.Equal(PaymentMethodContext.ContextStatus.WaitingForActivation, created.Status);
        invoice.SetPaymentPrompts(new PaymentPromptDictionary([created.Prompt]));
        await using (var ctx = btcpay.Contexts.CreateContext())
        {
            var data = new InvoiceData { Id = invoice.Id, StoreDataId = "store", Status = "New" };
            data.SetBlob(invoice);
            ctx.Invoices.Add(data);
            await ctx.SaveChangesAsync(Ct);
        }
        var activator = new InvoiceActivator(invoices, events, null!, handlers,
            new StoreRepository(btcpay.Contexts, new JsonSerializerSettings(), events, null!), new WalletRepository(btcpay.Contexts));

        Assert.True(await activator.ActivateInvoicePaymentMethod(invoice.Id, handler.PaymentMethodId));
        var activated = (await invoices.GetInvoice(invoice.Id)).GetPaymentPrompt(handler.PaymentMethodId)!;
        Assert.True(activated.Activated);
        Assert.Equal(handler.Swaps[0].DepositAddress, activated.Destination);
        Assert.Equal([11_764_706L], handler.Requested);

        Assert.True(await activator.ActivateInvoicePaymentMethod(invoice.Id, handler.PaymentMethodId, forceNew: true));
        var renewed = (await invoices.GetInvoice(invoice.Id)).GetPaymentPrompt(handler.PaymentMethodId)!;
        Assert.True(renewed.Activated);
        Assert.Equal(handler.Swaps[1].DepositAddress, renewed.Destination);
        Assert.Equal(handler.Swaps[1].SwapId, handler.ParsePaymentPromptDetails(renewed.Details).SwapId);
        Assert.Equal(2, handler.Requested.Count);
    }

    // BTCPay's payments table keys a payment by its id and method, so a second write of the funding,
    // after a restart or a lost answer, finds the first instead of paying the invoice twice.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task RecordsTheFundingOnceAndSettlesItWithWhatTheVhtlcHeld()
    {
        var (credit, invoices) = Credit();
        var swap = await AddInvoiceAsync("credited");

        Assert.Equal(SatoraCreditOutcome.Recorded, await credit.RecordAsync(swap, PaymentStatus.Processing, DateTimeOffset.UtcNow));
        Assert.Equal(SatoraCreditOutcome.AlreadyRecorded, await credit.RecordAsync(swap, PaymentStatus.Processing, DateTimeOffset.UtcNow));
        var funded = Assert.Single((await invoices.GetInvoice(swap.InvoiceId)).GetPayments(false));
        Assert.Equal((PaymentStatus.Processing, 99_500 / 100_000_000m), (funded.Status, funded.Value));

        swap.ClaimTxId = "ark-credited";
        swap.VhtlcSats = 99_400;
        Assert.Equal(SatoraCreditOutcome.Recorded, await credit.RecordAsync(swap, PaymentStatus.Settled, DateTimeOffset.UtcNow));
        Assert.Equal(SatoraCreditOutcome.AlreadyRecorded, await credit.RecordAsync(swap, PaymentStatus.Settled, DateTimeOffset.UtcNow));

        var settled = Assert.Single((await invoices.GetInvoice(swap.InvoiceId)).GetPayments(false));
        Assert.Equal((PaymentStatus.Settled, 99_400 / 100_000_000m, "ark-credited"),
            (settled.Status, settled.Value, new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc).ParsePaymentDetails(settled.Details).ClaimTxId));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AFundingTakenOffComesBackWhenRecordedAgain()
    {
        var (credit, invoices) = Credit();
        var swap = await AddInvoiceAsync("unaccounted");
        await credit.RecordAsync(swap, PaymentStatus.Processing, DateTimeOffset.UtcNow);

        await credit.UnaccountAsync(swap);
        Assert.Equal(PaymentStatus.Unaccounted, Assert.Single((await invoices.GetInvoice(swap.InvoiceId)).GetPayments(false)).Status);
        await credit.RecordAsync(swap, PaymentStatus.Processing, DateTimeOffset.UtcNow);

        Assert.Equal(PaymentStatus.Processing, Assert.Single((await invoices.GetInvoice(swap.InvoiceId)).GetPayments(false)).Status);
    }

    // An open checkout fetches the prompt again only on InvoiceNewPaymentDetailsEvent.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheDepositProgressReachesThePromptOnlyWhenItChangesAndAnOpenCheckoutIsTold()
    {
        var events = new EventAggregator(new Logs());
        var published = new List<InvoiceNewPaymentDetailsEvent>();
        using var subscription = events.Subscribe<InvoiceNewPaymentDetailsEvent>(published.Add);
        var (credit, invoices) = Credit(events);
        var swap = await AddInvoiceAsync("progress");
        var method = SatoraStablePaymentMethod.UsdcArbitrum.Id;
        SatoraStablePromptDetails Details() => SatoraStablePromptDetails.Of(invoices.GetInvoice(swap.InvoiceId).Result.GetPaymentPrompt(method))!;

        await credit.SetDepositProgressAsync(swap.InvoiceId, method, "renewed-since", "4000000");
        Assert.Null(Details().ReceivedUnits);
        await credit.SetDepositProgressAsync(swap.InvoiceId, method, swap.SwapId, "4000000");
        await credit.SetDepositProgressAsync(swap.InvoiceId, method, swap.SwapId, "4000000");

        Assert.Equal(("4000000", swap.SourceUnits, swap.ReceiveSats), (Details().ReceivedUnits, Details().Units, Details().ReceiveSats));
        var announced = Assert.Single(published);
        Assert.Equal((swap.InvoiceId, method, "4000000"),
            (announced.InvoiceId, announced.PaymentMethodId, Assert.IsType<SatoraStablePromptDetails>(announced.Details).ReceivedUnits));

        await credit.DeactivatePromptAsync(swap.InvoiceId, method, swap.SwapId);

        Assert.True((await invoices.GetInvoice(swap.InvoiceId)).GetPaymentPrompt(method) is { Inactive: true });
        Assert.Equal(2, published.Count);
        await credit.SetDepositProgressAsync(swap.InvoiceId, method, swap.SwapId, "5000000");
        Assert.Equal("4000000", Details().ReceivedUnits);
    }

    private (SatoraStableCredit Credit, InvoiceRepository Invoices) Credit(EventAggregator? events = null)
    {
        events ??= new EventAggregator(new Logs());
        var invoices = new InvoiceRepository(btcpay.Contexts, events);
        var handlers = new PaymentMethodHandlerDictionary([new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc)]);
        return (new SatoraStableCredit(invoices, new PaymentService(events, btcpay.Contexts, handlers, invoices), handlers, events,
            NullLogger<SatoraStableCredit>.Instance), invoices);
    }

    // An open invoice whose stablecoin prompt shows the swap's deposit address.
    private async Task<SatoraStableSwap> AddInvoiceAsync(string name)
    {
        var swap = SatoraStableIntegrationTests.Swap("store", name);
        var now = DateTimeOffset.UtcNow;
        var invoice = new InvoiceEntity
        {
            Id = swap.InvoiceId,
            StoreId = "store",
            Currency = "USD",
            Price = 4000,
            InvoiceTime = now,
            ExpirationTime = now.AddMinutes(15),
            MonitoringExpiration = now.AddHours(1)
        };
#pragma warning disable CS0618
        invoice.Rates["BTC"] = 34_000m;
#pragma warning restore CS0618
        var handler = new SatoraStableFakes.Handler(SatoraStablePaymentTests.Btc);
        invoice.SetPaymentPrompt(handler.PaymentMethodId, new PaymentPrompt
        {
            Currency = "BTC",
            Divisibility = 8,
            Destination = swap.DepositAddress,
            PaymentMethodFee = 0m,
            Details = JObject.FromObject(new SatoraStablePromptDetails
            {
                SwapId = swap.SwapId,
                ChainId = swap.ChainId,
                Token = swap.Token,
                TokenAddress = swap.TokenAddress,
                Decimals = swap.Decimals,
                Units = swap.SourceUnits,
                ReceiveSats = swap.ReceiveSats
            }, handler.Serializer)
        });
        var data = new InvoiceData { Id = invoice.Id, StoreDataId = "store", Status = "New" };
        data.SetBlob(invoice);
        await using var ctx = btcpay.Contexts.CreateContext();
        ctx.Invoices.Add(data);
        await ctx.SaveChangesAsync(Ct);
        return swap;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}

// Records what the wallet service asks of the store's stablecoin configs, which need BTCPay's own database.
public sealed class RecordingStableConfig() : SatoraStoreStableConfig(null!, null!)
{
    private readonly ConcurrentQueue<(string StoreId, string Call)> _calls = new();

    public List<string> Calls(string storeId) => _calls.Where(c => c.StoreId == storeId).Select(c => c.Call).ToList();

    public override Task SyncAsync(string storeId, bool walletEnabled, IReadOnlyCollection<SatoraStablecoin> coins)
    {
        _calls.Enqueue((storeId, $"{(walletEnabled ? "on" : "off")}:{string.Join(",", coins.Select(c => c.Id))}"));
        return Task.CompletedTask;
    }
}
