using BTCPayServer.Plugins.Satora.Chain;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraChainRefundPageModel
{
    public string StoreId { get; set; } = null!;
    public List<SatoraChainSwap> Swaps { get; set; } = [];
    // By swap id; null for an address that could not be read.
    public Dictionary<string, IReadOnlyList<SatoraHtlcOutput>?> Outputs { get; set; } = [];
    public DateTimeOffset Now { get; set; }
}

public class SatoraChainRefundRequest
{
    public string? Address { get; set; }
    public string? Confirmation { get; set; }
}
