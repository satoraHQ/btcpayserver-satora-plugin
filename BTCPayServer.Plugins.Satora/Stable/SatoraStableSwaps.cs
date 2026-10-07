using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora.Stable;

// Incoming stablecoin swaps. Updates name their columns, so the worker's writes and the owner's sweep
// request never overwrite each other, and a write that depends on the swap's state applies only to the
// state its writer read. The unguarded setters are the worker's alone, which runs one tick per store at
// a time in one BTCPay process per database. What the worker uses is virtual, so its tests can keep
// the rows in memory.
public class SatoraStableSwaps(SatoraPluginDbContextFactory dbContextFactory)
{
    public async Task AddAsync(SatoraStableSwap swap)
    {
        await using var ctx = dbContextFactory.CreateContext();
        ctx.SatoraStableSwaps.Add(swap);
        await ctx.SaveChangesAsync();
    }

    public virtual async Task<SatoraStableSwap?> FindAsync(string storeId, string swapId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps.AsNoTracking().FirstOrDefaultAsync(s => s.StoreId == storeId && s.SwapId == swapId);
    }

    public virtual async Task<List<SatoraStableSwap>> ListNeedingAttentionAsync(string storeId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.NeedsAttentionAt != null && s.CompletedAt == null)
            .OrderBy(s => s.NeedsAttentionAt)
            .ToListAsync();
    }

    public async Task<List<string>> ListOpenStoresAsync(CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps.Where(s => s.CompletedAt == null).Select(s => s.StoreId).Distinct().ToListAsync(cancellationToken);
    }

    // How long the VHTLC of the last swap made for the method since then had, refused swaps included:
    // what the swap server gives a swap.
    public virtual async Task<TimeSpan?> LastVhtlcWindowAsync(string paymentMethodId, DateTimeOffset since)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var last = await ctx.SatoraStableSwaps.AsNoTracking()
            .Where(s => s.PaymentMethodId == paymentMethodId && s.CreatedAt >= since)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new { s.VhtlcRefundLocktime, s.CreatedAt })
            .FirstOrDefaultAsync();
        return last is null ? null : last.VhtlcRefundLocktime - last.CreatedAt;
    }

    public virtual async Task<List<SatoraStableSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps.AsNoTracking().Where(s => s.StoreId == storeId && s.CompletedAt == null).ToListAsync(cancellationToken);
    }

    public virtual Task SetStatusAsync(string swapId, SatoraSwapStatus status) =>
        Update(swapId, u => u.SetProperty(s => s.Status, status));

    public virtual Task SetPaymentRecordedAsync(string swapId, bool recorded) =>
        Update(swapId, u => u.SetProperty(s => s.PaymentRecorded, recorded));

    public virtual Task SetCreditedAsync(string swapId) =>
        Update(swapId, u => u.SetProperty(s => s.Credited, true).SetProperty(s => s.PaymentRecorded, true));

    // Before every relay funding call, keeping the first attempt's time. False once the swap closed, went
    // to the owner or passed its funding cutoff: no new signature may then commit its deposit.
    public virtual async Task<bool> MarkFundingAttemptedAsync(string swapId, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.SwapId == swapId && s.CompletedAt == null && s.NeedsAttentionAt == null && s.FundingStartCutoff >= now)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FundingAttemptedAt, s => s.FundingAttemptedAt ?? now)) > 0;
    }

    // Once: a reconcile can report the same funding again.
    public virtual async Task<bool> SetFundedAsync(string swapId, string txHash)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.SwapId == swapId && s.FundTxHash == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FundTxHash, txHash)) > 0;
    }

    // The wallet's own claim receipt and what the VHTLC held, once.
    public virtual async Task<bool> SetClaimedAsync(string swapId, string claimTxId, long vhtlcSats)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.SwapId == swapId && s.ClaimTxId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimTxId, claimTxId).SetProperty(s => s.VhtlcSats, vhtlcSats)) > 0;
    }

    // Only for a swap still open, not yet waiting for the owner and with the claim the worker read:
    // false when any of that changed since.
    public virtual async Task<bool> SetNeedsAttentionAsync(string swapId, string? claimTxId, SatoraStableAttention reason, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.SwapId == swapId && s.NeedsAttentionAt == null && s.CompletedAt == null && s.ClaimTxId == claimTxId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NeedsAttentionAt, now).SetProperty(s => s.AttentionReason, reason)) > 0;
    }

    // The owner's sweep destination, and token for a wrong-token deposit, which can change until the
    // worker first tries the sweep.
    public virtual async Task<bool> RequestSweepAsync(string storeId, string swapId, string destination, string? token)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.StoreId == storeId && s.SwapId == swapId && s.AttentionReason == SatoraStableAttention.Sweep &&
                        s.NeedsAttentionAt != null && s.CompletedAt == null && !s.SweepAttempted)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SweepDestination, destination).SetProperty(s => s.SweepToken, token)) > 0;
    }

    // Fixes the destination and token the worker read: false when the owner changed either since, and
    // the worker tries again with the new ones on its next tick.
    public virtual async Task<bool> TryStartSweepAsync(string swapId, string destination, string? token)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.SwapId == swapId && s.SweepDestination == destination && s.SweepToken == token &&
                        s.SweepTxHash == null && s.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.SweepAttempted, true)) > 0;
    }

    public virtual Task SetSweepTxHashAsync(string swapId, string txHash) =>
        Update(swapId, u => u.SetProperty(s => s.SweepTxHash, txHash));

    // False for a swap closed since, as by the owner's dismissal.
    public virtual async Task<bool> SetClosedAsync(string swapId, string reason, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.SwapId == swapId && s.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClosedReason, reason).SetProperty(s => s.CompletedAt, now)) > 0;
    }

    // False for a swap closed since.
    public virtual async Task<bool> SetCompletedAsync(string swapId, DateTimeOffset completedAt)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraStableSwaps
            .Where(s => s.SwapId == swapId && s.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.CompletedAt, completedAt)) > 0;
    }

    private async Task Update(string swapId, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<SatoraStableSwap>> setters)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.SatoraStableSwaps.Where(s => s.SwapId == swapId).ExecuteUpdateAsync(setters);
    }
}
