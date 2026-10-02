using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraSeedProtectorTests
{
    private const string Phrase = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    private static SatoraSeedProtector NewProtector() => new(new EphemeralDataProtectionProvider());

    private static SatoraSettings Protected(SatoraSeedProtector protector, string storeId, bool backupConfirmed = true) => new()
    {
        StoreId = storeId,
        Enabled = true,
        BackupConfirmed = backupConfirmed,
        ProtectedSeed = protector.Protect(storeId, Phrase)
    };

    [Fact]
    public void ReadsBackTheSeedItProtected()
    {
        var protector = NewProtector();
        var settings = Protected(protector, "store1");

        Assert.DoesNotContain("abandon", settings.ProtectedSeed);
        Assert.Equal(new SatoraWalletStatus(SatoraWalletState.Ready, Phrase, true), protector.Read(settings));
    }

    [Fact]
    public void WalletWithoutConfirmedBackupIsNotEnabled()
    {
        var protector = NewProtector();

        Assert.Equal(new SatoraWalletStatus(SatoraWalletState.BackupRequired, Phrase, false),
            protector.Read(Protected(protector, "store1", backupConfirmed: false)));
    }

    // Lost or replaced data protection keys.
    [Fact]
    public void SeedIsLockedUnderOtherKeys()
    {
        var settings = Protected(NewProtector(), "store1");

        Assert.Equal(new SatoraWalletStatus(SatoraWalletState.Locked, null, false), NewProtector().Read(settings));
    }

    [Fact]
    public void CiphertextCopiedToAnotherStoreIsLocked()
    {
        var protector = NewProtector();
        var settings = Protected(protector, "store1");
        settings.StoreId = "store2";

        Assert.Equal(SatoraWalletState.Locked, protector.Read(settings).State);
    }

    // An unreadable seed is never mistaken for no seed, or for a legacy one that may be replaced by a restore.
    [Fact]
    public void UnreadableSeedIsLocked()
    {
        var settings = new SatoraSettings { StoreId = "store1", ProtectedSeed = "not a ciphertext", CryptedSeed = "legacy" };

        Assert.Equal(SatoraWalletState.Locked, NewProtector().Read(settings).State);
    }

    [Fact]
    public void LegacySeedRequiresRestore()
    {
        var settings = new SatoraSettings { StoreId = "store1", Enabled = true, CryptedSeed = "legacy" };

        Assert.Equal(new SatoraWalletStatus(SatoraWalletState.RestoreRequired, null, false), NewProtector().Read(settings));
    }

    [Fact]
    public void StoreWithoutSeedIsNotSetUp()
    {
        Assert.Equal(SatoraWalletState.NotSetUp, NewProtector().Read(null).State);
        Assert.Equal(SatoraWalletState.NotSetUp, NewProtector().Read(new SatoraSettings { StoreId = "store1" }).State);
    }
}
