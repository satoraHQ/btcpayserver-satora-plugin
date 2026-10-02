using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

// A seed ciphertext replaced by another wallet. Kept so funds a merchant did not expect in the
// old wallet can still be recovered by an operator.
public class SatoraRetiredSeed
{
    public const string DataProtectionFormat = "DataProtection";
    public const string LegacyFormat = "LegacyAes";

    [Key]
    public string Id { get; set; } = null!;

    public string StoreId { get; set; } = null!;

    public string Format { get; set; } = null!;

    public string Ciphertext { get; set; } = null!;

    public string? SeedFingerprint { get; set; }

    public DateTimeOffset RetiredAt { get; set; }
}
