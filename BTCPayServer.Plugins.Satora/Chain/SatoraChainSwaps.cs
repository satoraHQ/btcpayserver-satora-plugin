using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora.Chain;

// Incoming on-chain swaps. Updates name their columns, so the worker's writes and the owner's refund
// request never overwrite each other, and a close applies only to a swap still open, so the worker's
// close never overwrites the owner's dismissal. What the worker uses is virtual, so its tests can keep
// the rows in memory.
public class SatoraChainSwaps(SatoraPluginDbContextFactory dbContextFactory)
{
    // Any refund call may broadcast. The worker holds the swap from just before each one until its
    // outcome is recorded, and the owner's dismissal is decided only while no call holds it, so a swap
    // the owner dismissed is never refunded afterwards. One BTCPay process per database, as the
    // workers' ticks already assume.
    private readonly SatoraKeyedLock _refundHolds = new();

    public Task<IDisposable> HoldForRefundAsync(string swapId, CancellationToken cancellationToken) =>
        _refundHolds.EnterAsync(swapId, cancellationToken);

    // Null while a refund call holds the swap.
    public IDisposable? TryHoldForDismissal(string swapId) => _refundHolds.TryEnter(swapId);

    public async Task AddAsync(SatoraChainSwap swap)
    {
        await using var ctx = dbContextFactory.CreateContext();
        ctx.SatoraChainSwaps.Add(swap);
        await ctx.SaveChangesAsync();
    }

    public virtual async Task<SatoraChainSwap?> FindAsync(string storeId, string swapId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps.AsNoTracking().FirstOrDefaultAsync(s => s.StoreId == storeId && s.SwapId == swapId);
    }

    public virtual async Task<List<SatoraChainSwap>> ListNeedingRefundAsync(string storeId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps.AsNoTracking()
            .Where(s => s.StoreId == storeId && s.NeedsRefundAt != null && s.CompletedAt == null)
            .OrderBy(s => s.NeedsRefundAt)
            .ToListAsync();
    }

    public async Task<List<string>> ListOpenStoresAsync(CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps.Where(s => s.CompletedAt == null).Select(s => s.StoreId).Distinct().ToListAsync(cancellationToken);
    }

    public virtual async Task<List<SatoraChainSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps.AsNoTracking().Where(s => s.StoreId == storeId && s.CompletedAt == null).ToListAsync(cancellationToken);
    }

    // The owner's refund destination, which can change until the worker first tries to refund.
    public virtual async Task<bool> RequestRefundAsync(string storeId, string swapId, string address)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps
            .Where(s => s.StoreId == storeId && s.SwapId == swapId && s.NeedsRefundAt != null && s.CompletedAt == null && !s.RefundAttempted)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RefundAddress, address)) > 0;
    }

    public virtual Task SetStatusAsync(string swapId, SatoraSwapStatus status) =>
        Update(swapId, u => u.SetProperty(s => s.Status, status));

    public virtual Task SetPaymentRecordedAsync(string swapId, bool recorded) =>
        Update(swapId, u => u.SetProperty(s => s.PaymentRecorded, recorded));

    // Only before the claim, which takes the funding as it then was: false once the swap is claimed.
    public virtual async Task<bool> SetOverpaidAsync(string swapId, long overpaidSats)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps
            .Where(s => s.SwapId == swapId && s.ClaimTxId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.OverpaidSats, overpaidSats)) > 0;
    }

    public virtual Task SetClaimedAsync(string swapId, string claimTxId) =>
        Update(swapId, u => u.SetProperty(s => s.ClaimTxId, claimTxId));

    public virtual Task SetCreditedAsync(string swapId) =>
        Update(swapId, u => u.SetProperty(s => s.Credited, true).SetProperty(s => s.PaymentRecorded, true));

    // Only for a swap still open, not yet waiting for a refund and with the claim the worker read:
    // false when any of that changed since.
    public virtual async Task<bool> SetNeedsRefundAsync(string swapId, string? claimTxId, string reason, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps
            .Where(s => s.SwapId == swapId && s.NeedsRefundAt == null && s.CompletedAt == null && s.ClaimTxId == claimTxId)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.NeedsRefundAt, now).SetProperty(s => s.NeedsRefundReason, reason)) > 0;
    }

    // False for a swap closed since, as by the owner's dismissal.
    public virtual async Task<bool> SetCompletedAsync(string swapId, DateTimeOffset completedAt)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps
            .Where(s => s.SwapId == swapId && s.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.CompletedAt, completedAt)) > 0;
    }

    // Fixes the destination the worker read: false when the owner changed it since, and the worker
    // tries again with the new one on its next tick.
    public virtual async Task<bool> TryStartRefundAsync(string swapId, string address)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps
            .Where(s => s.SwapId == swapId && s.RefundAddress == address && s.RefundTxId == null && s.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.RefundAttempted, true)) > 0;
    }

    public virtual Task SetRefundTxIdAsync(string swapId, string refundTxId) =>
        Update(swapId, u => u.SetProperty(s => s.RefundTxId, refundTxId));

    // False for a swap closed since, as by the owner's dismissal.
    public virtual async Task<bool> SetClosedAsync(string swapId, string reason, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps
            .Where(s => s.SwapId == swapId && s.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClosedReason, reason).SetProperty(s => s.CompletedAt, now)) > 0;
    }

    // The owner's decision not to refund: the worker stops following the swap, whatever it was doing.
    public virtual async Task<bool> DismissRefundAsync(string storeId, string swapId, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraChainSwaps
            .Where(s => s.StoreId == storeId && s.SwapId == swapId && s.NeedsRefundAt != null && s.CompletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClosedReason, SatoraChainRefunds.DismissedReason).SetProperty(s => s.CompletedAt, now)) > 0;
    }

    private async Task Update(string swapId, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<SatoraChainSwap>> setters)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.SatoraChainSwaps.Where(s => s.SwapId == swapId).ExecuteUpdateAsync(setters);
    }
}
