using BTCPayServer.Plugins.Satora.Services;

namespace BTCPayServer.Plugins.Satora.Tests;

// The workers' sent alerts in memory. Two workers given the same instance stand for one BTCPay
// process and the next, after a restart, on the same database.
public sealed class MemorySentAlerts() : SatoraSentAlerts(null!)
{
    public HashSet<(string StoreId, string SwapId, string Problem)> Rows { get; } = [];

    // Thrown by every alert recorded while set, as by a database that is down.
    public Exception? AddError { get; set; }

    public override Task<bool> TryAddAsync(string storeId, string swapId, string problem, DateTimeOffset sentAt) =>
        AddError is not null ? throw AddError : Task.FromResult(Rows.Add((storeId, swapId, problem)));

    public override Task RemoveAsync(string storeId, string swapId, string problemPrefix)
    {
        Rows.RemoveWhere(r => r.StoreId == storeId && r.SwapId == swapId && r.Problem.StartsWith(problemPrefix, StringComparison.Ordinal));
        return Task.CompletedTask;
    }
}
