using System.Collections.Concurrent;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against a real Postgres: set SATORA_TEST_POSTGRES as for SatoraWalletIntegrationTests.
public class SatoraChainIntegrationTests(SatoraWalletIntegrationTests.Database db)
    : IClassFixture<SatoraWalletIntegrationTests.Database>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    private SatoraChainSwaps Swaps => new(db.Contexts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task KeepsOneSwapPerHtlcAddress()
    {
        await Swaps.AddAsync(Swap("unique", "first"));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => Swaps.AddAsync(Swap("unique", "second", htlc: "htlc-unique-first")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWorkerReadsOpenSwapsIncludingThoseWaitingForARefund()
    {
        var open = Swap("worker", "open");
        var refund = Swap("worker", "refund");
        var done = Swap("worker", "done");
        foreach (var swap in new[] { open, refund, done, Swap("worker-other", "other") })
            await Swaps.AddAsync(swap);
        await Swaps.SetNeedsRefundAsync(refund.SwapId, null, nameof(SatoraSwapStatus.ServerWontFund), Now);
        await Swaps.SetCompletedAsync(done.SwapId, Now);

        Assert.Contains("worker", await Swaps.ListOpenStoresAsync(Ct));
        Assert.Equal([open.SwapId, refund.SwapId], (await Swaps.ListOpenAsync("worker", Ct)).Select(s => s.SwapId).Order());
        var waiting = Assert.Single(await Swaps.ListNeedingRefundAsync("worker"));
        Assert.Equal((refund.SwapId, nameof(SatoraSwapStatus.ServerWontFund)), (waiting.SwapId, waiting.NeedsRefundReason));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task KeepsWhatTheWorkerWritesAndTheStatusByName()
    {
        var swap = Swap("writes", "swap");
        await Swaps.AddAsync(swap);

        await Swaps.SetStatusAsync(swap.SwapId, SatoraSwapStatus.ClientFundedServerRefunded);
        await Swaps.SetPaymentRecordedAsync(swap.SwapId, true);
        await Swaps.SetPaymentRecordedAsync(swap.SwapId, false);
        Assert.False((await Swaps.FindAsync("writes", swap.SwapId))!.PaymentRecorded);
        Assert.True(await Swaps.SetOverpaidAsync(swap.SwapId, 750));
        await Swaps.SetClaimedAsync(swap.SwapId, "ark");
        // The claim took the funding as it was.
        Assert.False(await Swaps.SetOverpaidAsync(swap.SwapId, 0));
        await Swaps.SetCreditedAsync(swap.SwapId);

        var read = await Swaps.FindAsync("writes", swap.SwapId);
        Assert.True(read is { Status: SatoraSwapStatus.ClientFundedServerRefunded, PaymentRecorded: true, OverpaidSats: 750, ClaimTxId: "ark", Credited: true, CompletedAt: null });
        Assert.Equal((swap.VhtlcRefundLocktime, swap.InvoiceExpiresAt), (read.VhtlcRefundLocktime, read.InvoiceExpiresAt));
        Assert.Null(await Swaps.FindAsync("writes-other", swap.SwapId));
        await using var ctx = db.Contexts.CreateContext();
        var stored = await ctx.Database.SqlQuery<string>(
            $"""SELECT "Status" AS "Value" FROM "BTCPayServer.Plugins.Satora"."SatoraChainSwaps" WHERE "SwapId" = {swap.SwapId}""").SingleAsync(Ct);
        Assert.Equal(nameof(SatoraSwapStatus.ClientFundedServerRefunded), stored);
    }

    // Only from the row the worker read: open, not yet waiting for a refund, with the same claim.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ASwapGoesToRefundsOnlyAsTheWorkerReadIt()
    {
        var unclaimed = Swap("needs-refund", "unclaimed");
        var claimed = Swap("needs-refund", "claimed");
        var done = Swap("needs-refund", "done");
        foreach (var swap in new[] { unclaimed, claimed, done })
            await Swaps.AddAsync(swap);
        await Swaps.SetClaimedAsync(claimed.SwapId, "ark");
        await Swaps.SetCompletedAsync(done.SwapId, Now);

        Assert.True(await Swaps.SetNeedsRefundAsync(unclaimed.SwapId, null, SatoraChainActions.UnclaimedReason, Now));
        Assert.False(await Swaps.SetNeedsRefundAsync(unclaimed.SwapId, null, SatoraChainActions.LeftoverReason, Now.AddMinutes(1)));
        Assert.False(await Swaps.SetNeedsRefundAsync(claimed.SwapId, null, SatoraChainActions.UnclaimedReason, Now));
        Assert.True(await Swaps.SetNeedsRefundAsync(claimed.SwapId, "ark", SatoraChainActions.LeftoverReason, Now));
        Assert.False(await Swaps.SetNeedsRefundAsync(done.SwapId, null, SatoraChainActions.UnclaimedReason, Now));

        Assert.True(await Swaps.FindAsync("needs-refund", unclaimed.SwapId) is { NeedsRefundReason: SatoraChainActions.UnclaimedReason, NeedsRefundAt: not null });
        Assert.True(await Swaps.FindAsync("needs-refund", claimed.SwapId) is { NeedsRefundReason: SatoraChainActions.LeftoverReason, ClaimTxId: "ark" });
        Assert.True(await Swaps.FindAsync("needs-refund", done.SwapId) is { NeedsRefundAt: null, NeedsRefundReason: null });
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheRefundAddressCanChangeOnlyUntilTheWorkerStartsTheRefund()
    {
        var swap = Swap("refund", "swap");
        await Swaps.AddAsync(swap);

        Assert.False(await Swaps.RequestRefundAsync("refund", swap.SwapId, "first"));
        await Swaps.SetNeedsRefundAsync(swap.SwapId, null, nameof(SatoraSwapStatus.ClientInvalidFunded), Now);
        Assert.False(await Swaps.RequestRefundAsync("refund-other", swap.SwapId, "first"));
        Assert.True(await Swaps.RequestRefundAsync("refund", swap.SwapId, "first"));
        Assert.True(await Swaps.RequestRefundAsync("refund", swap.SwapId, "second"));

        Assert.False(await Swaps.TryStartRefundAsync(swap.SwapId, "first"));
        Assert.True(await Swaps.TryStartRefundAsync(swap.SwapId, "second"));
        Assert.False(await Swaps.RequestRefundAsync("refund", swap.SwapId, "third"));
        Assert.True(await Swaps.TryStartRefundAsync(swap.SwapId, "second"));

        // A broadcast refund stays listed until it confirms.
        await Swaps.SetRefundTxIdAsync(swap.SwapId, "refund-tx");
        Assert.False(await Swaps.TryStartRefundAsync(swap.SwapId, "second"));
        Assert.Single(await Swaps.ListNeedingRefundAsync("refund"));
        await Swaps.SetCompletedAsync(swap.SwapId, Now);
        Assert.Empty(await Swaps.ListNeedingRefundAsync("refund"));
        Assert.True(await Swaps.FindAsync("refund", swap.SwapId) is { RefundAddress: "second", RefundAttempted: true, RefundTxId: "refund-tx", CompletedAt: not null, ClosedReason: null });
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task OnlyTheStoresOwnOpenRefundCanBeDismissed()
    {
        var swap = Swap("dismiss", "swap");
        await Swaps.AddAsync(swap);

        Assert.False(await Swaps.DismissRefundAsync("dismiss", swap.SwapId, Now));
        await Swaps.SetNeedsRefundAsync(swap.SwapId, null, nameof(SatoraSwapStatus.ClientInvalidFunded), Now);
        Assert.False(await Swaps.DismissRefundAsync("dismiss-other", swap.SwapId, Now));
        Assert.True(await Swaps.DismissRefundAsync("dismiss", swap.SwapId, Now));
        Assert.False(await Swaps.DismissRefundAsync("dismiss", swap.SwapId, Now));

        Assert.True(await Swaps.FindAsync("dismiss", swap.SwapId) is { CompletedAt: not null, ClosedReason: SatoraChainRefunds.DismissedReason });
        Assert.False(await Swaps.TryStartRefundAsync(swap.SwapId, "address"));
    }

    // The worker read the swap before the owner dismissed it; its close then changes nothing.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWorkerClosesOnlyASwapStillOpen()
    {
        var dismissed = Swap("close", "dismissed");
        var empty = Swap("close", "empty");
        var refunded = Swap("close", "refunded");
        var at = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds());
        foreach (var swap in new[] { dismissed, empty, refunded })
        {
            await Swaps.AddAsync(swap);
            await Swaps.SetNeedsRefundAsync(swap.SwapId, null, nameof(SatoraSwapStatus.ClientInvalidFunded), at);
        }
        Assert.True(await Swaps.DismissRefundAsync("close", dismissed.SwapId, at));

        Assert.False(await Swaps.SetClosedAsync(dismissed.SwapId, SatoraChainRefunds.EmptyReason, at.AddMinutes(1)));
        Assert.False(await Swaps.SetCompletedAsync(dismissed.SwapId, at.AddMinutes(1)));
        Assert.True(await Swaps.SetClosedAsync(empty.SwapId, SatoraChainRefunds.EmptyReason, at));
        Assert.False(await Swaps.SetClosedAsync(empty.SwapId, SatoraChainRefunds.UneconomicReason, at.AddMinutes(1)));
        Assert.True(await Swaps.SetCompletedAsync(refunded.SwapId, at));
        Assert.False(await Swaps.SetCompletedAsync(refunded.SwapId, at.AddMinutes(1)));

        Assert.True(await Swaps.FindAsync("close", dismissed.SwapId) is { ClosedReason: SatoraChainRefunds.DismissedReason } d && d.CompletedAt == at);
        Assert.True(await Swaps.FindAsync("close", empty.SwapId) is { ClosedReason: SatoraChainRefunds.EmptyReason } e && e.CompletedAt == at);
        Assert.True(await Swaps.FindAsync("close", refunded.SwapId) is { ClosedReason: null } r && r.CompletedAt == at);
    }

    // The statement the store's notification gives for a swap closed without a refund lists it again
    // as it was, its refund address and reason kept.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ASwapClosedWithoutARefundIsReopenedAsItWas()
    {
        var swap = Swap("reopen", "it's");
        await Swaps.AddAsync(swap);
        await Swaps.SetNeedsRefundAsync(swap.SwapId, null, SatoraChainActions.UnclaimedReason, Now);
        await Swaps.RequestRefundAsync("reopen", swap.SwapId, "address");
        await Swaps.SetClosedAsync(swap.SwapId, SatoraChainRefunds.UneconomicReason, Now);
        Assert.Empty(await Swaps.ListNeedingRefundAsync("reopen"));

        await using (var ctx = db.Contexts.CreateContext())
            Assert.Equal(1, await ctx.Database.ExecuteSqlRawAsync(SatoraChainRefunds.ReopenStatement(swap.SwapId), Ct));

        var reopened = Assert.Single(await Swaps.ListNeedingRefundAsync("reopen"));
        Assert.True(reopened is { NeedsRefundReason: SatoraChainActions.UnclaimedReason, RefundAddress: "address", CompletedAt: null, ClosedReason: null });
        Assert.True(await Swaps.TryStartRefundAsync(swap.SwapId, "address"));
    }

    // A replace would strand them: only the store's current seed can claim or refund its swaps.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task CountsOpenAndRefundWaitingOnChainSwapsAsNeedingTheSeed()
    {
        var (_, wallets) = db.Wallets();
        var open = Swap("count-chain", "open");
        var refund = Swap("count-chain", "refund");
        var done = Swap("count-chain", "done");
        foreach (var swap in new[] { open, refund, done, Swap("count-chain-other", "other") })
            await Swaps.AddAsync(swap);
        await Swaps.SetNeedsRefundAsync(refund.SwapId, null, nameof(SatoraSwapStatus.ClientInvalidFunded), Now);
        await Swaps.SetCompletedAsync(done.SwapId, Now);

        Assert.Equal(2, await wallets.CountOpenOperationsAsync("count-chain"));
        Assert.Equal("on-chain swaps of invoices: invoice-open; refunds on the On-chain refunds page for invoices: invoice-refund",
            await wallets.DescribeOpenOperationsAsync("count-chain"));
        await Swaps.SetRefundTxIdAsync(refund.SwapId, "refund-tx");
        Assert.Equal(2, await wallets.CountOpenOperationsAsync("count-chain"));
        // Closed without a refund: its address held nothing worth one.
        await Swaps.SetClosedAsync(refund.SwapId, SatoraChainRefunds.EmptyReason, Now);
        Assert.Equal(1, await wallets.CountOpenOperationsAsync("count-chain"));
        Assert.Equal(SatoraChainRefunds.EmptyReason, (await Swaps.FindAsync("count-chain", refund.SwapId))!.ClosedReason);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWalletOffersTheOnChainMethodOnlyWhileEnabled()
    {
        var (_, wallets) = db.Wallets();
        var (refusal, phrase) = await wallets.GenerateAsync("offer", null);
        Assert.Equal(SeedWriteRefusal.None, refusal);
        Assert.True(await wallets.ConfirmBackupAsync("offer", phrase));

        Assert.True(await wallets.SetEnabledAsync("offer", true));
        Assert.True(await wallets.SetEnabledAsync("offer", false));

        // A new wallet starts disabled.
        Assert.Equal([false, true, false], db.ChainConfig.Calls("offer"));
    }

    private static SatoraChainSwap Swap(string storeId, string name, string? htlc = null) => new()
    {
        SwapId = $"{storeId}-{name}",
        StoreId = storeId,
        InvoiceId = "invoice-" + name,
        Status = SatoraSwapStatus.Pending,
        HtlcAddress = htlc ?? $"htlc-{storeId}-{name}",
        DepositSats = 100_000,
        ReceiveSats = 99_500,
        VhtlcRefundLocktime = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()).AddHours(12),
        BtcRefundLocktime = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()).AddHours(24),
        InvoiceExpiresAt = DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()).AddMinutes(15),
        CreatedAt = Now
    };
}

// What SatoraChainCredit writes to an invoice through BTCPay's own InvoiceRepository, opt-in as above.
public class SatoraChainPromptIntegrationTests(SatoraChainPromptIntegrationTests.BtcPayDatabase btcpay)
    : IClassFixture<SatoraChainPromptIntegrationTests.BtcPayDatabase>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    // An open checkout fetches the prompt again only on InvoiceNewPaymentDetailsEvent.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ADeactivatedPromptIsSavedInactiveAndAnOpenCheckoutIsTold()
    {
        var events = new EventAggregator(new Logs());
        var published = new List<InvoiceNewPaymentDetailsEvent>();
        using var subscription = events.Subscribe<InvoiceNewPaymentDetailsEvent>(published.Add);
        var invoices = new InvoiceRepository(btcpay.Contexts, events);
        var credit = new SatoraChainCredit(invoices, null!, null!, events, NullLogger<SatoraChainCredit>.Instance);
        var invoiceId = await btcpay.AddInvoiceAsync("deactivate");

        await credit.DeactivatePromptAsync(invoiceId, "renewed-since");
        Assert.True((await invoices.GetInvoice(invoiceId)).GetPaymentPrompt(SatoraChainPaymentMethod.Id)!.Activated);
        Assert.Empty(published);

        await credit.DeactivatePromptAsync(invoiceId, "deactivate");

        var prompt = (await invoices.GetInvoice(invoiceId)).GetPaymentPrompt(SatoraChainPaymentMethod.Id)!;
        Assert.True(prompt is { Inactive: true, Destination: "htlc-deactivate" });
        var announced = Assert.Single(published);
        Assert.Equal((invoiceId, SatoraChainPaymentMethod.Id, "deactivate"),
            (announced.InvoiceId, announced.PaymentMethodId, Assert.IsType<SatoraChainPromptDetails>(announced.Details).SwapId));
    }

    // The renewer's new swap lands while the worker's deactivation of the old one has read the invoice
    // but not yet written it back: written then, the old prompt would replace the renewed one.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ADeactivationNeverWritesBackAPromptARenewalReplaced()
    {
        var events = new EventAggregator(new Logs());
        var invoices = new InvoiceRepository(btcpay.Contexts, events);
        var credit = new PausingCredit(invoices, events);
        // Paid in part, so the swap's deposit is no longer what is due.
        var invoiceId = await btcpay.AddInvoiceAsync("replaced", depositSats: 5_000_000);
        var renewer = new Renewer(credit, async () =>
        {
            var renewed = (await invoices.GetInvoice(invoiceId)).GetPaymentPrompt(SatoraChainPaymentMethod.Id)!;
            renewed.Inactive = false;
            renewed.Destination = "htlc-renewed";
            renewed.Details = JObject.FromObject(new SatoraChainPromptDetails { SwapId = "renewed", DepositSats = 11_764_941 });
            await invoices.UpdatePrompt(invoiceId, renewed);
            return true;
        });

        var deactivation = credit.DeactivatePromptAsync(invoiceId, "replaced");
        await credit.Read.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var renewal = renewer.RenewAsync(await invoices.GetInvoice(invoiceId));
        await Task.WhenAny(renewal, Task.Delay(TimeSpan.FromMilliseconds(500), Ct));
        credit.Resume.SetResult();
        await Task.WhenAll(deactivation, renewal).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        var prompt = (await invoices.GetInvoice(invoiceId)).GetPaymentPrompt(SatoraChainPaymentMethod.Id)!;
        Assert.True(prompt is { Activated: true, Destination: "htlc-renewed" });
        Assert.Equal(1, renewer.Renewals);
    }

    // The Events section of the invoice's page lists these rows. BTCPay drops one for an invoice that is
    // gone, which is no error.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ANoteGoesToTheInvoicesEvents()
    {
        var invoices = new InvoiceRepository(btcpay.Contexts, new EventAggregator(new Logs()));
        var logs = new RecordingLogger<SatoraChainCredit>();
        var credit = new SatoraChainCredit(invoices, null!, null!, null!, logs);
        var invoiceId = await btcpay.AddInvoiceAsync("noted");

        await credit.NoteAsync(SatoraChainActionsTests.Swap(swapId: "noted"), "Satora on-chain swap noted needs a refund (Underpaid).", InvoiceEventData.EventSeverity.Warning);
        await credit.NoteAsync(SatoraChainActionsTests.Swap(swapId: "gone"), "Satora on-chain swap gone needs a refund (Underpaid).", InvoiceEventData.EventSeverity.Warning);

        var note = Assert.Single(await invoices.GetInvoiceLogs(invoiceId));
        Assert.Equal((InvoiceEventData.EventSeverity.Warning, "Satora on-chain swap noted needs a refund (Underpaid)."), (note.Severity, note.Message));
        Assert.Empty(await invoices.GetInvoiceLogs("invoice-gone"));
        Assert.Empty(logs.Entries);
    }

    // As at the address of a swap a renewal replaced: each payment is judged as it settles, against the
    // invoice's other settled payments, so of two that each pay the invoice in full only the one settled
    // second is beyond it, also when the first one's credit is repeated afterwards. A funding the worker
    // missed is added settled, and judged the same.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task OnlyAPaymentSettledAfterTheOthersPaidTheInvoiceInFullIsSettledAfterPaidInFull()
    {
        var events = new EventAggregator(new Logs());
        var invoices = new InvoiceRepository(btcpay.Contexts, events);
        var handlers = new PaymentMethodHandlerDictionary([new SatoraChainPaymentHandler(SatoraStablePaymentTests.Btc, null!, null!)]);
        var credit = new SatoraChainCredit(invoices, new PaymentService(events, btcpay.Contexts, handlers, invoices), handlers, events,
            NullLogger<SatoraChainCredit>.Instance);
        var invoiceId = await btcpay.AddInvoiceAsync("paid");
        SatoraChainSwap Swap(string swapId)
        {
            var swap = SatoraChainActionsTests.Swap(swapId: swapId);
            (swap.InvoiceId, swap.DepositSats, swap.ReceiveSats) = (invoiceId, 11_764_941, 11_764_706);
            return swap;
        }
        var now = DateTimeOffset.UtcNow;
        var (first, second, missed) = (Swap("first"), Swap("second"), Swap("missed"));
        await credit.RecordAsync(first, PaymentStatus.Processing, now);
        await credit.RecordAsync(second, PaymentStatus.Processing, now);

        Assert.Equal(SatoraCreditOutcome.Recorded, await credit.RecordAsync(first, PaymentStatus.Settled, now));
        Assert.Equal(SatoraCreditOutcome.Recorded, await credit.RecordAsync(second, PaymentStatus.Settled, now));
        Assert.Equal(SatoraCreditOutcome.AlreadyRecorded, await credit.RecordAsync(first, PaymentStatus.Settled, now));
        Assert.Equal(SatoraCreditOutcome.Recorded, await credit.RecordAsync(missed, PaymentStatus.Settled, now));

        Assert.False(await credit.SettledAfterPaidInFullAsync(first));
        Assert.True(await credit.SettledAfterPaidInFullAsync(second));
        Assert.True(await credit.SettledAfterPaidInFullAsync(missed));
        Assert.Equal(3, (await invoices.GetInvoice(invoiceId)).GetPayments(false).Count(p => p.Status == PaymentStatus.Settled));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Stops between reading the invoice and writing its prompt back, until resumed.
    private sealed class PausingCredit(InvoiceRepository invoices, EventAggregator events)
        : SatoraChainCredit(invoices, null!, null!, events, NullLogger<SatoraChainCredit>.Instance)
    {
        public TaskCompletionSource Read { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<InvoiceEntity?> ReadInvoiceAsync(string invoiceId)
        {
            var invoice = await base.ReadInvoiceAsync(invoiceId);
            Read.TrySetResult();
            await Resume.Task;
            return invoice;
        }
    }

    // Saves the new swap's prompt as InvoiceActivator would.
    private sealed class Renewer(SatoraChainCredit credit, Func<Task<bool>> renew)
        : SatoraChainPromptRenewer(null!, null!, null!, credit, NullLogger<SatoraChainPromptRenewer>.Instance)
    {
        public int Renewals { get; private set; }

        protected override Task<bool> RenewPromptAsync(string invoiceId)
        {
            Renewals++;
            return renew();
        }
    }

    // BTCPay's own tables, migrated as BTCPay migrates them at startup, in a database of their own.
    public sealed class BtcPayDatabase : IAsyncLifetime
    {
        private readonly string _name = $"satora_btcpay_test_{Guid.NewGuid():N}";

        public ApplicationDbContextFactory Contexts { get; private set; } = null!;

        public async ValueTask InitializeAsync()
        {
            if (SatoraWalletIntegrationTests.Database.Server is not { } server)
                return;
            await ExecuteOnServer(server, $"CREATE DATABASE {_name}");
            var connectionString = new NpgsqlConnectionStringBuilder(server) { Database = _name }.ConnectionString;
            Contexts = new ApplicationDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = connectionString }), NullLoggerFactory.Instance);
            await using var ctx = Contexts.CreateContext();
            await ctx.Database.MigrateAsync();
            ctx.Stores.Add(new StoreData { Id = "store", StoreName = "store" });
            await ctx.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (SatoraWalletIntegrationTests.Database.Server is not { } server)
                return;
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServer(server, $"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
        }

        // An open invoice whose BTC-SATORA-CHAIN prompt shows the swap's address for its deposit, by
        // default exactly what is due.
        public async Task<string> AddInvoiceAsync(string swapId, long depositSats = 11_764_941)
        {
            var now = DateTimeOffset.UtcNow;
            var invoice = new InvoiceEntity
            {
                Id = "invoice-" + swapId,
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
            invoice.SetPaymentPrompt(SatoraChainPaymentMethod.Id, new PaymentPrompt
            {
                Currency = "BTC",
                Divisibility = 8,
                Destination = "htlc-" + swapId,
                PaymentMethodFee = 0.00000235m,
                Details = JObject.FromObject(new SatoraChainPromptDetails { SwapId = swapId, DepositSats = depositSats })
            });
            var data = new InvoiceData { Id = invoice.Id, StoreDataId = "store", Status = "New" };
            data.SetBlob(invoice);
            await using var ctx = Contexts.CreateContext();
            ctx.Invoices.Add(data);
            await ctx.SaveChangesAsync();
            return invoice.Id;
        }

        private static async Task ExecuteOnServer(string server, string sql)
        {
            await using var connection = new NpgsqlConnection(server);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}

// Records what the wallet service asks of the store's BTC-SATORA-CHAIN config, which needs BTCPay's own database.
public sealed class RecordingChainConfig() : SatoraStoreChainConfig(null!, null!)
{
    private readonly ConcurrentQueue<(string StoreId, bool Enabled)> _calls = new();

    public List<bool> Calls(string storeId) => _calls.Where(c => c.StoreId == storeId).Select(c => c.Enabled).ToList();

    public override Task SyncAsync(string storeId, bool enabled)
    {
        _calls.Enqueue((storeId, enabled));
        return Task.CompletedTask;
    }
}
