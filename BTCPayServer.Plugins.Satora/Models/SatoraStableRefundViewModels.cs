using BTCPayServer.Plugins.Satora.Stable;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraStableRefundPageModel
{
    public string StoreId { get; set; } = null!;
    public List<SatoraStableSwap> Swaps { get; set; } = [];
    // By swap id, for the swaps waiting for a sweep; null for an address that could not be read.
    public Dictionary<string, SatoraDepositorFunds?> Funds { get; set; } = [];
    public DateTimeOffset Now { get; set; }
}

public class SatoraStableRefundRequest
{
    public string? Destination { get; set; }
    // Another token's contract, for one sent to the address by mistake; empty for the swap's own.
    public string? Token { get; set; }
    public string? Confirmation { get; set; }
}

public class SatoraStableCheckRequest
{
    // A swap id, or an invoice id for every stablecoin swap of the invoice.
    public string? Id { get; set; }
    public string? Token { get; set; }
}
