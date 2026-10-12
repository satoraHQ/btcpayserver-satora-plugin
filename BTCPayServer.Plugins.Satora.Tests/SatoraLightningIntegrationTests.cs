using System.Collections.Concurrent;
using BTCPayServer.Events;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against a real Postgres: set SATORA_TEST_POSTGRES as for SatoraWalletIntegrationTests.
public class SatoraLightningIntegrationTests(SatoraWalletIntegrationTests.Database db)
    : IClassFixture<SatoraWalletIntegrationTests.Database>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    private SatoraLightningSwaps Swaps => new(db.Contexts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task KeepsOneSwapPerPaymentHashForEachStoreAndDirection()
    {
        await Swaps.AddAsync(Swap("unique", "hash-1"));

        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => Swaps.AddAsync(Swap("unique", "hash-1")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        await Swaps.AddAsync(Swap("unique-other", "hash-1"));
        await Swaps.AddAsync(Swap("unique", "hash-1", SatoraLightningDirection.ArkadeToLightning));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWorkerReadsOnlyOpenIncomingSwaps()
    {
        var open = Swap("worker", "open");
        var expired = Swap("worker", "expired");
        var credited = Swap("worker", "credited");
        foreach (var swap in new[] { open, expired, credited, Swap("worker", "outgoing", SatoraLightningDirection.ArkadeToLightning), Swap("worker-other", "open") })
            await Swaps.AddAsync(swap);
        await Swaps.SetCompletedAsync(expired.SwapId, Now);
        await Swaps.SetCreditedAsync(credited.SwapId, Now);

        var stores = (await Swaps.ListOpenAsync(Ct)).Where(s => s.StoreId.StartsWith("worker", StringComparison.Ordinal)).ToList();
        Assert.Equal(["worker", "worker-other"], stores.Select(s => s.StoreId).Order());
        Assert.Equal(open.ClaimCutoff, stores.Single(s => s.StoreId == "worker").ClaimCutoff, TimeSpan.FromMilliseconds(1));
        Assert.Equal([open.SwapId], (await Swaps.ListOpenAsync("worker", Ct)).Select(s => s.SwapId));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task CancellingMarksOnlyTheStoresOwnUncreditedSwap()
    {
        var swap = Swap("cancel", "hash");
        var credited = Swap("cancel", "credited");
        await Swaps.AddAsync(swap);
        await Swaps.AddAsync(credited);
        await Swaps.SetCreditedAsync(credited.SwapId, Now);

        Assert.False(await Swaps.SetDoNotClaimAsync("cancel-other", swap.SwapId));
        Assert.False((await Swaps.FindAsync("cancel", swap.SwapId, Ct))!.DoNotClaim);
        Assert.True(await Swaps.SetDoNotClaimAsync("cancel", swap.SwapId));
        Assert.False(await Swaps.SetDoNotClaimAsync("cancel", credited.SwapId));

        Assert.True((await Swaps.FindAsync("cancel", swap.SwapId, Ct))!.DoNotClaim);
        Assert.False((await Swaps.FindAsync("cancel", credited.SwapId, Ct))!.DoNotClaim);
    }

    // BTCPay's invoice search knows an invoice's id but no swap id, and a payment out of the wallet pays
    // no invoice of the store's.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AReplaceRefusalNamesIncomingLightningSwapsByTheirInvoice()
    {
        var (_, wallets) = db.Wallets();
        var first = Swap("name-ln", new string('1', 64));
        var renewed = Swap("name-ln", new string('2', 64), createdAt: Now.AddSeconds(1));
        var untracked = Swap("name-ln", new string('3', 64), createdAt: Now.AddSeconds(2));
        var payment = Swap("name-ln", new string('4', 64), SatoraLightningDirection.ArkadeToLightning, createdAt: Now.AddSeconds(3));
        foreach (var swap in new[] { first, renewed, untracked, payment })
            await Swaps.AddAsync(swap);
        db.LightningInvoices.ByPaymentHash[first.PaymentHash] = "invoice-1";
        db.LightningInvoices.ByPaymentHash[renewed.PaymentHash] = "invoice-1";

        Assert.Equal(
            $"Lightning swaps of invoices: invoice-1; Lightning swaps BTCPay has no invoice for: {untracked.SwapId}; Lightning payments: {payment.SwapId}",
            await wallets.DescribeOpenOperationsAsync("name-ln"));
    }

    // The worker starting a claim and BTCPay cancelling the invoice decide against each other on the
    // row: whichever writes first wins, and a cancel that comes too late says so.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AClaimAndACancelOfTheSameSwapExcludeEachOther()
    {
        var logs = new RecordingLogger<SatoraLightningClient>();
        var client = new SatoraLightningClient("race", "key", Network.RegTest, null!, Swaps, new SatoraLightningInvoiceHub(), null!, null!, logs);
        var claimed = Swap("race", "claimed");
        var cancelled = Swap("race", "cancelled");
        await Swaps.AddAsync(claimed);
        await Swaps.AddAsync(cancelled);

        Assert.True(await Swaps.StartClaimAsync("race", claimed.SwapId, Now));
        await client.CancelInvoice(claimed.SwapId, Ct);
        // A retry, or a resume past the cutoff, passes again and keeps when the claim started.
        Assert.True(await Swaps.StartClaimAsync("race", claimed.SwapId, Now.AddMinutes(1)));
        await client.CancelInvoice(cancelled.SwapId, Ct);
        Assert.False(await Swaps.StartClaimAsync("race", cancelled.SwapId, Now));

        var claimedRow = (await Swaps.FindAsync("race", claimed.SwapId, Ct))!;
        Assert.False(claimedRow.DoNotClaim);
        Assert.Equal(Now, claimedRow.ClaimStartedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.True(await Swaps.FindAsync("race", cancelled.SwapId, Ct) is { DoNotClaim: true, ClaimStartedAt: null });
        Assert.Equal([LogLevel.Warning, LogLevel.Information], logs.Entries.Select(e => e.Level));

        // Another store's id, or a row that is gone, is never claimed.
        var other = Swap("race-other", "claimed");
        await Swaps.AddAsync(other);
        Assert.False(await Swaps.StartClaimAsync("race", other.SwapId, Now));
        Assert.False(await Swaps.StartClaimAsync("race", Guid.NewGuid().ToString(), Now));
        Assert.Null((await Swaps.FindAsync("race-other", other.SwapId, Ct))!.ClaimStartedAt);
    }

    // CancelInvoice and the worker write the same row from different threads.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task EachUpdateWritesOnlyItsOwnColumns()
    {
        var swap = Swap("columns", "hash");
        await Swaps.AddAsync(swap);

        await Swaps.SetDoNotClaimAsync("columns", swap.SwapId);
        await Swaps.SetStatusAsync(swap.SwapId, SatoraSwapStatus.ClientRedeemed);
        await Swaps.SetClaimedAsync(swap.SwapId, "ark-txid");
        var paidAt = Now.AddSeconds(5);
        await Swaps.SetCreditedAsync(swap.SwapId, paidAt);

        var row = (await Swaps.FindAsync("columns", swap.SwapId, Ct))!;
        Assert.True(row is { DoNotClaim: true, Status: SatoraSwapStatus.ClientRedeemed, ClaimTxId: "ark-txid", Credited: true });
        Assert.Equal(paidAt, row.PaidAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(paidAt, row.CompletedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal((swap.Bolt11, swap.AmountMsat, swap.ReceiveSats), (row.Bolt11, row.AmountMsat, row.ReceiveSats));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ReadsInvoicesOfTheStoreByIdHashAndPendingState()
    {
        var older = Swap("reads", "older", createdAt: Now.AddMinutes(-2));
        var newer = Swap("reads", "newer", createdAt: Now.AddMinutes(-1));
        var lapsed = Swap("reads", "lapsed", cutoff: Now.AddMinutes(-1));
        foreach (var swap in new[] { older, newer, lapsed, Swap("reads", "outgoing", SatoraLightningDirection.ArkadeToLightning) })
            await Swaps.AddAsync(swap);
        await Swaps.SetCreditedAsync(older.SwapId, Now);

        Assert.Equal(newer.SwapId, (await Swaps.FindByPaymentHashAsync("reads", "newer", Ct))?.SwapId);
        Assert.Null(await Swaps.FindByPaymentHashAsync("reads-other", "newer", Ct));
        Assert.Null(await Swaps.FindByPaymentHashAsync("reads", "outgoing", Ct));
        Assert.Null(await Swaps.FindAsync("reads-other", newer.SwapId, Ct));
        Assert.Equal([newer.SwapId], (await Swaps.ListAsync("reads", pendingOnly: true, Now, 100, Ct)).Select(s => s.SwapId));
        Assert.Equal([lapsed.SwapId, newer.SwapId, older.SwapId], (await Swaps.ListAsync("reads", pendingOnly: false, Now, 100, Ct)).Select(s => s.SwapId));
        Assert.Single(await Swaps.ListAsync("reads", pendingOnly: false, Now, 1, Ct));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AnOpenLightningSwapOfTheCurrentWalletBlocksAReplace()
    {
        var (_, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "replace-ln");
        await Swaps.AddAsync(Swap("replace-ln", "before", createdAt: Now.AddDays(-1)));
        var closed = Swap("replace-ln", "closed", createdAt: DateTimeOffset.UtcNow);
        await Swaps.AddAsync(closed);
        await Swaps.SetCompletedAsync(closed.SwapId, Now);
        Assert.Equal(0, await wallets.CountOpenOperationsAsync("replace-ln"));

        await Swaps.AddAsync(Swap("replace-ln", "open", createdAt: DateTimeOffset.UtcNow));

        Assert.Equal(1, await wallets.CountOpenOperationsAsync("replace-ln"));
        Assert.Equal(SeedWriteRefusal.OpenOperations, (await wallets.GenerateAsync("replace-ln", SeedWriteGuard.ConfirmationWord)).Refusal);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheConnectionKeyFollowsSeedWritesAndRotationAndTheStoreConfigFollowsTheWallet()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "keys");
        var first = (await db.Settings("keys")).LightningConnectionKey!;
        Assert.True(registry.IsLightningKey("keys", first));
        // A registry built later, as after a restart, finds the stored key.
        Assert.True(db.Wallets().Registry.IsLightningKey("keys", first));

        Assert.True(await wallets.RotateLightningKeyAsync("keys"));
        var second = (await db.Settings("keys")).LightningConnectionKey!;
        Assert.NotEqual(first, second);
        Assert.False(registry.IsLightningKey("keys", first));
        Assert.True(registry.IsLightningKey("keys", second));

        Assert.True(await wallets.SetEnabledAsync("keys", false));

        Assert.Equal([(false, first), (true, first), (true, second), (false, second)], db.LightningConfig.Calls("keys"));
        Assert.False(await wallets.RotateLightningKeyAsync("keys-none"));
    }

    // The rotation lands after CreateInvoice's first check, while it waits for the wallet. Without the
    // second check the client would go on to arkd, which this registry cannot reach.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task CreateInvoiceChecksTheKeyAgainOnceItHoldsTheWallet()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "recheck");
        var key = (await db.Settings("recheck")).LightningConnectionKey!;
        var client = new SatoraLightningClient("recheck", key, Network.RegTest, registry, Swaps, new SatoraLightningInvoiceHub(),
            new SatoraClaimWindow(registry.Network), null!, NullLogger<SatoraLightningClient>.Instance);
        registry.BeforeUse = async () =>
        {
            registry.BeforeUse = null;
            Assert.True(await wallets.RotateLightningKeyAsync("recheck"));
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.CreateInvoice(new CreateInvoiceParams(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(15)), Ct));

        Assert.Null(registry.BeforeUse);
        Assert.Empty(await Swaps.ListOpenAsync("recheck", Ct));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task OnlyAnEnabledWalletReplacesTheStoresLightningNode()
    {
        var (_, wallets) = db.Wallets();
        Assert.False(await wallets.UseAsLightningNodeAsync("node"));
        await ReadyAndEnabled(wallets, "node");
        var key = (await db.Settings("node")).LightningConnectionKey!;

        Assert.True(await wallets.UseAsLightningNodeAsync("node"));
        Assert.True(await wallets.SetEnabledAsync("node", false));
        Assert.False(await wallets.UseAsLightningNodeAsync("node"));

        Assert.Equal([key], db.LightningConfig.Replaces("node"));
    }

    private static async Task ReadyAndEnabled(SatoraWalletService wallets, string storeId)
    {
        var (refusal, phrase) = await wallets.GenerateAsync(storeId, null);
        Assert.Equal(SeedWriteRefusal.None, refusal);
        Assert.True(await wallets.ConfirmBackupAsync(storeId, phrase));
        Assert.True(await wallets.SetEnabledAsync(storeId, true));
    }

    private static SatoraLightningSwap Swap(
        string storeId,
        string paymentHash,
        SatoraLightningDirection direction = SatoraLightningDirection.LightningToArkade,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? cutoff = null) => new()
    {
        SwapId = Guid.NewGuid().ToString(),
        StoreId = storeId,
        Direction = direction,
        Status = SatoraSwapStatus.Pending,
        PaymentHash = paymentHash,
        Bolt11 = "lnbcrt" + paymentHash,
        AmountMsat = 1_000_000,
        ReceiveSats = 990,
        ClaimCutoff = cutoff ?? Now.AddMinutes(25),
        ExpiresAt = cutoff ?? Now.AddMinutes(25),
        CreatedAt = createdAt ?? Now
    };
}

// How SatoraLightningCredit finds an incoming swap's invoice in BTCPay's own database, opt-in as above.
public class SatoraLightningInvoiceIntegrationTests(SatoraChainPromptIntegrationTests.BtcPayDatabase btcpay)
    : IClassFixture<SatoraChainPromptIntegrationTests.BtcPayDatabase>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    // LightningLikePaymentHandler tracks each prompt's payment hash under BTC-LN; the same string
    // tracked for another method is not a Lightning swap's.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task FindsTheInvoiceTrackingEachPaymentHash()
    {
        var invoices = new InvoiceRepository(btcpay.Contexts, new EventAggregator(new Logs()));
        var invoiceId = await btcpay.AddInvoiceAsync("lightning");
        var tracked = new string('a', 64);
        var otherMethod = new string('b', 64);
        await invoices.AddAddressInvoice(invoiceId, SatoraLightningCredit.PaymentMethodId, tracked);
        await invoices.AddAddressInvoice(invoiceId, SatoraChainPaymentMethod.Id, otherMethod);
        var credit = new SatoraLightningCredit(invoices, null!, null!, null!, btcpay.Contexts, NullLogger<SatoraLightningCredit>.Instance);

        var found = await credit.FindInvoiceIdsAsync([tracked, otherMethod, new string('c', 64), "not-a-payment-hash"]);

        Assert.Equal([(tracked, invoiceId)], found.Select(f => (f.Key, f.Value)));
    }
}

// The invoices BTCPay's own database would find for incoming Lightning swaps, by payment hash.
public sealed class FakeLightningInvoices() : SatoraLightningCredit(null!, null!, null!, null!, null!, NullLogger<SatoraLightningCredit>.Instance)
{
    public ConcurrentDictionary<string, string> ByPaymentHash { get; } = new();

    public override Task<IReadOnlyDictionary<string, string>> FindInvoiceIdsAsync(IReadOnlyCollection<string> paymentHashes) =>
        Task.FromResult<IReadOnlyDictionary<string, string>>(paymentHashes.Distinct().Where(ByPaymentHash.ContainsKey).ToDictionary(h => h, h => ByPaymentHash[h]));
}

// Records what the wallet service asks of the store's BTC-LN config, which needs BTCPay's own database.
public sealed class RecordingLightningConfig() : SatoraStoreLightningConfig(null!, null!)
{
    private readonly ConcurrentQueue<(string StoreId, bool Enabled, string? Key)> _calls = new();
    private readonly ConcurrentQueue<(string StoreId, string Key)> _replaces = new();

    public List<(bool Enabled, string? Key)> Calls(string storeId) =>
        _calls.Where(c => c.StoreId == storeId).Select(c => (c.Enabled, c.Key)).ToList();

    public List<string> Replaces(string storeId) =>
        _replaces.Where(r => r.StoreId == storeId).Select(r => r.Key).ToList();

    public override Task SyncAsync(string storeId, bool enabled, string? connectionKey, bool replace = false)
    {
        if (replace)
            _replaces.Enqueue((storeId, connectionKey!));
        else
            _calls.Enqueue((storeId, enabled, connectionKey));
        return Task.CompletedTask;
    }
}
