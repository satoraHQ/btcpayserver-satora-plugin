using BTCPayServer.Plugins.Satora.Data;
using Microsoft.EntityFrameworkCore;

namespace BTCPayServer.Plugins.Satora.Services;

// The once-only alerts the workers have sent (SatoraOnceAlerts). Virtual, so the workers' tests can
// keep them in memory.
public class SatoraSentAlerts(SatoraPluginDbContextFactory dbContextFactory)
{
    // True when this call recorded the alert, false when it was recorded before.
    public virtual async Task<bool> TryAddAsync(string storeId, string swapId, string problem, DateTimeOffset sentAt)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO "BTCPayServer.Plugins.Satora"."SatoraSentAlerts" ("StoreId", "SwapId", "Problem", "SentAt") VALUES ({storeId}, {swapId}, {problem}, {sentAt}) ON CONFLICT DO NOTHING""") == 1;
    }

    public virtual async Task RemoveAsync(string storeId, string swapId, string problemPrefix)
    {
        await using var ctx = dbContextFactory.CreateContext();
        await ctx.SatoraSentAlerts.Where(a => a.StoreId == storeId && a.SwapId == swapId && a.Problem.StartsWith(problemPrefix)).ExecuteDeleteAsync();
    }
}
