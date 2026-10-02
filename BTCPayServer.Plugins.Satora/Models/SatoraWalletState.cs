namespace BTCPayServer.Plugins.Satora.Models;

public enum SatoraWalletState
{
    NotSetUp,
    // Only a seed from an earlier plugin version, which this version does not read.
    RestoreRequired,
    // A seed is stored but cannot be decrypted, usually because BTCPay's data protection keys changed.
    Locked,
    BackupRequired,
    Ready
}
