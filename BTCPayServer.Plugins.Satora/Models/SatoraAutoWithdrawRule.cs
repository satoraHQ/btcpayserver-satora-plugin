using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

// A store's automatic withdrawal: once the wallet can spend ThresholdSats, what it can spend above
// ReserveSats goes to Destination.
public class SatoraAutoWithdrawRule
{
    [Key]
    public string StoreId { get; set; } = null!;

    public bool Enabled { get; set; }

    public SatoraWithdrawalType DestinationType { get; set; }

    public string Destination { get; set; } = null!;

    public long ThresholdSats { get; set; }

    public long ReserveSats { get; set; }

    // The most a run pays in fees, in basis points of what the destination receives.
    public int MaxFeeBps { get; set; }

    // Raised by every write. A save names the version its form showed, so it never overwrites a
    // change it did not see.
    public int Version { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
