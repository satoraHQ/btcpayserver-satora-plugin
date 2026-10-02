using System.Diagnostics.CodeAnalysis;
using NBitcoin;

namespace BTCPayServer.Plugins.Satora.Services;

public static class SatoraMnemonic
{
    public static string Generate() => new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

    // NBitcoin's Mnemonic constructor checks the word count and the wordlist but not the checksum,
    // and the SDK refuses a phrase with a bad checksum only once a client is built from it.
    public static bool TryParse(string? input, [NotNullWhen(true)] out string? mnemonic)
    {
        mnemonic = null;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        var words = input.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        try
        {
            var parsed = new Mnemonic(string.Join(' ', words), Wordlist.English);
            if (!parsed.IsValidChecksum)
                return false;
            mnemonic = parsed.ToString();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string Fingerprint(string mnemonic) =>
        new Mnemonic(mnemonic, Wordlist.English).DeriveExtKey().GetPublicKey().GetHDFingerPrint().ToString();
}
