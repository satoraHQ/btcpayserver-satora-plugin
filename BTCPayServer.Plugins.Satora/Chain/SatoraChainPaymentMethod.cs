using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;

namespace BTCPayServer.Plugins.Satora.Chain;

// On-chain bitcoin swapped into the store's Arkade wallet. A payment method of its own, so BTCPay's
// BTC-CHAIN method and the store's on-chain wallet are never touched.
public static class SatoraChainPaymentMethod
{
    public const string CryptoCode = "BTC";
    public const string PrettyName = "Bitcoin (Satora)";

    public static readonly PaymentMethodId Id = new("BTC-SATORA-CHAIN");
}

// What the store's payment method config holds while its wallet is enabled. The wallet is the
// plugin's own per-store state, so there is nothing to configure here.
public class SatoraChainPaymentMethodConfig;

public class SatoraChainPromptDetails
{
    public string SwapId { get; set; } = null!;

    // What the swap's HTLC takes. The prompt shows its address only while this is what is due.
    public long DepositSats { get; set; }

    // Read without the handler's serializer, as Newtonsoft matches the stored camelCase names anyway.
    public static SatoraChainPromptDetails? Of(PaymentPrompt? prompt) => prompt?.Details?.ToObject<SatoraChainPromptDetails>();
}

public class SatoraChainPaymentData
{
    public string SwapId { get; set; } = null!;

    public string? ClaimTxId { get; set; }

    // Decided as the payment settled: the invoice's other settled payments had paid it in full by
    // then, so all of this one is beyond what was due.
    public bool AfterPaidInFull { get; set; }
}
