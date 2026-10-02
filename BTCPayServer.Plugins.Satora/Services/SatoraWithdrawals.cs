using System.Linq.Expressions;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Npgsql;

namespace BTCPayServer.Plugins.Satora.Services;

// The SatoraWithdrawals table. Virtual so the withdrawal tests can keep the rows in memory.
public class SatoraWithdrawals(SatoraPluginDbContextFactory dbContextFactory)
{
    // Longer than the slowest withdrawal, an offboard: the SDK caps its quote at 300 s and its exit at
    // 600 s, and each may first wait up to 300 s for a review's quote to release the SDK's wallet lock.
    public static readonly TimeSpan InterruptedAfter = TimeSpan.FromMinutes(30);

    private const string MarkedFailed = "Marked failed by the store after checking the wallet.";

    // Unknown, or Pending without a swap for the worker to resolve: only the owner can tell whether
    // its funds left. While one exists the store takes no new withdrawal.
    public static bool IsUnresolved(SatoraWithdrawal withdrawal) =>
        withdrawal.Status == SatoraWithdrawalStatus.Unknown ||
        withdrawal is { Status: SatoraWithdrawalStatus.Pending, SwapId: null };

    // The owner can mark it sent or failed once no request can still be working on it: none runs it
    // in this process (running), and a Pending one is older than InterruptedAfter.
    public static bool OwnerCanResolve(SatoraWithdrawal withdrawal, bool running, DateTimeOffset now) =>
        !running && (withdrawal.Status == SatoraWithdrawalStatus.Unknown ||
                     withdrawal is { Status: SatoraWithdrawalStatus.Pending, SwapId: null } && now - withdrawal.CreatedAt >= InterruptedAfter);

    // False when the key was used before: the PRIMARY KEY refuses a second row for it.
    public virtual async Task<bool> TryAddAsync(SatoraWithdrawal withdrawal)
    {
        await using var ctx = dbContextFactory.CreateContext();
        ctx.SatoraWithdrawals.Add(withdrawal);
        try
        {
            await ctx.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    public virtual async Task<SatoraWithdrawal?> FindAsync(string storeId, string id)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraWithdrawals.AsNoTracking().FirstOrDefaultAsync(w => w.StoreId == storeId && w.Id == id);
    }

    public virtual async Task<List<SatoraWithdrawal>> ListAsync(string storeId, int limit)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraWithdrawals.AsNoTracking()
            .Where(w => w.StoreId == storeId)
            .OrderByDescending(w => w.CreatedAt)
            .Take(limit)
            .ToListAsync();
    }

    public virtual async Task<List<SatoraWithdrawal>> ListUnresolvedAsync(string storeId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await Unresolved(ctx.SatoraWithdrawals.AsNoTracking().Where(w => w.StoreId == storeId))
            .OrderBy(w => w.CreatedAt)
            .ToListAsync();
    }

    // The swap row and the withdrawal's link to it are written together, so a withdrawal never
    // points at a swap that is not recorded, nor a recorded swap's withdrawal at none.
    public virtual async Task AttachSwapAsync(string withdrawalId, SatoraLightningSwap swap)
    {
        await using var ctx = dbContextFactory.CreateContext();
        ctx.SatoraLightningSwaps.Add(swap);
        var withdrawal = await ctx.SatoraWithdrawals.SingleAsync(w => w.Id == withdrawalId);
        withdrawal.SwapId = swap.SwapId;
        withdrawal.UpdatedAt = DateTimeOffset.UtcNow;
        await ctx.SaveChangesAsync();
    }

    public virtual Task SetAmountsAsync(string id, long? amountSats, long? feeSats) =>
        Update(id, u => u
            .SetProperty(w => w.AmountSats, amountSats)
            .SetProperty(w => w.FeeSats, feeSats));

