using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BTCPayServer.Plugins.Satora.Lightning;

namespace BTCPayServer.Plugins.Satora.Models;

// An incoming on-chain swap (Bitcoin to Arkade) behind a BTC-SATORA-CHAIN payment prompt. Kept apart
// from SatoraLightningSwap, whose rows are keyed on a payment hash and a BOLT11 these swaps do not
// have, while these need an HTLC address, two locktimes and a refund destination.
public class SatoraChainSwap
{
    [Key]
    public string SwapId { get; set; } = null!;

    // The store whose wallet created the swap and whose seed claims or refunds it.
    public string StoreId { get; set; } = null!;

    // The invoice whose prompt showed the HTLC address. A prompt renewed after a partial payment gets a
    // new swap; this row stays, for a customer who pays it anyway or needs a refund.
    public string InvoiceId { get; set; } = null!;

    // The swap server's status as last read by the worker.
    public SatoraSwapStatus Status { get; set; }

    public string HtlcAddress { get; set; } = null!;

    // What the customer pays into the HTLC: the amount due plus the swap's fee.
    public long DepositSats { get; set; }

    // What the wallet receives on Arkade: the amount due when the prompt was made.
    public long ReceiveSats { get; set; }

    // The server funds the VHTLC only before it, and refunds it after it by median time past.
    public DateTimeOffset VhtlcRefundLocktime { get; set; }

    // From then on, by median time past, the wallet's key alone can spend the HTLC back to the customer.
    public DateTimeOffset BtcRefundLocktime { get; set; }

    // The invoice's expiry when the prompt was made. A customer may pay up to it, so an expired swap's
    // address is read until a little after it.
    public DateTimeOffset InvoiceExpiresAt { get; set; }

    // A payment for the swap is on the invoice (Processing from the funding, or Settled from the claim).
    public bool PaymentRecorded { get; set; }

    // What the funding output paid beyond the deposit, as the latest read before the claim found it. The
    // swap server keeps it; the store hears of it once the claim credits the swap.
    public long OverpaidSats { get; set; }

    public string? ClaimTxId { get; set; }

    // The Settled payment is recorded (or there was no invoice to record it on).
    public bool Credited { get; set; }

    // Set once the customer's bitcoin has to go back. The row stays open until the owner's refund is sent.
    public DateTimeOffset? NeedsRefundAt { get; set; }

    public string? NeedsRefundReason { get; set; }

    public string? RefundAddress { get; set; }

    // Set before the first RefundBitcoinAsync. The address is fixed from then on: the SDK refuses a
    // second destination once one refund exists.
    public bool RefundAttempted { get; set; }

    // The latest refund broadcast. The swap stays open until it confirms and nothing worth refunding
    // is left at the address, as the SDK sweeps outputs it left behind on later calls.
    public string? RefundTxId { get; set; }

    // Why a swap waiting for a refund closed without one going out: its address held nothing worth
    // refunding, or the owner dismissed it.
    public string? ClosedReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Set once the swap needs nothing more from the plugin; the worker only reads rows without it.
    public DateTimeOffset? CompletedAt { get; set; }

    [NotMapped]
    public DateTimeOffset ClaimCutoff => VhtlcRefundLocktime - SatoraClaimWindow.ClaimMargin;
}
