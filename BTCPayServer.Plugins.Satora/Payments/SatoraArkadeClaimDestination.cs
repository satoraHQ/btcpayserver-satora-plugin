using BTCPayServer.Data;

namespace BTCPayServer.Plugins.Satora.Payments;

public class SatoraArkadeClaimDestination(string address) : IClaimDestination
{
    public string Address { get; } = address;
    public string? Id => Address;
    public decimal? Amount => null;

    public override string ToString() => Address;
}
