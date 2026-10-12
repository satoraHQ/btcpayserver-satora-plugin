using BTCPayServer.Plugins.Satora.Models;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class TokenFormatTests
{
    [Theory]
    [InlineData("Btc", 8)]
    [InlineData("WbtcArbitrum", 8)]
    [InlineData("UsdcPolygon", 6)]
    [InlineData("Usdt0Arbitrum", 6)]
    [InlineData("EurcEthereum", 6)]
    [InlineData("UnlistedTokenEthereum", 18)]
    [InlineData(null, 18)]
    public void DecimalsFor(string? token, int expected)
    {
        Assert.Equal(expected, TokenFormat.DecimalsFor(token));
    }

    [Theory]
    [InlineData("1500000", "UsdcArbitrum", "1.500000")]
    [InlineData("12345678", "Btc", "0.12345678")]
    [InlineData("1000000000000000000", "TbtcEthereum", "1.000000000000000000")]
    [InlineData("0", "UsdtPolygon", "0.000000")]
    public void FormatAmountScalesBySmallestUnit(string raw, string token, string expected)
    {
        Assert.Equal(expected, TokenFormat.FormatAmount(raw, token));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("not-a-number", "not-a-number")]
    [InlineData("1.5", "1.5")]
    public void FormatAmountPassesThroughUnparseableInput(string? raw, string expected)
    {
        Assert.Equal(expected, TokenFormat.FormatAmount(raw, "UsdcPolygon"));
    }

    [Theory]
    [InlineData("ark1qabc", "UsdcPolygon", "https://arkade.space/address/ark1qabc")]
    [InlineData("0xabc", "UsdcArbitrum", "https://arbiscan.io/address/0xabc")]
    [InlineData("0xabc", "UsdtPolygon", "https://polygonscan.com/address/0xabc")]
    [InlineData("0xabc", "WbtcEthereum", "https://etherscan.io/address/0xabc")]
    [InlineData("0xabc", "Btc", null)]
    [InlineData("bc1qabc", "Btc", null)]
    [InlineData(null, "UsdcPolygon", null)]
    public void ExplorerUrl(string? address, string token, string? expected)
    {
        Assert.Equal(expected, TokenFormat.ExplorerUrl(address, token));
    }
}
