using BTCPayServer.Plugins.Satora.Models;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class TokenFormatTests
{
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

    // An offboard's commitment txid as the SDK returned it; esplora found it only without the prefix.
    [Theory]
    [InlineData("0xd073afb6857e07ddcaa39c6874381aaf419d3a3c781493a4af492fe87e7b5bba", "d073afb6857e07ddcaa39c6874381aaf419d3a3c781493a4af492fe87e7b5bba")]
    [InlineData("d073afb6857e07ddcaa39c6874381aaf419d3a3c781493a4af492fe87e7b5bba", "d073afb6857e07ddcaa39c6874381aaf419d3a3c781493a4af492fe87e7b5bba")]
    [InlineData("0xd073afb6857e07ddcaa39c6874381aaf419d3a3c781493a4af492fe87e7b5bbz", "0xd073afb6857e07ddcaa39c6874381aaf419d3a3c781493a4af492fe87e7b5bbz")]
    [InlineData("0x1234", "0x1234")]
    [InlineData(null, null)]
    public void BareTxIdDropsTheSdksHexPrefix(string? txid, string? expected)
    {
        Assert.Equal(expected, TokenFormat.BareTxId(txid));
    }
}
