using BTCPayServer.Plugins.Satora.Services;

namespace BTCPayServer.Plugins.Satora.Tests;

// The workers' sent alerts in memory. Two workers given the same instance stand for one BTCPay
// process and the next, after a restart, on the same database.
public sealed class MemorySentAlerts() : SatoraSentAlerts(null!)
{
    public HashSet<(string StoreId, string SwapId, string Problem)> Rows { get; } = [];

    public override Task<bool> TryAddAsync(string storeId, string swapId, string problem, DateTimeOffset sentAt) =>
        Task.FromResult(Rows.Add((storeId, swapId, problem)));

    public override Task RemoveAsync(string storeId, string swapId, string problemPrefix)
    {
        Rows.RemoveWhere(r => r.StoreId == storeId && r.SwapId == swapId && r.Problem.StartsWith(problemPrefix, StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
