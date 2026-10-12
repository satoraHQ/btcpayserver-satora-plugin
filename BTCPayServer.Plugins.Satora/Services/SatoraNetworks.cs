using NBitcoin;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Services;

public static class SatoraNetworks
{
    // Read through BTCPay's configuration: BTCPAY_SATORA_BASEURL in the environment, or
    // satora_baseurl= in settings.config.
    public const string BaseUrlKey = "SATORA_BASEURL";
    public const string ArkadeServerUrlKey = "SATORA_ARKADESERVERURL";
    public const string EsploraUrlKey = "SATORA_ESPLORAURL";
    // Each EVM chain's node for the stablecoin swaps' deposit, relay, claim and sweep reads, on mainnet
    // too: a node can delay a funding but not move the deposit, which takes the depositor's signature,
    // a claim takes its word for a conversion shortfall only within the invoice's payment tolerance,
    // and the public nodes the SDK falls back to rate-limit. Off mainnet the chain is a test fork, so
    // the stablecoin methods need one.
    public const string EvmRpcArbitrumKey = "SATORA_EVMRPC_ARBITRUM";

    public static SatoraBitcoinNetwork FromChain(ChainName chain)
    {
        if (chain == ChainName.Mainnet)
            return SatoraBitcoinNetwork.Mainnet;
        if (chain == ChainName.Regtest)
            return SatoraBitcoinNetwork.Regtest;
        if (chain == global::NBitcoin.Bitcoin.Instance.Signet.ChainName)
            return SatoraBitcoinNetwork.Signet;
        throw new NotSupportedException(
            $"Satora does not support {chain}. Run BTCPay on mainnet, signet or regtest.");
    }

    // Overrides point a test deployment at its own backend; a mainnet store always uses the SDK defaults.
    public static SatoraEndpoints Endpoints(IConfiguration configuration, SatoraBitcoinNetwork network)
    {
        if (network == SatoraBitcoinNetwork.Mainnet)
            return new SatoraEndpoints(null, null, null);
        return new SatoraEndpoints(
            NullIfEmpty(configuration[BaseUrlKey]),
            NullIfEmpty(configuration[ArkadeServerUrlKey]),
            NullIfEmpty(configuration[EsploraUrlKey]));
    }

    // The SDK's own default for each network (Satora.Sdk Defaults.For, which it keeps internal).
    public static string? DefaultEsploraUrl(SatoraBitcoinNetwork network) => network switch
    {
        SatoraBitcoinNetwork.Mainnet => "https://mempool.space/api",
        SatoraBitcoinNetwork.Signet => "https://mutinynet.com/api",
        SatoraBitcoinNetwork.Regtest => "http://localhost:3000/api",
        _ => null
    };

    public static string? EvmRpcKey(long chainId) => chainId switch
    {
        42161 => EvmRpcArbitrumKey,
        _ => null
    };

    // Null for the SDK's public default.
    public static string? EvmRpcUrl(IConfiguration configuration, long chainId) =>
        EvmRpcKey(chainId) is { } key ? NullIfEmpty(configuration[key]) : null;

    public static bool HasEndpointOverrides(IConfiguration configuration) =>
        new[] { BaseUrlKey, ArkadeServerUrlKey, EsploraUrlKey }.Any(key => NullIfEmpty(configuration[key]) is not null);

    public static string WalletDirectory(string dataDir, SatoraBitcoinNetwork network, string storeId)
    {
        if (string.IsNullOrEmpty(storeId) || storeId is "." or ".." ||
            storeId.IndexOfAny(['/', '\\']) >= 0 || storeId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Not a valid store id for a directory name.", nameof(storeId));
        return Path.Combine(dataDir, "SatoraWallets", network.ToString().ToLowerInvariant(), storeId);
    }

    // The directory holds the SDK's swap and contract databases.
    public static void CreateOwnerOnlyDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(directory);
            return;
        }
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        Directory.CreateDirectory(directory, ownerOnly);
        // CreateDirectory leaves the mode of a directory that already exists alone.
        File.SetUnixFileMode(directory, ownerOnly);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public sealed record SatoraEndpoints(string? BaseUrl, string? ArkadeServerUrl, string? EsploraUrl);
