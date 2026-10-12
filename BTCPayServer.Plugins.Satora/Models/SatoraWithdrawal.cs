using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

// One withdrawal attempt, written before any SDK call that could move funds.
public class SatoraWithdrawal
{
    // For a manual withdrawal, the idempotency key the review page handed out: a second submit of the
    // same review is refused.
    [Key]
    public string Id { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    public SatoraWithdrawalOrigin Origin { get; set; }

    public SatoraWithdrawalType Type { get; set; }

    public string Destination { get; set; } = null!;

    // What the destination receives; null until known for "send everything".
    public long? AmountSats { get; set; }

    public long? FeeSats { get; set; }

    public SatoraWithdrawalStatus Status { get; set; }

    // The Ark txid of a send or a Lightning swap's funding, or the commitment txid of an offboard.
    public string? TxId { get; set; }

    // Lightning withdrawals: the Arkade to Lightning swap, which SatoraOutgoingWorker resolves.
    public string? SwapId { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public enum SatoraWithdrawalType
{
    Arkade,
    LightningInvoice,
    LightningAddress,
    Lnurl,
    Bitcoin
}

public enum SatoraWithdrawalOrigin
{
    Manual,
    // Started by the store's automatic withdrawal rule.
    Auto
}

public enum SatoraWithdrawalStatus
{
    Pending,
    Sent,
    // The SDK could not tell whether the funds left: only the owner, checking the balance, can.
    Unknown,
    Failed
}
