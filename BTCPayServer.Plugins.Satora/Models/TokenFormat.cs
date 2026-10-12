namespace BTCPayServer.Plugins.Satora.Models
{
    // Block-explorer links for addresses, and the SDK's txids in the form explorers take.
    public static class TokenFormat
    {
        // Block-explorer URL for an address, dispatching on:
        //   - Arkade addresses (prefix `ark1`) -> arkade.space
        //   - EVM addresses (prefix `0x`) -> per-chain scanner, picked
        //     from the token suffix (Arbitrum / Ethereum / Polygon)
        // Returns null when we have no good link — the view falls back
        // to plain text.
        public static string? ExplorerUrl(string? address, string? tokenTypeName)
        {
            if (string.IsNullOrEmpty(address)) return null;
            if (address.StartsWith("ark1")) return $"https://arkade.space/address/{address}";
            if (address.StartsWith("0x"))
            {
                if (tokenTypeName?.EndsWith("Arbitrum") == true) return $"https://arbiscan.io/address/{address}";
                if (tokenTypeName?.EndsWith("Polygon") == true) return $"https://polygonscan.com/address/{address}";
                if (tokenTypeName?.EndsWith("Ethereum") == true) return $"https://etherscan.io/address/{address}";
            }
            return null;
        }

        // The SDK returns Ark and commitment txids as 0x-prefixed hex (its Bitcoin refund txids come
        // bare), while explorers, arkd and bitcoind take bare hex. Not for EVM hashes, which keep their 0x.
        public static string? BareTxId(string? txid) =>
            txid is { Length: 66 } && txid.StartsWith("0x", StringComparison.Ordinal) && txid[2..].All(char.IsAsciiHexDigit)
                ? txid[2..]
                : txid;
    }
}
