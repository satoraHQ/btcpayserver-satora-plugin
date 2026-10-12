using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraSwapLimitsTests
{
    // As the swap server words them (format_sats_to_btc), seen on regtest for a 5000 sat Lightning payment.
    [Theory]
    [InlineData("Min amount is ₿ 0.00 010 000", "at least 10000 sats (0.00010000 BTC)")]
    [InlineData("Max amount is ₿ 1.00 000 000", "at most 100000000 sats (1.00000000 BTC)")]
    [InlineData("Max amount is ₿ 21.50 000 001", "at most 2150000001 sats (21.50000001 BTC)")]
    public void WordsTheServersAmountLimitForTheStore(string server, string limit)
    {
        var refusal = SatoraSwapLimits.Refusal(new SdkException(SdkErrorKind.Api, server, 400), l => $"Satora pays Lightning amounts of {l}.");

        Assert.Equal($"Satora pays Lightning amounts of {limit}.", refusal?.Message);
    }

    [Theory]
    [InlineData(SdkErrorKind.Api, "could not resolve lightning address or LNURL")]
    [InlineData(SdkErrorKind.Api, "Min amount is unknown")]
    [InlineData(SdkErrorKind.Transport, "Min amount is ₿ 0.00 010 000")]
    public void LeavesEveryOtherErrorAsItIs(SdkErrorKind kind, string message)
    {
        Assert.Null(SatoraSwapLimits.Refusal(new SdkException(kind, message, 400), l => l));
        Assert.Null(SatoraSwapLimits.Refusal(new InvalidOperationException("Min amount is ₿ 0.00 010 000"), l => l));
    }
}
