namespace BTCPayServer.Plugins.Satora.Models;

// An alert a worker sends once, recorded before it went out (SatoraOnceAlerts).
public class SatoraSentAlert
{
    public string StoreId { get; set; } = null!;

    // The swap the alert is about; empty for one about the store.
    public string SwapId { get; set; } = null!;

    // The worker, the problem and the status the alert is for, if any: "lightning/UnexpectedStatus/Expired".
    public string Problem { get; set; } = null!;

    public DateTimeOffset SentAt { get; set; }
}
