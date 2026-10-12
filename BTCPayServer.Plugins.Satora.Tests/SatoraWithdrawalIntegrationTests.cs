using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against a real Postgres: set SATORA_TEST_POSTGRES as for SatoraWalletIntegrationTests.
public class SatoraWithdrawalIntegrationTests(SatoraWalletIntegrationTests.Database db)
    : IClassFixture<SatoraWalletIntegrationTests.Database>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    private SatoraWithdrawals Withdrawals => new(db.Contexts);

    private SatoraOutgoingSwaps Outgoing => new(db.Contexts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AReviewKeyIsUsedOnceAcrossAllStores()
    {
        var key = NewKey();

        Assert.True(await Withdrawals.TryAddAsync(Withdrawal("keys", key)));
        Assert.False(await Withdrawals.TryAddAsync(Withdrawal("keys", key)));
        Assert.False(await Withdrawals.TryAddAsync(Withdrawal("keys-other", key)));
        Assert.Null(await Withdrawals.FindAsync("keys-other", key));
        Assert.Equal("keys", (await Withdrawals.FindAsync("keys", key))?.StoreId);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ASwapBelongsToOneWithdrawalAtMost()
    {
        Assert.True(await Withdrawals.TryAddAsync(Withdrawal("swap-link", NewKey())));
        Assert.True(await Withdrawals.TryAddAsync(Withdrawal("swap-link", NewKey())));
        var swapId = Guid.NewGuid().ToString();
        await using var ctx = db.Contexts.CreateContext();
        ctx.SatoraWithdrawals.Add(Withdrawal("swap-link", NewKey(), swapId: swapId));
        await ctx.SaveChangesAsync(Ct);

        ctx.SatoraWithdrawals.Add(Withdrawal("swap-link", NewKey(), swapId: swapId));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync(Ct));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AttachingASwapWritesItAndTheLinkTogether()
    {
        var key = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("attach", key, SatoraWithdrawalType.LightningAddress));
        var swap = Swap("attach", "attach-hash");

        await Withdrawals.AttachSwapAsync(key, swap);

        Assert.Equal(swap.SwapId, (await Withdrawals.FindAsync("attach", key))?.SwapId);
        Assert.Equal(swap.SwapId, (await Outgoing.FindByPaymentHashAsync("attach", "attach-hash"))?.SwapId);
        // A second swap for the same invoice is refused, and so is the link that came with it.
        var second = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("attach", second, SatoraWithdrawalType.LightningAddress));
        await Assert.ThrowsAsync<DbUpdateException>(() => Withdrawals.AttachSwapAsync(second, Swap("attach", "attach-hash")));
        Assert.Null((await Withdrawals.FindAsync("attach", second))?.SwapId);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ListsOnlyWithdrawalsWhoseOutcomeTheOwnerMustTell()
    {
        var unknown = NewKey();
        var pending = NewKey();
        var lightning = NewKey();
        var sent = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("unresolved", unknown, status: SatoraWithdrawalStatus.Unknown));
        await Withdrawals.TryAddAsync(Withdrawal("unresolved", pending));
        await Withdrawals.TryAddAsync(Withdrawal("unresolved", lightning, SatoraWithdrawalType.LightningAddress));
        await Withdrawals.AttachSwapAsync(lightning, Swap("unresolved", "unresolved-hash"));
        await Withdrawals.TryAddAsync(Withdrawal("unresolved", sent, status: SatoraWithdrawalStatus.Sent));
        await Withdrawals.TryAddAsync(Withdrawal("unresolved-other", NewKey(), status: SatoraWithdrawalStatus.Unknown));

        Assert.Equal(new[] { unknown, pending }.Order(), (await Withdrawals.ListUnresolvedAsync("unresolved")).Select(w => w.Id).Order());
        Assert.Equal(4, (await Withdrawals.ListAsync("unresolved", 50)).Count);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWorkersOutcomeChangesOnlyAPendingWithdrawal()
    {
        var pending = NewKey();
        var resolved = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("outcome", pending, SatoraWithdrawalType.LightningAddress));
        await Withdrawals.TryAddAsync(Withdrawal("outcome", resolved, SatoraWithdrawalType.LightningAddress));
        var pendingSwap = Swap("outcome", "outcome-1");
        var resolvedSwap = Swap("outcome", "outcome-2");
        await Withdrawals.AttachSwapAsync(pending, pendingSwap);
        await Withdrawals.AttachSwapAsync(resolved, resolvedSwap);
        await Withdrawals.SetOutcomeAsync(resolved, SatoraWithdrawalStatus.Failed, null, "refused");

        Assert.True(await Withdrawals.ClosePaidSwapAsync(pendingSwap.SwapId, "ark-txid", Now));
        Assert.True(await Withdrawals.ClosePaidSwapAsync(resolvedSwap.SwapId, "ark-txid", Now));

        Assert.True(await Withdrawals.FindAsync("outcome", pending) is { Status: SatoraWithdrawalStatus.Sent, TxId: "ark-txid" });
        Assert.True(await Withdrawals.FindAsync("outcome", resolved) is { Status: SatoraWithdrawalStatus.Failed, TxId: null, Error: "refused" });
    }

    // The store closed the payment as failed while the worker had it read open: none of the worker's
    // closes that follow may overwrite that answer, on the swap or on its withdrawal.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheWorkersClosesNeverOverwriteTheStoresClose()
    {
        var id = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("closed", id, SatoraWithdrawalType.LightningAddress));
        var swap = Swap("closed", "closed-1");
        await Withdrawals.AttachSwapAsync(id, swap);
        Assert.True(await Withdrawals.CloseSwapAsync("closed", swap.SwapId, paid: false, Now));

        var later = Now.AddMinutes(1);
        Assert.False(await Withdrawals.ClosePaidSwapAsync(swap.SwapId, "ark-fund", later));
        Assert.False(await Withdrawals.CloseRefundedSwapAsync(swap.SwapId, "ark-refund", "refunded", later));
        Assert.False(await Withdrawals.CloseUnfundedSwapAsync(swap.SwapId, "unfunded", later));

        var row = (await Outgoing.FindByPaymentHashAsync("closed", "closed-1"))!;
        Assert.True(row is { PaidAt: null, RefundTxId: null });
        Assert.Equal(Now, row.CompletedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.True(await Withdrawals.FindAsync("closed", id) is { Status: SatoraWithdrawalStatus.Failed, TxId: null });
    }

    // The worker read the swap unattempted; a payment marked it attempted before the worker wrote.
    // The close and the funding attempt each apply only to a row the other has not changed.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AnUnattemptedSwapIsClosedWithItsWithdrawalOnlyWhileItIsUnattemptedAndIsNeverAttemptedOnceClosed()
    {
        var unattempted = NewKey();
        var attempted = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("unattempted", unattempted, SatoraWithdrawalType.LightningAddress));
        await Withdrawals.TryAddAsync(Withdrawal("unattempted", attempted, SatoraWithdrawalType.LightningAddress));
        var unattemptedSwap = Swap("unattempted", "unattempted-1");
        var attemptedSwap = Swap("unattempted", "unattempted-2");
        await Withdrawals.AttachSwapAsync(unattempted, unattemptedSwap);
        await Withdrawals.AttachSwapAsync(attempted, attemptedSwap);
        Assert.True(await Outgoing.SetFundingAttemptedAsync(attemptedSwap.SwapId));

        Assert.True(await Withdrawals.CloseUnattemptedSwapAsync(unattemptedSwap.SwapId, SatoraSwapStatus.Expired, "unfunded", Now));
        Assert.False(await Withdrawals.CloseUnattemptedSwapAsync(attemptedSwap.SwapId, SatoraSwapStatus.Expired, "unfunded", Now));
        Assert.False(await Withdrawals.CloseUnattemptedSwapAsync(unattemptedSwap.SwapId, SatoraSwapStatus.Expired, "again", Now));
        Assert.False(await Outgoing.SetFundingAttemptedAsync(unattemptedSwap.SwapId));

        Assert.True(await Withdrawals.FindAsync("unattempted", unattempted) is { Status: SatoraWithdrawalStatus.Failed, Error: "unfunded" });
        Assert.Equal(SatoraWithdrawalStatus.Pending, (await Withdrawals.FindAsync("unattempted", attempted))!.Status);
        Assert.True(await Outgoing.FindByPaymentHashAsync("unattempted", "unattempted-1") is
            { Status: SatoraSwapStatus.Expired, FundingAttempted: false, CompletedAt: not null });
        Assert.Equal([attemptedSwap.SwapId], (await Outgoing.ListOpenAsync("unattempted", Ct)).Select(s => s.SwapId));
    }

    [Theory(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    [InlineData(true, SatoraWithdrawalStatus.Sent)]
    [InlineData(false, SatoraWithdrawalStatus.Failed)]
    public async Task ARefundWithoutTheServerKeepsTheSwapOpenUntilTheStoreClosesItWithItsAnswer(bool paid, SatoraWithdrawalStatus expected)
    {
        var store = $"close-{paid}";
        var key = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal(store, key, SatoraWithdrawalType.LightningAddress));
        var swap = Swap(store, "close-hash");
        await Withdrawals.AttachSwapAsync(key, swap);
        await Outgoing.SetFundingAttemptedAsync(swap.SwapId);

        await Outgoing.SetRefundTxIdAsync(swap.SwapId, "ark-refund");
        Assert.True((await Outgoing.ListOpenAsync(store, Ct)).Single() is { RefundTxId: "ark-refund", CompletedAt: null });

        // A close that matches no row leaves the withdrawal as it was.
        Assert.False(await Withdrawals.CloseSwapAsync("close-other", swap.SwapId, paid, Now));
        Assert.Equal(SatoraWithdrawalStatus.Pending, (await Withdrawals.FindAsync(store, key))!.Status);
        Assert.Equal(key, (await Withdrawals.FindBySwapIdAsync(store, swap.SwapId))?.Id);
        Assert.Null(await Withdrawals.FindBySwapIdAsync("close-other", swap.SwapId));

        Assert.True(await Withdrawals.CloseSwapAsync(store, swap.SwapId, paid, Now));
        Assert.False(await Withdrawals.CloseSwapAsync(store, swap.SwapId, !paid, Now));
        Assert.Empty(await Outgoing.ListOpenAsync(store, Ct));
        var closed = (await Outgoing.FindByPaymentHashAsync(store, "close-hash"))!;
        Assert.Equal((paid, true), (closed.PaidAt is not null, closed.CompletedAt is not null));
        Assert.True(await Withdrawals.FindAsync(store, key) is { SwapId: not null } w && w.Status == expected);
        Assert.Empty(await Withdrawals.ListUnresolvedAsync(store));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ARequestsOutcomeDoesNotOverwriteTheOwnersAnswer()
    {
        var key = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("owner-first", key, createdAt: Now - SatoraWithdrawals.InterruptedAfter));
        Assert.True(await Withdrawals.ResolveAsync("owner-first", key, sent: false, running: false, Now));

        await Withdrawals.SetOutcomeAsync(key, SatoraWithdrawalStatus.Sent, "ark-txid", null);

        Assert.True(await Withdrawals.FindAsync("owner-first", key) is { Status: SatoraWithdrawalStatus.Failed, TxId: null });
    }

    // Two answers read the same unknown row; only the first to write applies.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task OnlyOneOfTwoConcurrentAnswersApplies()
    {
        for (var i = 0; i < 10; i++)
        {
            var key = NewKey();
            await Withdrawals.TryAddAsync(Withdrawal("concurrent", key, status: SatoraWithdrawalStatus.Unknown));

            var answers = await Task.WhenAll(
                Task.Run(() => Withdrawals.ResolveAsync("concurrent", key, sent: true, running: false, Now)),
                Task.Run(() => Withdrawals.ResolveAsync("concurrent", key, sent: false, running: false, Now)));

            var applied = Assert.Single(answers.Select((answer, index) => (answer, sent: index == 0)), a => a.answer);
            Assert.Equal(applied.sent ? SatoraWithdrawalStatus.Sent : SatoraWithdrawalStatus.Failed,
                (await Withdrawals.FindAsync("concurrent", key))!.Status);
        }
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TheOwnerResolvesOnlyAnUnknownOrInterruptedWithdrawalOfTheirStore()
    {
        var unknown = NewKey();
        var running = NewKey();
        var interrupted = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("resolve", unknown, status: SatoraWithdrawalStatus.Unknown));
        await Withdrawals.TryAddAsync(Withdrawal("resolve", running));
        await Withdrawals.TryAddAsync(Withdrawal("resolve", interrupted, createdAt: Now - SatoraWithdrawals.InterruptedAfter));

        Assert.False(await Withdrawals.ResolveAsync("resolve-other", unknown, sent: true, running: false, Now));
        Assert.False(await Withdrawals.ResolveAsync("resolve", running, sent: true, running: false, Now));
        Assert.False(await Withdrawals.ResolveAsync("resolve", interrupted, sent: false, running: true, Now));
        Assert.True(await Withdrawals.ResolveAsync("resolve", unknown, sent: true, running: false, Now));
        Assert.True(await Withdrawals.ResolveAsync("resolve", interrupted, sent: false, running: false, Now));
        Assert.False(await Withdrawals.ResolveAsync("resolve", unknown, sent: false, running: false, Now));

        Assert.Equal(SatoraWithdrawalStatus.Sent, (await Withdrawals.FindAsync("resolve", unknown))!.Status);
        Assert.Equal(SatoraWithdrawalStatus.Failed, (await Withdrawals.FindAsync("resolve", interrupted))!.Status);
        Assert.Equal([running], (await Withdrawals.ListUnresolvedAsync("resolve")).Select(w => w.Id));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AnUnknownWithdrawalOrAnOpenPaymentBlocksAReplace()
    {
        var (_, wallets) = db.Wallets();
        var (refusal, phrase) = await wallets.GenerateAsync("replace-out", null);
        Assert.Equal(SeedWriteRefusal.None, refusal);
        Assert.True(await wallets.ConfirmBackupAsync("replace-out", phrase));
        await Withdrawals.TryAddAsync(Withdrawal("replace-out", NewKey(), status: SatoraWithdrawalStatus.Failed));
        Assert.Equal(0, await wallets.CountOpenOperationsAsync("replace-out"));

        var unknown = NewKey();
        await Withdrawals.TryAddAsync(Withdrawal("replace-out", unknown, status: SatoraWithdrawalStatus.Unknown));
        var payment = Swap("replace-out", "replace-hash");
        await Outgoing.AddAsync(payment);

        Assert.Equal(2, await wallets.CountOpenOperationsAsync("replace-out"));
        Assert.Equal(SeedWriteRefusal.OpenOperations, (await wallets.GenerateAsync("replace-out", SeedWriteGuard.ConfirmationWord)).Refusal);
        Assert.Equal($"Lightning payments: {payment.SwapId}; withdrawals to mark sent or failed on the withdrawals page: {unknown}",
            await wallets.DescribeOpenOperationsAsync("replace-out"));
        await Withdrawals.ResolveAsync("replace-out", unknown, sent: true, running: false, Now);
        Assert.True(await Withdrawals.ClosePaidSwapAsync(payment.SwapId, null, Now));
        Assert.Equal(0, await wallets.CountOpenOperationsAsync("replace-out"));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task OutgoingUpdatesWriteOnlyTheirColumnsOfOutgoingRows()
    {
        var payment = Swap("out-cols", "same-hash");
        var invoice = Swap("out-cols", "same-hash", SatoraLightningDirection.LightningToArkade);
        await Outgoing.AddAsync(payment);
        await new SatoraLightningSwaps(db.Contexts).AddAsync(invoice);

        Assert.True(await Outgoing.SetFundingAttemptedAsync(payment.SwapId));
        await Outgoing.SetStatusAsync(payment.SwapId, SatoraSwapStatus.ServerWontFund);
        await Outgoing.SetFundedAsync(payment.SwapId, "ark-fund");
        Assert.True(await Withdrawals.CloseRefundedSwapAsync(payment.SwapId, "ark-refund", "refunded", Now));
        Assert.False(await Outgoing.SetFundingAttemptedAsync(invoice.SwapId));

        var row = (await Outgoing.FindByPaymentHashAsync("out-cols", "same-hash"))!;
        Assert.True(row is { FundingAttempted: true, Status: SatoraSwapStatus.ServerWontFund, FundingTxId: "ark-fund", RefundTxId: "ark-refund", FundingSats: 12_400 });
        Assert.Equal(Now, row.CompletedAt!.Value, TimeSpan.FromMilliseconds(1));
        Assert.Equal(payment.SwapId, row.SwapId);
        Assert.False((await new SatoraLightningSwaps(db.Contexts).FindAsync("out-cols", invoice.SwapId, Ct))!.FundingAttempted);
        Assert.DoesNotContain("out-cols", await Outgoing.ListOpenStoresAsync(Ct));
        Assert.Empty(await Outgoing.ListOpenAsync("out-cols", Ct));
        Assert.Equal([payment.SwapId], (await Outgoing.ListAsync("out-cols", includePending: false, 0, 10)).Select(s => s.SwapId));
    }

    // A first funding the SDK refused before it recorded anything is undone, and the worker, which only
    // funds a swap again, then leaves it alone. Nothing undoes a funded or closed swap.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task UndoesARefusedFirstFundingAttemptOnlyWhileNothingFundedOrClosedTheSwap()
    {
        var refused = Swap("undo", "undo-1");
        var funded = Swap("undo", "undo-2");
        var closed = Swap("undo", "undo-3");
        foreach (var swap in new[] { refused, funded, closed })
        {
            await Outgoing.AddAsync(swap);
            Assert.False(await Outgoing.SetFundingAttemptedAsync(swap.SwapId, again: true));
            Assert.True(await Outgoing.SetFundingAttemptedAsync(swap.SwapId));
        }
        await Outgoing.SetFundedAsync(funded.SwapId, "ark-fund");
        Assert.True(await Withdrawals.CloseUnfundedSwapAsync(closed.SwapId, "unfunded", Now));

        Assert.True(await Outgoing.ClearFundingAttemptedAsync(refused.SwapId));
        Assert.False(await Outgoing.ClearFundingAttemptedAsync(funded.SwapId));
        Assert.False(await Outgoing.ClearFundingAttemptedAsync(closed.SwapId));

        Assert.False(await Outgoing.SetFundingAttemptedAsync(refused.SwapId, again: true));
        Assert.True(await Outgoing.SetFundingAttemptedAsync(funded.SwapId, again: true));
        var open = (await Outgoing.ListOpenAsync("undo", Ct)).ToDictionary(s => s.SwapId);
        Assert.Equal(new[] { refused.SwapId, funded.SwapId }.Order(), open.Keys.Order());
        Assert.False(open[refused.SwapId].FundingAttempted);
        Assert.True(open[funded.SwapId] is { FundingAttempted: true, FundingTxId: "ark-fund" });
        Assert.True((await Outgoing.FindByPaymentHashAsync("undo", "undo-3"))!.FundingAttempted);
        // A payment of the same invoice marks it attempted afresh.
        Assert.True(await Outgoing.SetFundingAttemptedAsync(refused.SwapId));
    }

    // A Lightning invoice withdrawal paying the invoice again takes over the swap the earlier one
    // recorded, with the link the unique index on SwapId allows one withdrawal, but only from a failed
    // withdrawal and only while the swap is open and not marked attempted. A refused move changes
    // nothing, the earlier link included, and the worker's close then fails the withdrawal that took
    // the swap over.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task MovesASwapToAWithdrawalPayingItsInvoiceAgainOnlyFromAFailedOneWhileNothingFundedOrClosedIt()
    {
        var swap = Swap("move", "move-1");
        var (earlier, retry, third) = (NewKey(), NewKey(), NewKey());
        await Withdrawals.TryAddAsync(Withdrawal("move", earlier, SatoraWithdrawalType.LightningInvoice));
        await Withdrawals.AttachSwapAsync(earlier, swap);
        await Withdrawals.TryAddAsync(Withdrawal("move", retry, SatoraWithdrawalType.LightningInvoice));
        await Withdrawals.TryAddAsync(Withdrawal("move", third, SatoraWithdrawalType.LightningInvoice));

        Assert.False(await Withdrawals.MoveSwapAsync(retry, swap.SwapId));
        await Withdrawals.SetOutcomeAsync(earlier, SatoraWithdrawalStatus.Failed, null, "refused");
        Assert.True(await Outgoing.SetFundingAttemptedAsync(swap.SwapId));
        Assert.False(await Withdrawals.MoveSwapAsync(retry, swap.SwapId));
        Assert.Equal((swap.SwapId, null), await LinksAsync(earlier, retry));

        Assert.True(await Outgoing.ClearFundingAttemptedAsync(swap.SwapId));
        Assert.True(await Withdrawals.MoveSwapAsync(retry, swap.SwapId));
        Assert.Equal((null, swap.SwapId), await LinksAsync(earlier, retry));
        Assert.Equal(retry, (await Withdrawals.FindBySwapIdAsync("move", swap.SwapId))?.Id);
        Assert.False(await Withdrawals.MoveSwapAsync(third, swap.SwapId));

        Assert.True(await Withdrawals.CloseUnattemptedSwapAsync(swap.SwapId, SatoraSwapStatus.Expired, "expired", Now));
        Assert.True(await Withdrawals.FindAsync("move", retry) is { Status: SatoraWithdrawalStatus.Failed, Error: "expired" });
        Assert.False(await Withdrawals.MoveSwapAsync(third, swap.SwapId));
        Assert.Equal((swap.SwapId, null), await LinksAsync(retry, third));
        Assert.Equal(SatoraWithdrawalStatus.Failed, (await Withdrawals.FindAsync("move", earlier))?.Status);

        async Task<(string?, string?)> LinksAsync(string first, string second) =>
            ((await Withdrawals.FindAsync("move", first))?.SwapId, (await Withdrawals.FindAsync("move", second))?.SwapId);
    }

    private static string NewKey() => Guid.NewGuid().ToString("N");

    private static SatoraWithdrawal Withdrawal(string storeId, string id, SatoraWithdrawalType type = SatoraWithdrawalType.Arkade,
        SatoraWithdrawalStatus status = SatoraWithdrawalStatus.Pending, string? swapId = null, DateTimeOffset? createdAt = null) => new()
    {
        Id = id,
        StoreId = storeId,
        Type = type,
        Destination = "destination",
        AmountSats = 1_000,
        Status = status,
        SwapId = swapId,
        // Rows older than the store's current seed would not count against a replace.
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        UpdatedAt = createdAt ?? DateTimeOffset.UtcNow
    };

    private static SatoraLightningSwap Swap(string storeId, string paymentHash,
        SatoraLightningDirection direction = SatoraLightningDirection.ArkadeToLightning) => new()
    {
        SwapId = Guid.NewGuid().ToString(),
        StoreId = storeId,
        Direction = direction,
        Status = SatoraSwapStatus.Pending,
        PaymentHash = paymentHash,
        Bolt11 = "lnbcrt" + paymentHash,
        AmountMsat = 12_345_000,
        FundingSats = 12_400,
        ClaimCutoff = Now.AddDays(21),
        ExpiresAt = Now.AddDays(1),
        CreatedAt = DateTimeOffset.UtcNow
    };
}
