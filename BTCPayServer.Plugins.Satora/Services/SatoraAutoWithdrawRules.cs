using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace BTCPayServer.Plugins.Satora.Services;

// The SatoraAutoWithdrawRules table. Virtual so the withdrawal and worker tests can keep the rules in
// memory.
public class SatoraAutoWithdrawRules(SatoraPluginDbContextFactory dbContextFactory)
{
    public virtual async Task<SatoraAutoWithdrawRule?> GetAsync(string storeId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraAutoWithdrawRules.AsNoTracking().FirstOrDefaultAsync(r => r.StoreId == storeId);
    }

    // Stores rule, enabled, in place of the store's rule at expectedVersion, or as its first rule when
    // expectedVersion is null. Not saved when the stored rule is no longer the one the form showed.
    // Previous is the rule it replaced.
    public virtual async Task<(bool Saved, SatoraAutoWithdrawRule? Previous)> SaveAsync(SatoraAutoWithdrawRule rule, int? expectedVersion,
        DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        if (expectedVersion is not { } version)
        {
            ctx.SatoraAutoWithdrawRules.Add(new SatoraAutoWithdrawRule
            {
                StoreId = rule.StoreId,
                Enabled = true,
                DestinationType = rule.DestinationType,
                Destination = rule.Destination,
                ThresholdSats = rule.ThresholdSats,
                ReserveSats = rule.ReserveSats,
                MaxFeeBps = rule.MaxFeeBps,
                Version = 1,
                UpdatedAt = now
            });
            try
            {
                await ctx.SaveChangesAsync();
                return (true, null);
            }
            catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                return (false, null);
            }
        }
        var previous = await ctx.SatoraAutoWithdrawRules.AsNoTracking().FirstOrDefaultAsync(r => r.StoreId == rule.StoreId && r.Version == version);
        if (previous is null)
            return (false, null);
        var saved = await ctx.SatoraAutoWithdrawRules
            .Where(r => r.StoreId == rule.StoreId && r.Version == version)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Enabled, true)
                .SetProperty(r => r.DestinationType, rule.DestinationType)
                .SetProperty(r => r.Destination, rule.Destination)
                .SetProperty(r => r.ThresholdSats, rule.ThresholdSats)
                .SetProperty(r => r.ReserveSats, rule.ReserveSats)
                .SetProperty(r => r.MaxFeeBps, rule.MaxFeeBps)
                .SetProperty(r => r.Version, version + 1)
                .SetProperty(r => r.UpdatedAt, now)) == 1;
        return saved ? (true, previous) : (false, null);
    }

    // Whatever version the rule is at: turning it off only ever stops funds leaving the wallet. False
    // when it was off already.
    public virtual async Task<bool> DisableAsync(string storeId, DateTimeOffset now)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraAutoWithdrawRules
            .Where(r => r.StoreId == storeId && r.Enabled)
            .ExecuteUpdateAsync(u => u
                .SetProperty(r => r.Enabled, false)
                .SetProperty(r => r.Version, r => r.Version + 1)
                .SetProperty(r => r.UpdatedAt, now)) == 1;
    }
}
