using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using BTCPayServer.Plugins.Satora.Models;
using Org.BouncyCastle.Crypto.Digests;

namespace BTCPayServer.Plugins.Satora.Stable;

public static partial class SatoraEvmAddress
{
    public static bool IsHex(string? address) => address is not null && Hex().IsMatch(address);

    // An address the owner typed, where a typo loses what is sent to it: one in mixed case has to carry
    // its EIP-55 checksum. The zero address burns tokens.
    public static bool IsValid(string? address)
    {
        if (!IsHex(address))
            return false;
        var hex = address![2..];
        var lower = hex.ToLowerInvariant();
        if (lower.All(c => c == '0'))
            return false;
        if (hex == lower || hex == hex.ToUpperInvariant())
            return true;
        var digest = new KeccakDigest(256);
        var bytes = Encoding.ASCII.GetBytes(lower);
        digest.BlockUpdate(bytes, 0, bytes.Length);
        var hash = new byte[32];
        digest.DoFinal(hash, 0);
        for (var i = 0; i < hex.Length; i++)
        {
            var nibble = i % 2 == 0 ? hash[i / 2] >> 4 : hash[i / 2] & 0xf;
            if (char.IsLetter(hex[i]) && char.IsUpper(hex[i]) != nibble >= 8)
                return false;
        }
        return true;
    }

    public static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^0x[0-9a-fA-F]{40}$")]
    private static partial Regex Hex();
}

public enum SatoraStableRefundRefusal
{
    None,
    NotFound,
    NotNeeded,
    // The deposit is in the swap's HTLC, which this page cannot refund.
    HtlcRefund,
    AlreadyStarted,
    InvalidAddress,
    InvalidToken,
    ConfirmationRequired,
    SweepRunning
}

public static class SatoraStableRefunds
{
    public const string ConfirmationWord = "REFUND";
    public const string DismissWord = "DISMISS";

    // Why a swap waiting for the owner closed without the plugin sending anything more.
    public const string DismissedReason = "Dismissed";
    // A sweep was started, its transaction never recorded, and the address then held nothing.
    public const string SweepUnrecordedReason = "SweepUnrecorded";

    // A plain token transfer costs a few cents of gas on Arbitrum; this leaves room for a busy hour.
    public static (BigInteger Wei, string Symbol)? SuggestedGas(long chainId) => chainId switch
    {
        42161 => (BigInteger.Pow(10, 14), "ETH"),
        _ => null
    };

    // The BTC-pegged token the swap server's route buys on the chain and its HTLC locks, which an HTLC
    // refund pays to the deposit address: tBTC on Arbitrum, as the swap server is configured. The owner
    // can still enter another contract on the refunds page.
    public static string? HtlcToken(long chainId) => chainId switch
    {
        42161 => "0x6c84a8f1c29108F47a79964b5Fe888D4f4D0dE40",
        _ => null
    };

    public static string ChainName(long chainId) =>
        SatoraStablePaymentMethod.All.FirstOrDefault(c => c.ChainId == chainId)?.ChainName ?? $"chain {chainId}";

    public static string NativeSymbol(long chainId) => SuggestedGas(chainId)?.Symbol ?? "native gas";

    public static string GasToSend(long chainId) =>
        SuggestedGas(chainId) is { } gas ? $"{Native(chainId, gas.Wei)} on {ChainName(chainId)}" : $"a little native gas on {ChainName(chainId)}";

    // The SDK sweeps the swap's own token when given none, so the owner naming it is the same as not.
    public static string? Token(SatoraStableSwap swap, string? token) =>
        string.IsNullOrWhiteSpace(token) || SatoraEvmAddress.Same(token.Trim(), swap.TokenAddress) ? null : token.Trim();

    // Less than a hundredth of the swap's own token is not worth the gas. Another token's decimals are
    // unknown, so any of it is.
    public static bool Worth(SatoraStableSwap swap, string? token, BigInteger units) =>
        token is null ? units >= SatoraStableActions.DustUnits(swap.Decimals) : units > 0;

