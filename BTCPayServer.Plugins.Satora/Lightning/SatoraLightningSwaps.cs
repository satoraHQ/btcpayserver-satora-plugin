using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora.Lightning;

// Incoming Lightning swaps (Lightning to Arkade). Updates name their columns, so the worker's status
// writes and CancelInvoice's do-not-claim never overwrite each other. What the worker uses is virtual,
// so its tests can keep the rows in memory.
public class SatoraLightningSwaps(SatoraPluginDbContextFactory dbContextFactory)
{
    private const SatoraLightningDirection Incoming = SatoraLightningDirection.LightningToArkade;

    public async Task AddAsync(SatoraLightningSwap swap)
    {
        await using var ctx = dbContextFactory.CreateContext();
        ctx.SatoraLightningSwaps.Add(swap);
        await ctx.SaveChangesAsync();
    }

    public async Task<SatoraLightningSwap?> FindAsync(string storeId, string swapId, CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Invoices(ctx, storeId).FirstOrDefaultAsync(s => s.SwapId == swapId, cancellationToken);
    }

    public async Task<SatoraLightningSwap?> FindByPaymentHashAsync(string storeId, string paymentHash, CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Invoices(ctx, storeId).FirstOrDefaultAsync(s => s.PaymentHash == paymentHash, cancellationToken);
    }

    // Pending: not paid, and not past the point where it could still be claimed.
    public async Task<List<SatoraLightningSwap>> ListAsync(string storeId, bool pendingOnly, DateTimeOffset now, int limit, CancellationToken cancellationToken = default)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var swaps = Invoices(ctx, storeId);
        if (pendingOnly)
            swaps = swaps.Where(s => !s.Credited && s.CompletedAt == null && s.ClaimCutoff > now);
        return await swaps.OrderByDescending(s => s.CreatedAt).Take(limit).ToListAsync(cancellationToken);
    }

    // Against StartClaimAsync: once a claim started, this changes nothing and the claim goes on.
    public async Task<bool> SetDoNotClaimAsync(string storeId, string swapId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Invoices(ctx, storeId)
            .Where(s => s.SwapId == swapId && !s.Credited && s.ClaimStartedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.DoNotClaim, true)) > 0;
    }

    public async Task<List<(string StoreId, DateTimeOffset ClaimCutoff)>> ListOpenAsync(CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var open = await ctx.SatoraLightningSwaps
            .Where(s => s.Direction == Incoming && s.CompletedAt == null)
            .Select(s => new { s.StoreId, s.ClaimCutoff })
            .ToListAsync(cancellationToken);
        return open.Select(s => (s.StoreId, s.ClaimCutoff)).ToList();
    }

    public virtual async Task<List<SatoraLightningSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Invoices(ctx, storeId).Where(s => s.CompletedAt == null).ToListAsync(cancellationToken);
    }

    // The worker's side of the race with CancelInvoice, which can mark a swap after the worker listed it:
    // false for a swap marked DoNotClaim or a row that is gone, which are not claimed. A claim that
    // started stays started, so a retry or a resume past the cutoff passes again.
    public virtual async Task<bool> StartClaimAsync(string storeId, string swapId, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Invoices(ctx, storeId)
            .Where(s => s.SwapId == swapId && !s.DoNotClaim)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.ClaimStartedAt, s => s.ClaimStartedAt ?? now)) > 0;
    }

    public virtual Task SetStatusAsync(string swapId, SatoraSwapStatus status) =>
        Update(swapId, u => u.SetProperty(s => s.Status, status));

    public virtual Task SetClaimedAsync(string swapId, string claimTxId) =>
        Update(swapId, u => u.SetProperty(s => s.ClaimTxId, claimTxId));

    public virtual Task SetCreditedAsync(string swapId, DateTimeOffset paidAt) =>
        Update(swapId, u => u
            .SetProperty(s => s.Credited, true)
            .SetProperty(s => s.PaidAt, paidAt)
            .SetProperty(s => s.CompletedAt, paidAt));

    public virtual Task SetCompletedAsync(string swapId, DateTimeOffset completedAt) =>
        Update(swapId, u => u.SetProperty(s => s.CompletedAt, completedAt));

    private async Task Update(string swapId, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<SatoraLightningSwap>> setters)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.SatoraLightningSwaps.Where(s => s.SwapId == swapId).ExecuteUpdateAsync(setters);
    }

    private static IQueryable<SatoraLightningSwap> Invoices(SatoraPluginDbContext ctx, string storeId) =>
        ctx.SatoraLightningSwaps.AsNoTracking().Where(s => s.StoreId == storeId && s.Direction == Incoming);
}
