using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using BTCPayServer.Plugins.Satora.Lightning;

namespace BTCPayServer.Plugins.Satora.Models;

// An incoming stablecoin swap (a token on an EVM chain to Arkade) behind one of the stablecoin payment
// prompts. The customer sends the token to an address of the swap's own, derived from the store's
// seed; the store's key signs the relay funding from it, claims the VHTLC, and is the only key that
// can move anything left there.
public class SatoraStableSwap
{
    [Key]
    public string SwapId { get; set; } = null!;

    // The store whose wallet created the swap and whose seed funds, claims and sweeps it.
    public string StoreId { get; set; } = null!;

    // The invoice whose prompt showed the deposit address. A renewed prompt gets a new swap; this row
    // stays, for a customer who pays it anyway or needs a sweep.
    public string InvoiceId { get; set; } = null!;

    // The prompt's payment method, one per token and chain.
    public string PaymentMethodId { get; set; } = null!;

    // The EVM chain id, 42161 for Arbitrum One.
    public long ChainId { get; set; }

    // The token's symbol, such as USDC.
    public string Token { get; set; } = null!;

    public string TokenAddress { get; set; } = null!;

    public int Decimals { get; set; }

    public string DepositAddress { get; set; } = null!;

    // What the swap takes from the deposit address, in the token's smallest unit, as a decimal string.
    public string SourceUnits { get; set; } = null!;

    // What the wallet receives on Arkade: the amount due when the prompt was made.
    public long ReceiveSats { get; set; }

    // What the VHTLC the wallet claimed held: less than ReceiveSats when the DEX delivered less than
    // quoted, more when it delivered more. The invoice is credited with it, up to ReceiveSats.
    public long? VhtlcSats { get; set; }

    // The swap server's status as last read by the worker.
    public SatoraSwapStatus Status { get; set; }

    // The server funds the VHTLC only before it, and refunds it after it by median time past.
    public DateTimeOffset VhtlcRefundLocktime { get; set; }

    // From then on anyone can refund the EVM HTLC to the deposit address, without the server (the
    // coordinator's refundTo).
    public DateTimeOffset EvmRefundLocktime { get; set; }

    // No relay funding starts after it, so one that lands at the end of its permit deadline still
    // leaves time for the server to fund the VHTLC and the wallet to claim it.
    public DateTimeOffset FundingStartCutoff { get; set; }

    // The invoice's expiry when the prompt was made.
    public DateTimeOffset InvoiceExpiresAt { get; set; }

    // Set before the first relay funding. The SDK's journal decides whether one may still land; this
    // only says that the deposit may have moved into the swap's HTLC.
    public DateTimeOffset? FundingAttemptedAt { get; set; }

    public string? FundTxHash { get; set; }

    // A payment for the swap is on the invoice (Processing from the funding, or Settled from the claim).
    public bool PaymentRecorded { get; set; }

    public string? ClaimTxId { get; set; }

    // The Settled payment is recorded (or there was no invoice to record it on).
    public bool Credited { get; set; }

    // Set once tokens at the deposit address, or in the swap's HTLC, have to go back. The row stays open
    // until the owner's sweep goes out.
    public DateTimeOffset? NeedsAttentionAt { get; set; }

    public SatoraStableAttention? AttentionReason { get; set; }

    // The token contract to sweep; null for the swap's own token.
    public string? SweepToken { get; set; }

    public string? SweepDestination { get; set; }

    // Set before the first SweepEvmDepositAsync. The token and destination are fixed from then on: the
    // SDK refuses another sweep while one it reserved is unresolved.
    public bool SweepAttempted { get; set; }

    public string? SweepTxHash { get; set; }

    public string? HtlcRefundTxHash { get; set; }

    // Why the swap closed without a sweep, such as an empty deposit address or the owner's dismissal.
    public string? ClosedReason { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    // Set once the swap needs nothing more from the plugin; the worker only reads rows without it.
    public DateTimeOffset? CompletedAt { get; set; }

    [NotMapped]
    public DateTimeOffset ClaimCutoff => VhtlcRefundLocktime - SatoraClaimWindow.ClaimMargin;
}

public enum SatoraStableAttention
{
    // Tokens at the deposit address that no funding can take any more.
    Sweep,
    // A funding the server will not complete: the HTLC holds the deposit until its refund locktime.
    HtlcRefund
}
