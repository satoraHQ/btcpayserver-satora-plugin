using System.Text;
using NBitcoin;
using NBitcoin.Crypto;

namespace BTCPayServer.Plugins.Satora.Tests;

// Signed BOLT11 invoices for any amount, as a payee's LNURL endpoint issues them. Each has a fresh
// payment hash and node key.
public static class TestBolt11
{
    private const string Charset = "qpzry9x8gf2tvdw0s3jn54khce6mua7l";
    private static readonly uint[] Generator = [0x3b6a57b2, 0x26508e6d, 0x1ea119fa, 0x3d4233dd, 0x2a1462b3];

    // descriptionHash: the SHA-256 an LNURL-pay invoice commits to, of its endpoint's metadata.
    public static string Create(long amountSats, Network network, byte[]? descriptionHash = null)
    {
        var prefix = network.ChainName == ChainName.Mainnet ? "bc" : network.ChainName == ChainName.Regtest ? "bcrt" : "tb";
        // Ten nano-bitcoin to the sat.
        var hrp = $"ln{prefix}{amountSats * 10}n";
        var data = new List<byte>();
        AppendInt(data, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 7);
        AppendTag(data, 'p', RandomUtils.GetBytes(32));
        AppendTag(data, 's', RandomUtils.GetBytes(32));
        if (descriptionHash is null)
            AppendTag(data, 'd', Encoding.UTF8.GetBytes("test"));
        else
            AppendTag(data, 'h', descriptionHash);
        var expiry = new List<byte>();
        AppendInt(expiry, 3600, 3);
        data.Add((byte)Charset.IndexOf('x'));
        AppendInt(data, expiry.Count, 2);
        data.AddRange(expiry);
        var message = Encoding.UTF8.GetBytes(hrp).Concat(ConvertBits(data, 5, 8)).ToArray();
        var signature = new Key().SignCompact(new uint256(Hashes.SHA256(message)));
        data.AddRange(ConvertBits(signature.Signature.Concat(new[] { (byte)signature.RecoveryId }).ToList(), 8, 5));
        var checksum = Checksum(hrp, data);
        return hrp + "1" + new string(data.Concat(checksum).Select(b => Charset[b]).ToArray());
    }

    private static void AppendTag(List<byte> data, char tag, byte[] value)
    {
        var groups = ConvertBits(value.ToList(), 8, 5);
        data.Add((byte)Charset.IndexOf(tag));
        AppendInt(data, groups.Count, 2);
        data.AddRange(groups);
    }

    // Big-endian, in exactly that many 5-bit groups.
    private static void AppendInt(List<byte> data, long value, int groups)
    {
        for (var i = groups - 1; i >= 0; i--)
            data.Add((byte)((value >> (5 * i)) & 31));
    }

    // Pads the last group with zero bits.
    private static List<byte> ConvertBits(IReadOnlyList<byte> values, int from, int to)
    {
        var result = new List<byte>();
        int accumulator = 0, bits = 0, mask = (1 << to) - 1;
        foreach (var value in values)
        {
            accumulator = (accumulator << from) | value;
            bits += from;
            while (bits >= to)
            {
                bits -= to;
                result.Add((byte)((accumulator >> bits) & mask));
            }
        }
        if (bits > 0)
            result.Add((byte)((accumulator << (to - bits)) & mask));
        return result;
    }

    private static byte[] Checksum(string hrp, List<byte> data)
    {
        var values = hrp.Select(c => (byte)(c >> 5)).Append((byte)0).Concat(hrp.Select(c => (byte)(c & 31))).Concat(data)
            .Concat(new byte[6]);
        var polymod = Polymod(values) ^ 1;
        return Enumerable.Range(0, 6).Select(i => (byte)((polymod >> (5 * (5 - i))) & 31)).ToArray();
    }

    private static uint Polymod(IEnumerable<byte> values)
    {
        uint checksum = 1;
        foreach (var value in values)
        {
            var top = checksum >> 25;
            checksum = ((checksum & 0x1ffffff) << 5) ^ value;
            for (var i = 0; i < 5; i++)
                if (((top >> i) & 1) == 1)
                    checksum ^= Generator[i];
        }
        return checksum;
    }
}
