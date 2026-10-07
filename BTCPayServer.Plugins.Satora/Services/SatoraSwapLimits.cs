using System.Globalization;
using System.Text.RegularExpressions;
using Satora.Sdk;

namespace BTCPayServer.Plugins.Satora.Services;

// The swap server refuses an amount outside a route's limits with text alone, in a BTC grouping of
// its own: "Min amount is ₿ 0.00 010 000" (format_sats_to_btc in the swap repo's lib.rs).
public static partial class SatoraSwapLimits
{
    [GeneratedRegex(@"^(Min|Max) amount is ₿ (\d+)\.(\d{2}) (\d{3}) (\d{3})$", RegexOptions.CultureInvariant)]
    private static partial Regex LimitText();

    // The server's refusal of an amount as the plugin words it, or null for any other error. message
    // gets the limit, as "at least 10000 sats (0.00010000 BTC)" or "at most ...".
    public static SatoraAmountRefusedException? Refusal(Exception error, Func<string, string> message)
    {
        if (error is not SdkException { Kind: SdkErrorKind.Api } sdk || LimitText().Match(sdk.Message.Trim()) is not { Success: true } match)
            return null;
        var sats = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 100_000_000 +
                   long.Parse(match.Groups[3].Value + match.Groups[4].Value + match.Groups[5].Value, CultureInfo.InvariantCulture);
        return new SatoraAmountRefusedException(message(Limit(match.Groups[1].Value == "Min", sats)), error);
    }

    public static string Limit(bool minimum, long sats) =>
        string.Create(CultureInfo.InvariantCulture, $"{(minimum ? "at least" : "at most")} {sats} sats ({sats / 100_000_000m:0.00000000} BTC)");

    public static async Task<T> CheckAsync<T>(Func<Task<T>> call, Func<string, string> message)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (Refusal(e, message) is { } refusal)
        {
            throw refusal;
        }
    }
}

public sealed class SatoraAmountRefusedException(string message, Exception innerException) : InvalidOperationException(message, innerException);
