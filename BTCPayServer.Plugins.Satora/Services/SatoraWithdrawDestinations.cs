using System.Globalization;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using NBitcoin;
using NBitcoin.Payment;

namespace BTCPayServer.Plugins.Satora.Services;

// AmountSats: the amount the destination itself fixes (a BOLT11 invoice, a BIP21 URI's amount).
public sealed record SatoraWithdrawTarget(SatoraWithdrawalType Type, string Destination, long? AmountSats, string? PaymentHash = null);

// Destination detection from pseudozach's PR #2, checked against the store's network.
public static class SatoraWithdrawDestinations
{
    public static string Display(SatoraWithdrawalType type) => type switch
    {
        SatoraWithdrawalType.Arkade => "Arkade",
        SatoraWithdrawalType.LightningInvoice => "Lightning invoice",
        SatoraWithdrawalType.LightningAddress => "Lightning address",
        SatoraWithdrawalType.Lnurl => "LNURL",
        _ => "Bitcoin on-chain"
    };

    public static bool IsLightning(SatoraWithdrawalType type) =>
        type is SatoraWithdrawalType.LightningInvoice or SatoraWithdrawalType.LightningAddress or SatoraWithdrawalType.Lnurl;

    public static SatoraWithdrawTarget? Detect(string? input, Network network, out string? error)
    {
        error = null;
        var destination = input?.Trim() ?? "";
        if (destination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            destination = destination["lightning:".Length..];
        if (destination.Length == 0)
        {
            error = "Enter a destination.";
            return null;
        }
        if (destination.StartsWith("ark1", StringComparison.OrdinalIgnoreCase) || destination.StartsWith("tark1", StringComparison.OrdinalIgnoreCase))
        {
            var prefix = network.ChainName == ChainName.Mainnet ? "ark1" : "tark1";
            if (destination.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return new SatoraWithdrawTarget(SatoraWithdrawalType.Arkade, destination, null);
            error = "That Arkade address is for another network.";
            return null;
        }
        if (BOLT11PaymentRequest.TryParse(destination, out var invoice, network) && invoice?.PaymentHash is { } paymentHash)
        {
            long? sats = invoice.MinimumAmount == LightMoney.Zero ? null : (invoice.MinimumAmount.MilliSatoshi + 999) / 1000;
            return new SatoraWithdrawTarget(SatoraWithdrawalType.LightningInvoice, destination, sats, paymentHash.ToString());
        }
        if (destination.StartsWith("lnurl1", StringComparison.OrdinalIgnoreCase))
            return new SatoraWithdrawTarget(SatoraWithdrawalType.Lnurl, destination, null);
        if (IsLightningAddress(destination))
            return new SatoraWithdrawTarget(SatoraWithdrawalType.LightningAddress, destination.ToLowerInvariant(), null);
        try
        {
            var bip21 = new BitcoinUrlBuilder(destination, network);
            if (bip21.Address is not null)
                return new SatoraWithdrawTarget(SatoraWithdrawalType.Bitcoin, bip21.Address.ToString(), bip21.Amount?.Satoshi);
        }
        catch (FormatException)
        {
        }
        try
        {
            return new SatoraWithdrawTarget(SatoraWithdrawalType.Bitcoin, BitcoinAddress.Create(destination, network).ToString(), null);
        }
        catch (FormatException)
        {
        }
        error = "Enter an Arkade address, a Lightning invoice, Lightning address or LNURL, or a Bitcoin address for this network.";
        return null;
    }

    // LUD-16: a lowercase local part of letters, digits and -_.+ at a domain name.
    private static bool IsLightningAddress(string destination)
    {
        var at = destination.IndexOf('@');
        if (at <= 0 || at != destination.LastIndexOf('@'))
            return false;
        var user = destination[..at].ToLowerInvariant();
        var host = destination[(at + 1)..];
        return user.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' or '+') &&
               host.Contains('.') && Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    // The amount to send, or null for everything the wallet can spend; or an error. A destination
    // that fixes the amount wins only when nothing else was entered.
    public static string? Amount(SatoraWithdrawTarget target, string? amountBtc, bool sendAll, out long? amountSats)
    {
        amountSats = null;
        long? typed = null;
        if (!string.IsNullOrWhiteSpace(amountBtc))
        {
            if (!TryParseDecimal(amountBtc, out var btc))
                return "Enter the amount in BTC with a point before the decimals, for example 0.001.";
            if (btc == 0 || ToSats(btc) is not { } sats)
                return "Enter an amount above zero and at most 21,000,000 BTC, with at most 8 decimals.";
            typed = sats;
        }
        if (target.Type == SatoraWithdrawalType.LightningInvoice)
        {
            if (target.AmountSats is null)
                return "The Satora wallet cannot pay an invoice without an amount.";
            if (sendAll || typed is not null && typed != target.AmountSats)
                return "The invoice fixes the amount; leave the amount empty.";
            amountSats = target.AmountSats;
            return null;
        }
        if (sendAll)
            return typed is null ? null : "Enter an amount or choose to send everything, not both.";
        if (typed is not null && target.AmountSats is not null && typed != target.AmountSats)
            return "The amount differs from the one in the payment request.";
        amountSats = typed ?? target.AmountSats;
        return amountSats is null ? "Enter an amount, or choose to send everything." : null;
    }

    // The point is the only separator taken: read with a group separator, "0,001" would be 1 BTC.
    public static bool TryParseDecimal(string text, out decimal value) =>
        decimal.TryParse(text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    // Whole sats for an amount in BTC, or null above 21,000,000 BTC or past 8 decimals. The range is
    // checked first: a decimal this large overflows when turned into sats.
    public static long? ToSats(decimal btc)
    {
        if (btc < 0 || btc > 21_000_000m)
            return null;
        var sats = btc * 100_000_000m;
        return sats == decimal.Truncate(sats) ? (long)sats : null;
    }
}
