using BTCPayServer.Plugins.Satora.Services;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraMnemonicTests
{
    private const string Phrase = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Fact]
    public void AcceptsAValidPhraseInAnyCaseAndSpacing()
    {
        Assert.True(SatoraMnemonic.TryParse("  Abandon abandon ABANDON abandon abandon abandon\nabandon abandon abandon abandon abandon   about ", out var mnemonic));
        Assert.Equal(Phrase, mnemonic);
    }

    [Theory]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon")]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about")]
    [InlineData("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon satora")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsPhrasesTheSdkWouldRefuse(string? input)
    {
        Assert.False(SatoraMnemonic.TryParse(input, out _));
    }

    [Fact]
    public void GeneratesValidTwelveWordPhrases()
    {
        var first = SatoraMnemonic.Generate();

        Assert.True(SatoraMnemonic.TryParse(first, out _));
        Assert.Equal(12, first.Split(' ').Length);
        Assert.NotEqual(first, SatoraMnemonic.Generate());
    }

    // The BIP32 root fingerprint of the BIP39 test phrase, as wallets show it (BIP84 test vectors).
    [Fact]
    public void FingerprintIsTheBip32RootFingerprint()
    {
        Assert.Equal("73c5da0a", SatoraMnemonic.Fingerprint(Phrase));
    }
}
