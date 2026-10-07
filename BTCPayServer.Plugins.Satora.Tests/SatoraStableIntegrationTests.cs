using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using Microsoft.EntityFrameworkCore;
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
