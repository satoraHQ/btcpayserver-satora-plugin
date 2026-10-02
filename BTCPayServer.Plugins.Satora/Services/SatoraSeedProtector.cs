using System.Security.Cryptography;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.AspNetCore.DataProtection;

namespace BTCPayServer.Plugins.Satora.Services;

// Seeds at rest use BTCPay's data protection keys, as BTCPay does for wallet configs ("ConfigProtector"
// in UIStoreOnChainWalletsController). The store id in the purpose chain keeps a ciphertext copied to
// another store's row from decrypting there.
public class SatoraSeedProtector(IDataProtectionProvider provider)
{
    public const string Purpose = "Satora.WalletSeed";

    public string Protect(string storeId, string mnemonic) => For(storeId).Protect(mnemonic);

    public SatoraWalletStatus Read(SatoraSettings? settings)
    {
        if (settings is null)
            return new(SatoraWalletState.NotSetUp, null, false);
        if (string.IsNullOrEmpty(settings.ProtectedSeed))
            return new(string.IsNullOrEmpty(settings.CryptedSeed) ? SatoraWalletState.NotSetUp : SatoraWalletState.RestoreRequired, null, false);
        string mnemonic;
        try
        {
            mnemonic = For(settings.StoreId).Unprotect(settings.ProtectedSeed);
        }
        catch (CryptographicException)
        {
            return new(SatoraWalletState.Locked, null, false);
        }
        return settings.BackupConfirmed
            ? new(SatoraWalletState.Ready, mnemonic, settings.Enabled)
            : new(SatoraWalletState.BackupRequired, mnemonic, false);
    }

    private IDataProtector For(string storeId) => provider.CreateProtector(Purpose, storeId);
}

// Enabled is the stored flag of a Ready wallet; a wallet in any other state takes no payments.
public sealed record SatoraWalletStatus(SatoraWalletState State, string? Mnemonic, bool Enabled);
