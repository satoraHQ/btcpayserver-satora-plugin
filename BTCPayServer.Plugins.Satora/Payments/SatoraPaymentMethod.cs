using BTCPayServer.Payments;
using BTCPayServer.Payouts;

namespace BTCPayServer.Plugins.Satora.Payments;

public static class SatoraPaymentMethod
{
    public const string Id = "Satora";
    public const string Currency = "BTC";

    public static readonly PaymentMethodId PaymentMethodId = PaymentMethodId.Parse(Id);
    public static readonly PayoutMethodId PayoutMethodId = PayoutMethodId.Parse(Id);
}
