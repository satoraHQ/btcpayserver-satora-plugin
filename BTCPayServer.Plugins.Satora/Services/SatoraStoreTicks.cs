using System.Collections.Concurrent;

namespace BTCPayServer.Plugins.Satora.Services;

// The ticks a worker runs on their own tasks, one at a time per store, kept so the worker's StopAsync
// can wait for them before BTCPay disposes the wallets they use.
public sealed class SatoraStoreTicks
{
    // A tick that is only recording what it found ends well within this. One inside a native call,
    // which nothing can interrupt, is left to the process exit: BTCPay's hosted services share one
    // shutdown timeout, and the workers stop one after another.
    public static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<string, Task> _running = new();
    private readonly TimeSpan _stopWait;

    public SatoraStoreTicks() : this(StopWait)
    {
    }

    // Tests that hold a tick open pass a longer wait, so the wait cannot end before the tick does.
    internal SatoraStoreTicks(TimeSpan stopWait) => _stopWait = stopWait;

    // False while the store's previous tick is still running.
    public bool TryStart(string storeId, Func<Task> tick)
    {
        var start = new Task<Task>(async () =>
        {
            try
            {
                await tick();
            }
            finally
            {
                _running.TryRemove(storeId, out _);
            }
        });
        if (!_running.TryAdd(storeId, start.Unwrap()))
            return false;
        start.Start(TaskScheduler.Default);
        return true;
    }

    // Never throws: a tick that failed while the worker stopped has nothing left to report.
    public async Task WaitAsync(CancellationToken cancellationToken) =>
        await Task.WhenAll(_running.Values).WaitAsync(_stopWait, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
}