    // The swap's own token in whole tokens; another token in its smallest unit, whose decimals are unknown.
    public static string Amount(SatoraStableSwap swap, string? token, BigInteger units) =>
        token is null
            ? $"{Trim(SatoraStableActions.FormatUnits(units, swap.Decimals))} {swap.Token}"
            : $"{units.ToString(CultureInfo.InvariantCulture)} units of the token {token}";

    public static string Native(long chainId, BigInteger wei) => $"{Trim(SatoraStableActions.FormatUnits(wei, 18))} {NativeSymbol(chainId)}";

    private static string Trim(string amount) => amount.Contains('.') ? amount.TrimEnd('0').TrimEnd('.') : amount;

    public static string Describe(SatoraStableAttention? attention) => attention switch
    {
        SatoraStableAttention.Sweep => "Tokens at the deposit address",
        SatoraStableAttention.HtlcRefund => "Deposit locked in the swap contract",
        _ => ""
    };

    // The destination and token can change until the worker first tries the sweep, as the SDK then
    // fixes them. Sending to the deposit address or to a token's contract would lose the tokens.
    public static SatoraStableRefundRefusal Check(SatoraStableSwap? swap, string? destination, string? token, string? confirmation)
    {
        if (swap is null)
            return SatoraStableRefundRefusal.NotFound;
        if (Waiting(swap) is var refusal and not SatoraStableRefundRefusal.None)
            return refusal;
        if (swap.SweepAttempted)
            return SatoraStableRefundRefusal.AlreadyStarted;
        if (!SatoraEvmAddress.IsValid(destination) || SatoraEvmAddress.Same(destination, swap.DepositAddress) ||
            SatoraEvmAddress.Same(destination, swap.TokenAddress) || SatoraEvmAddress.Same(destination, token))
            return SatoraStableRefundRefusal.InvalidAddress;
        if (token is not null && (!SatoraEvmAddress.IsValid(token) || SatoraEvmAddress.Same(token, swap.DepositAddress)))
            return SatoraStableRefundRefusal.InvalidToken;
        return string.Equals(confirmation?.Trim(), ConfirmationWord, StringComparison.Ordinal)
            ? SatoraStableRefundRefusal.None
            : SatoraStableRefundRefusal.ConfirmationRequired;
    }

    public static SatoraStableRefundRefusal CheckDismiss(SatoraStableSwap? swap, string? confirmation)
    {
        if (swap is null)
            return SatoraStableRefundRefusal.NotFound;
        if (Waiting(swap) is var refusal and not SatoraStableRefundRefusal.None)
            return refusal;
        return string.Equals(confirmation?.Trim(), DismissWord, StringComparison.Ordinal)
            ? SatoraStableRefundRefusal.None
            : SatoraStableRefundRefusal.ConfirmationRequired;
    }

    private static SatoraStableRefundRefusal Waiting(SatoraStableSwap swap) =>
        swap.NeedsAttentionAt is null || swap.CompletedAt is not null ? SatoraStableRefundRefusal.NotNeeded
        : swap.AttentionReason != SatoraStableAttention.Sweep ? SatoraStableRefundRefusal.HtlcRefund
        : SatoraStableRefundRefusal.None;

    // A sweep already mined, or one whose outcome was never learned, stays where it went.
    public static string DismissedNote(SatoraStableSwap swap) =>
        $"The refund of the tokens at the deposit address {swap.DepositAddress} of Satora stablecoin swap {swap.SwapId} was dismissed on Satora's Stablecoin refunds page. " +
        (swap.SweepTxHash is not null ? $"Its refund in {swap.SweepTxHash} already went out, and nothing more is sent from the address."
            : swap.SweepAttempted ? "An earlier refund attempt may still be mined, but nothing more is sent from the address."
            : "Nothing at the address is sent back.");
}
