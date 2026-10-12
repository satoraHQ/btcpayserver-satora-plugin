using System.ComponentModel.DataAnnotations;

namespace BTCPayServer.Plugins.Satora.Models;

public class SatoraSettings
{
    [Key]
    public string StoreId { get; set; } = null!;

    public bool Enabled { get; set; }

    // Written by earlier plugin versions (AES keyed on the public store id). Never read again:
    // a store that still has one must restore its wallet from the recovery phrase.
    public string? CryptedSeed { get; set; }

    // IDataProtector ciphertext, purpose chain ("Satora.WalletSeed", StoreId).
    public string? ProtectedSeed { get; set; }

    // BIP32 root fingerprint of the seed, kept in clear so a restore can tell whether it brings back
    // the same wallet even when ProtectedSeed can no longer be decrypted.
    public string? SeedFingerprint { get; set; }

    public bool BackupConfirmed { get; set; }

    public DateTimeOffset? WalletCreated { get; set; }

    public DateTimeOffset? BackupConfirmedAt { get; set; }

    public string? LightningConnectionKey { get; set; }
}
