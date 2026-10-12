using BTCPayServer.Plugins.Satora.Services;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraStoreTicksTests
{
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(500);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RunsOneTickAtATimePerStore()
    {
        var ticks = new SatoraStoreTicks();
        var finish = new TaskCompletionSource();

        Assert.True(ticks.TryStart("store", () => finish.Task));
        Assert.False(ticks.TryStart("store", () => Task.CompletedTask));
        Assert.True(ticks.TryStart("other", () => Task.CompletedTask));
        finish.SetResult();
        await ticks.WaitAsync(Ct);

        Assert.True(ticks.TryStart("store", () => Task.CompletedTask));
    }

    // Without a stop wait only the tick's end ends the wait, however long the delay runs under load.
    [Fact]
    public async Task WaitsForATickStillRunning()
    {
        var ticks = new SatoraStoreTicks(Timeout.InfiniteTimeSpan);
        var finish = new TaskCompletionSource();
        ticks.TryStart("store", () => finish.Task);

        var stopped = ticks.WaitAsync(Ct);
        await Task.Delay(Blocked, Ct);
        Assert.False(stopped.IsCompleted);
        finish.SetResult();

        await stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    // As a tick inside a native call, which nothing can interrupt.
    [Fact]
    public async Task StopsWaitingForATickThatDoesNotEnd()
    {
        var ticks = new SatoraStoreTicks();
        ticks.TryStart("store", () => new TaskCompletionSource().Task);

        await ticks.WaitAsync(Ct).WaitAsync(SatoraStoreTicks.StopWait + TimeSpan.FromSeconds(10), Ct);
    }

    // As a call on a client BTCPay disposed while the worker stopped.
    [Fact]
    public async Task ATickThatFailedDoesNotFailTheWait()
    {
        var ticks = new SatoraStoreTicks();
        var fail = new TaskCompletionSource();
        ticks.TryStart("store", async () =>
        {
            await fail.Task;
            throw new ObjectDisposedException("client");
        });

        var stopped = ticks.WaitAsync(Ct);
        fail.SetResult();

        await stopped.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }
}
