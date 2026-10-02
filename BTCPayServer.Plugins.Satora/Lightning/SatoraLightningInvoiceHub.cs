using System.Collections.Concurrent;
using System.Threading.Channels;
using BTCPayServer.Lightning;

namespace BTCPayServer.Plugins.Satora.Lightning;

// The worker publishes each invoice it closes without a payment, as Expired, so BTCPay stops waiting
// for it; a paid one it records itself. Every open Listen session of that store gets it. BTCPay keeps
// one listener per connection string, but a key rotation can leave two.
public class SatoraLightningInvoiceHub
{
    private readonly ConcurrentDictionary<Listener, string> _listeners = new();

    public ILightningInvoiceListener Subscribe(string storeId)
    {
        var listener = new Listener(this);
        _listeners.TryAdd(listener, storeId);
        return listener;
    }

    public void Publish(string storeId, LightningInvoice invoice)
    {
        foreach (var (listener, listenerStoreId) in _listeners)
        {
            if (listenerStoreId == storeId)
                listener.Channel.Writer.TryWrite(invoice);
        }
    }

    private sealed class Listener(SatoraLightningInvoiceHub hub) : ILightningInvoiceListener
    {
        // A session that stops reading without being disposed loses its oldest notifications, not memory.
        public Channel<LightningInvoice> Channel { get; } = System.Threading.Channels.Channel.CreateBounded<LightningInvoice>(
            new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest });

        public async Task<LightningInvoice> WaitInvoice(CancellationToken cancellation) =>
            await Channel.Reader.ReadAsync(cancellation);

        public void Dispose()
        {
            hub._listeners.TryRemove(this, out _);
            Channel.Writer.TryComplete();
        }
    }
}