    // The request's outcome. Only a Pending row takes it: one the owner or the worker decided keeps
    // their answer.
    public virtual async Task SetOutcomeAsync(string id, SatoraWithdrawalStatus status, string? txId, string? error)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.SatoraWithdrawals
            .Where(w => w.Id == id && w.Status == SatoraWithdrawalStatus.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.TxId, w => txId ?? w.TxId)
                .SetProperty(w => w.Error, error)
                .SetProperty(w => w.UpdatedAt, DateTimeOffset.UtcNow));
    }

    // The worker's close of an outgoing swap nothing funded. False when a payment marked it attempted
    // since it was read, or it is closed already: this close and a funding attempt each apply only to
    // a row the other has not changed, and the withdrawal fails only with the close.
    public virtual Task<bool> CloseUnattemptedSwapAsync(string swapId, SatoraSwapStatus status, string error, DateTimeOffset completedAt) =>
        CloseSwapAsync(
            s => s.SwapId == swapId && s.Direction == SatoraLightningDirection.ArkadeToLightning && !s.FundingAttempted && s.CompletedAt == null,
            u => u.SetProperty(s => s.Status, status).SetProperty(s => s.CompletedAt, completedAt),
            swapId, SatoraWithdrawalStatus.Failed, null, error);

    // The worker's closes of an outgoing swap it saw through: its invoice paid, its funds refunded to
    // the wallet, or nothing funded after all. Each applies only to an open swap, so none overwrites
    // the store's close, and the withdrawal takes the outcome only with the close. False when the
    // swap is closed already.
    public virtual Task<bool> ClosePaidSwapAsync(string swapId, string? fundingTxId, DateTimeOffset paidAt) =>
        CloseSwapAsync(OpenOutgoing(swapId), u => u.SetProperty(s => s.PaidAt, paidAt).SetProperty(s => s.CompletedAt, paidAt),
            swapId, SatoraWithdrawalStatus.Sent, fundingTxId, null);

    public virtual Task<bool> CloseRefundedSwapAsync(string swapId, string refundTxId, string error, DateTimeOffset completedAt) =>
        CloseSwapAsync(OpenOutgoing(swapId), u => u.SetProperty(s => s.RefundTxId, refundTxId).SetProperty(s => s.CompletedAt, completedAt),
            swapId, SatoraWithdrawalStatus.Failed, null, error);

    public virtual Task<bool> CloseUnfundedSwapAsync(string swapId, string error, DateTimeOffset completedAt) =>
        CloseSwapAsync(OpenOutgoing(swapId), u => u.SetProperty(s => s.CompletedAt, completedAt),
            swapId, SatoraWithdrawalStatus.Failed, null, error);

    // The store's close of a Lightning payment the worker cannot finish, with its answer: the invoice
    // was paid, or the funds are back in the wallet. GetPayment then reports it complete or failed,
    // and its withdrawal takes the same answer, only with the close. False when it is closed already.
    public virtual Task<bool> CloseSwapAsync(string storeId, string swapId, bool paid, DateTimeOffset completedAt) =>
        CloseSwapAsync(
            s => s.SwapId == swapId && s.StoreId == storeId && s.Direction == SatoraLightningDirection.ArkadeToLightning && s.CompletedAt == null,
            u =>
            {
                u.SetProperty(s => s.CompletedAt, completedAt);
                if (paid)
                    u.SetProperty(s => s.PaidAt, completedAt);
            },
            swapId, paid ? SatoraWithdrawalStatus.Sent : SatoraWithdrawalStatus.Failed, null, paid ? null : MarkedFailed);

    public virtual async Task<SatoraWithdrawal?> FindBySwapIdAsync(string storeId, string swapId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraWithdrawals.AsNoTracking().FirstOrDefaultAsync(w => w.StoreId == storeId && w.SwapId == swapId);
    }

    // The owner's answer for an unresolved withdrawal. Applies only while the row is as it was read:
    // an outcome written since then stands.
    public virtual async Task<bool> ResolveAsync(string storeId, string id, bool sent, bool running, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var withdrawal = await ctx.SatoraWithdrawals.AsNoTracking().FirstOrDefaultAsync(w => w.StoreId == storeId && w.Id == id);
        if (withdrawal is null || !OwnerCanResolve(withdrawal, running, now))
            return false;
        var error = sent ? withdrawal.Error : MarkedFailed;
        return await ctx.SatoraWithdrawals
            .Where(w => w.Id == id && w.Status == withdrawal.Status && w.SwapId == withdrawal.SwapId)
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, sent ? SatoraWithdrawalStatus.Sent : SatoraWithdrawalStatus.Failed)
                .SetProperty(w => w.Error, error)
                .SetProperty(w => w.UpdatedAt, now)) == 1;
    }

    public static IQueryable<SatoraWithdrawal> Unresolved(IQueryable<SatoraWithdrawal> withdrawals) =>
        withdrawals.Where(w => w.Status == SatoraWithdrawalStatus.Unknown || (w.Status == SatoraWithdrawalStatus.Pending && w.SwapId == null));

    private static Expression<Func<SatoraLightningSwap, bool>> OpenOutgoing(string swapId) =>
        s => s.SwapId == swapId && s.Direction == SatoraLightningDirection.ArkadeToLightning && s.CompletedAt == null;

    // The retrying execution strategy BTCPay configures runs a transaction only as a unit it can retry.
    private async Task<bool> CloseSwapAsync(Expression<Func<SatoraLightningSwap, bool>> swap, Action<UpdateSettersBuilder<SatoraLightningSwap>> close,
        string swapId, SatoraWithdrawalStatus status, string? txId, string? error)
    {
        await using var strategyCtx = dbContextFactory.CreateContext();
        return await strategyCtx.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var ctx = dbContextFactory.CreateContext();
            await using var transaction = await ctx.Database.BeginTransactionAsync();
            if (await ctx.SatoraLightningSwaps.Where(swap).ExecuteUpdateAsync(close) != 1)
                return false;
            await SetSwapOutcome(ctx, swapId, status, txId, error);
            await transaction.CommitAsync();
            return true;
        });
    }

    // A withdrawal already decided keeps its status.
    private static Task<int> SetSwapOutcome(SatoraPluginDbContext ctx, string swapId, SatoraWithdrawalStatus status, string? txId, string? error) =>
        ctx.SatoraWithdrawals
            .Where(w => w.SwapId == swapId && w.Status == SatoraWithdrawalStatus.Pending)
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, status)
                .SetProperty(w => w.TxId, w => txId ?? w.TxId)
                .SetProperty(w => w.Error, error)
                .SetProperty(w => w.UpdatedAt, DateTimeOffset.UtcNow));

    private async Task Update(string id, Action<UpdateSettersBuilder<SatoraWithdrawal>> setters)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.SatoraWithdrawals.Where(w => w.Id == id).ExecuteUpdateAsync(u =>
        {
            setters(u);
            u.SetProperty(w => w.UpdatedAt, DateTimeOffset.UtcNow);
        });
    }
}
