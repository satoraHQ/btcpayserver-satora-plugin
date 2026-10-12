using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.Satora.Services;

// The alerts one worker sends once per swap, or once per outage of a store. Each is recorded in the
// database before it goes out, so a BTCPay restart does not send it again: one whose sending then
// fails is lost rather than sent twice. What this process learned spares a database round trip on
// every tick that finds the problem.
public sealed class SatoraOnceAlerts(SatoraSentAlerts sent, string worker, ILogger logger)
{
    // True once sent, false once known not to be; the database is asked only for neither.
    private readonly ConcurrentDictionary<(string StoreId, string SwapId, string Problem), bool> _known = new();

    // True for the one call that is to send the alert, which status, when given, narrows to that
    // status. One that cannot be recorded is not sent, and a later call tries again.
    public async Task<bool> TryStartAsync(string storeId, string? swapId, Enum problem, object? status = null)
    {
        var key = Key(storeId, swapId, problem, status);
        if (_known.GetValueOrDefault(key))
            return false;
        try
        {
            var first = await sent.TryAddAsync(key.StoreId, key.SwapId, key.Problem, DateTimeOffset.UtcNow);
            _known[key] = true;
            return first;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record the Satora alert {Problem} about {SwapId} of store {StoreId}; it is sent once it can be",
                key.Problem, swapId, storeId);
            return false;
        }
    }

    // Only what this process has seen: after a restart, false until the alert comes up again.
    public bool KnownSent(string storeId, string? swapId, Enum problem) => _known.GetValueOrDefault(Key(storeId, swapId, problem, null));

    // The store's alert goes out again next time, as once the outage it told of has ended.
    public async Task ForgetAsync(string storeId, Enum problem)
    {
        var key = Key(storeId, null, problem, null);
        if (_known.TryGetValue(key, out var known) && !known)
            return;
        try
        {
            await sent.RemoveAsync(key.StoreId, key.SwapId, key.Problem);
            _known[key] = false;
        }
        catch (Exception e)
        {
            _known.TryRemove(key, out _);
            logger.LogWarning(e, "Could not clear the Satora alert {Problem} of store {StoreId}", key.Problem, storeId);
        }
    }

    // A swap the worker closed: if anything reopens it, its alerts go out again.
    public async Task ForgetSwapAsync(string storeId, string swapId)
    {
        foreach (var key in _known.Keys.Where(k => k.StoreId == storeId && k.SwapId == swapId))
            _known.TryRemove(key, out _);
        try
        {
            await sent.RemoveAsync(storeId, swapId, worker + "/");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not clear the Satora alerts about {SwapId} of store {StoreId}", swapId, storeId);
        }
    }

    private (string StoreId, string SwapId, string Problem) Key(string storeId, string? swapId, Enum problem, object? status) =>
        (storeId, swapId ?? "", $"{worker}/{problem}/{status}");
}
