using BTCPayServer.Plugins.Satora.Services;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraKeyedLockTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly SatoraKeyedLock _lock = new();

    [Fact]
    public async Task OneHolderAtATimePerKey()
    {
        var first = await _lock.EnterAsync("a", Ct);
        Assert.Null(_lock.TryEnter("a"));
        using (var other = _lock.TryEnter("b"))
            Assert.NotNull(other);
        var waiting = _lock.EnterAsync("a", Ct);
        Assert.False(waiting.IsCompleted);

        first.Dispose();
        first.Dispose();

        using (await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct))
            Assert.Null(_lock.TryEnter("a"));
        using var again = _lock.TryEnter("a");
        Assert.NotNull(again);
    }

    // A wait given up leaves the key to the next one, and no entry once the holder lets go.
    [Fact]
    public async Task AWaitGivenUpDoesNotHoldTheKey()
    {
        var holder = _lock.TryEnter("a")!;
        using var cancel = new CancellationTokenSource();
        var waiting = _lock.EnterAsync("a", cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        holder.Dispose();

        Assert.Equal(0, _lock.Count);
        using var next = _lock.TryEnter("a");
        Assert.NotNull(next);
    }

    // Swap and invoice ids pass through once each, so their entries must not pile up.
    [Fact]
    public async Task AKeyNobodyHoldsOrWaitsForLeavesNoEntry()
    {
        (await _lock.EnterAsync("a", Ct)).Dispose();
        Assert.Equal(0, _lock.Count);

        var holder = _lock.TryEnter("a")!;
        Assert.Null(_lock.TryEnter("a"));
        holder.Dispose();
        Assert.Equal(0, _lock.Count);

        holder = _lock.TryEnter("a")!;
        var waiting = _lock.EnterAsync("a", Ct);
        holder.Dispose();
        (await waiting.WaitAsync(TimeSpan.FromSeconds(10), Ct)).Dispose();
        Assert.Equal(0, _lock.Count);
    }
}
