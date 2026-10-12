using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora.Lightning;

// Outgoing Lightning swaps (Arkade to Lightning): payments through BTCPay and Lightning withdrawals.
// Updates name their columns, as in SatoraLightningSwaps. Every close is in SatoraWithdrawals, with
// the withdrawal's outcome and only while the swap is open; the updates here record what stays true
// after a close (the server's status, a funding or refund txid), so they apply to a closed swap too.
// Virtual so the payment and worker tests can keep the rows in memory.
public class SatoraOutgoingSwaps(SatoraPluginDbContextFactory dbContextFactory)
{
    private const SatoraLightningDirection Outgoing = SatoraLightningDirection.ArkadeToLightning;

    // The unique index on (StoreId, Direction, PaymentHash) refuses a second row for an invoice.
    public virtual async Task AddAsync(SatoraLightningSwap swap)
    {
        await using var ctx = dbContextFactory.CreateContext();
        ctx.SatoraLightningSwaps.Add(swap);
        await ctx.SaveChangesAsync();
    }

    public virtual async Task<SatoraLightningSwap?> FindByPaymentHashAsync(string storeId, string paymentHash)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Payments(ctx, storeId).FirstOrDefaultAsync(s => s.PaymentHash == paymentHash);
    }

    public virtual async Task<List<SatoraLightningSwap>> ListAsync(string storeId, bool includePending, int offset, int limit)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var swaps = Payments(ctx, storeId);
        if (!includePending)
            swaps = swaps.Where(s => s.CompletedAt != null);
        return await swaps.OrderByDescending(s => s.CreatedAt).Skip(offset).Take(limit).ToListAsync();
    }

    public virtual async Task<List<string>> ListOpenStoresAsync(CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraLightningSwaps
            .Where(s => s.Direction == Outgoing && s.CompletedAt == null)
            .Select(s => s.StoreId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public virtual async Task<List<SatoraLightningSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Payments(ctx, storeId).Where(s => s.CompletedAt == null).ToListAsync(cancellationToken);
    }

    // False when the swap was closed since it was read: nothing may fund it then. again is for the
    // worker, which only funds a swap again: false too once its first attempt was undone.
    public virtual async Task<bool> SetFundingAttemptedAsync(string swapId, bool again = false)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraLightningSwaps
            .Where(s => s.SwapId == swapId && s.Direction == Outgoing && s.CompletedAt == null && (!again || s.FundingAttempted))
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FundingAttempted, true)) == 1;
    }

    // Undoes a first funding attempt the SDK refused before it recorded or sent anything, so nothing
    // paid the VHTLC: the swap is unattempted again, as if the payment had been refused before it.
    // False when the swap was funded or closed since.
    public virtual async Task<bool> ClearFundingAttemptedAsync(string swapId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraLightningSwaps
            .Where(s => s.SwapId == swapId && s.Direction == Outgoing && s.CompletedAt == null && s.FundingTxId == null)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.FundingAttempted, false)) == 1;
    }

    public virtual Task SetFundedAsync(string swapId, string fundingTxId) =>
        Update(swapId, u => u.SetProperty(s => s.FundingTxId, fundingTxId));

    public virtual Task SetStatusAsync(string swapId, SatoraSwapStatus status) =>
        Update(swapId, u => u.SetProperty(s => s.Status, status));

    // A refund the wallet made without the swap server: the swap stays open until the server confirms
    // a refund state or the store closes it.
    public virtual Task SetRefundTxIdAsync(string swapId, string refundTxId) =>
        Update(swapId, u => u.SetProperty(s => s.RefundTxId, refundTxId));

    private async Task Update(string swapId, Action<Microsoft.EntityFrameworkCore.Query.UpdateSettersBuilder<SatoraLightningSwap>> setters)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.SatoraLightningSwaps.Where(s => s.SwapId == swapId && s.Direction == Outgoing).ExecuteUpdateAsync(setters);
    }

    private static IQueryable<SatoraLightningSwap> Payments(SatoraPluginDbContext ctx, string storeId) =>
        ctx.SatoraLightningSwaps.AsNoTracking().Where(s => s.StoreId == storeId && s.Direction == Outgoing);
}
