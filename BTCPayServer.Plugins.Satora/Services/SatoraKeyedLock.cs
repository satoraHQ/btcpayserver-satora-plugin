namespace BTCPayServer.Plugins.Satora.Services;

// One holder at a time per key. A key's entry lives only while someone holds or waits for it, so ids
// that come and go, such as a swap's or an invoice's, leave nothing behind.
public sealed class SatoraKeyedLock
{
    private readonly Dictionary<string, Entry> _entries = new();

    internal int Count
    {
        get
        {
            lock (_entries)
                return _entries.Count;
        }
    }

    public async Task<IDisposable> EnterAsync(string key, CancellationToken cancellationToken = default)
    {
        var entry = Join(key);
        try
        {
            await entry.Gate.WaitAsync(cancellationToken);
        }
        catch
        {
            Leave(key, entry);
            throw;
        }
        return new Holder(this, key, entry);
    }

    // Null while another holds the key.
    public IDisposable? TryEnter(string key)
    {
        var entry = Join(key);
        if (entry.Gate.Wait(0))
            return new Holder(this, key, entry);
        Leave(key, entry);
        return null;
    }

    private Entry Join(string key)
    {
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out var entry))
                _entries[key] = entry = new Entry();
            entry.Users++;
            return entry;
        }
    }

    private void Leave(string key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.Users == 0)
                _entries.Remove(key);
        }
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int Users { get; set; }
    }

    private sealed class Holder(SatoraKeyedLock owner, string key, Entry entry) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1)
                return;
            entry.Gate.Release();
            owner.Leave(key, entry);
        }
    }
}
