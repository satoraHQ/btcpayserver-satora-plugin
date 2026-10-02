using System.Runtime.Versioning;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Configuration;
using NBitcoin;
using Xunit;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraNetworksTests
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // The names BTCPay accepts for --network, resolved the way DefaultConfiguration.GetNetworkType does.
    [Theory]
    [InlineData("mainnet", SatoraBitcoinNetwork.Mainnet)]
    [InlineData("regtest", SatoraBitcoinNetwork.Regtest)]
    [InlineData("signet", SatoraBitcoinNetwork.Signet)]
    public void MapsBtcpayNetworksToSdkNetworks(string btcpayNetwork, SatoraBitcoinNetwork expected)
    {
        Assert.Equal(expected, SatoraNetworks.FromChain(Network.GetNetwork(btcpayNetwork)!.ChainName));
    }

    [Fact]
    public void RefusesTestnet()
    {
        var e = Assert.Throws<NotSupportedException>(() => SatoraNetworks.FromChain(Network.GetNetwork("testnet")!.ChainName));
        Assert.Contains("testnet", e.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HonoursEndpointOverridesOffMainnetOnly()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["satora_baseurl"] = "http://satora.test:3333",
            ["SATORA_ARKADESERVERURL"] = "http://arkd.test:7070",
            ["SATORA_ESPLORAURL"] = " "
        }).Build();

        Assert.Equal(new SatoraEndpoints("http://satora.test:3333", "http://arkd.test:7070", null),
            SatoraNetworks.Endpoints(configuration, SatoraBitcoinNetwork.Regtest));
        Assert.Equal(new SatoraEndpoints(null, null, null),
            SatoraNetworks.Endpoints(configuration, SatoraBitcoinNetwork.Mainnet));
    }

    // BTCPay reads its environment with the BTCPAY_ prefix, so this is the variable an operator sets.
    [Fact]
    public void ReadsTheOverrideFromBtcpayEnvironment()
    {
        const string variable = "BTCPAY_SATORA_BASEURL";
        var previous = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, "http://satora.test:3333");
        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables("BTCPAY_").Build();
            Assert.Equal("http://satora.test:3333", SatoraNetworks.Endpoints(configuration, SatoraBitcoinNetwork.Signet).BaseUrl);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public void WalletDirectoryIsPerNetworkAndStore()
    {
        Assert.Equal(Path.Combine("/var/lib/btcpay", "SatoraWallets", "regtest", "7BxYr9"),
            SatoraNetworks.WalletDirectory("/var/lib/btcpay", SatoraBitcoinNetwork.Regtest, "7BxYr9"));
        Assert.Equal(Path.Combine("/var/lib/btcpay", "SatoraWallets", "mainnet", "7BxYr9"),
            SatoraNetworks.WalletDirectory("/var/lib/btcpay", SatoraBitcoinNetwork.Mainnet, "7BxYr9"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    public void RefusesStoreIdsThatLeaveTheDirectory(string storeId)
    {
        Assert.Throws<ArgumentException>(() => SatoraNetworks.WalletDirectory("/var/lib/btcpay", SatoraBitcoinNetwork.Regtest, storeId));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void WalletDirectoryIsOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Unix permissions");
        var root = Directory.CreateTempSubdirectory();
        try
        {
            var directory = SatoraNetworks.WalletDirectory(root.FullName, SatoraBitcoinNetwork.Regtest, "store");
            SatoraNetworks.CreateOwnerOnlyDirectory(directory);
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(directory));

            File.SetUnixFileMode(directory, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            SatoraNetworks.CreateOwnerOnlyDirectory(directory);
            Assert.Equal(OwnerOnly, File.GetUnixFileMode(directory));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
