using System.Numerics;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Stable;
using Xunit;
using Refusal = BTCPayServer.Plugins.Satora.Stable.SatoraStableRefundRefusal;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraStableRefundsTests
{
    // EIP-55's own examples.
    public const string Customer = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    public const string OtherToken = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359";
    public const string Deposit = "0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB";
    private const string Usdc = "0xaf88d065e77c8cC2239327C5EDb3A432268e5831";

    [Theory]
    [InlineData(Customer, true)]
    [InlineData(OtherToken, true)]
    [InlineData(Deposit, true)]
    [InlineData("0xD1220A0cf47c7B9Be7A2E6BA89F429762e7b9aDb", true)]
    // In one case throughout, an address carries no checksum.
    [InlineData("0x52908400098527886E0F7030069857D2E4169EE7", true)]
    [InlineData("0xde709f2102306220921060314715629080e2fb77", true)]
    // The last letter's case flipped, as a typo would.
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeD", false)]
    [InlineData("0x0000000000000000000000000000000000000000", false)]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAe", false)]
    [InlineData("5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed00", false)]
    [InlineData(null, false)]
    public void AcceptsOnlyAnAddressWhoseChecksumHoldsWhenItHasOne(string? address, bool valid) =>
        Assert.Equal(valid, SatoraEvmAddress.IsValid(address));

    [Theory]
    [InlineData("missing", Customer, null, "REFUND", Refusal.NotFound)]
    [InlineData("open", Customer, null, "REFUND", Refusal.NotNeeded)]
    [InlineData("closed", Customer, null, "REFUND", Refusal.NotNeeded)]
    [InlineData("htlc", Customer, null, "REFUND", Refusal.HtlcRefund)]
    [InlineData("attempted", Customer, null, "REFUND", Refusal.AlreadyStarted)]
    [InlineData("waiting", "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeD", null, "REFUND", Refusal.InvalidAddress)]
    [InlineData("waiting", null, null, "REFUND", Refusal.InvalidAddress)]
    // Each would lose the tokens: the address sends to itself, or to a token's contract.
    [InlineData("waiting", "0xDBF03B407C01E7CD3CBEA99509D93F8DDDC8C6FB", null, "REFUND", Refusal.InvalidAddress)]
    [InlineData("waiting", "0xaf88d065e77c8cc2239327c5edb3a432268e5831", null, "REFUND", Refusal.InvalidAddress)]
    [InlineData("waiting", OtherToken, OtherToken, "REFUND", Refusal.InvalidAddress)]
    [InlineData("waiting", Customer, "0x1234", "REFUND", Refusal.InvalidToken)]
    [InlineData("waiting", Customer, Deposit, "REFUND", Refusal.InvalidToken)]
    [InlineData("waiting", Customer, null, "refund", Refusal.ConfirmationRequired)]
    [InlineData("waiting", Customer, null, null, Refusal.ConfirmationRequired)]
    [InlineData("waiting", Customer, null, " REFUND ", Refusal.None)]
    [InlineData("waiting", Customer, OtherToken, "REFUND", Refusal.None)]
    public void ASweepIsRequestedOnlyForTokensWaitingAtTheAddressToAnAddressThatKeepsThem(string state, string? destination, string? token,
        string? confirmation, Refusal expected) =>
        Assert.Equal(expected, SatoraStableRefunds.Check(Swap(state), destination, token, confirmation));

    // The refunds page fills in the token an HTLC refund paid to the address, which the owner then sends.
    [Fact]
    public void TheTokenAnHtlcRefundPaysIsOneTheOwnerCanSendBack()
    {
        var token = SatoraStableRefunds.HtlcToken(42161);
        var swap = Swap("waiting")!;

        Assert.Equal("0x6c84a8f1c29108F47a79964b5Fe888D4f4D0dE40", token);
        Assert.Equal(token, SatoraStableRefunds.Token(swap, token));
        Assert.Equal(Refusal.None, SatoraStableRefunds.Check(swap, Customer, token, "REFUND"));
        Assert.Null(SatoraStableRefunds.HtlcToken(137));
    }

    [Theory]
    [InlineData("missing", "DISMISS", Refusal.NotFound)]
    [InlineData("open", "DISMISS", Refusal.NotNeeded)]
    [InlineData("closed", "DISMISS", Refusal.NotNeeded)]
    // The HTLC's refund is made by Satora support, not from this page.
    [InlineData("htlc", "DISMISS", Refusal.HtlcRefund)]
    [InlineData("waiting", "REFUND", Refusal.ConfirmationRequired)]
    [InlineData("waiting", "DISMISS", Refusal.None)]
    [InlineData("attempted", "DISMISS", Refusal.None)]
    public void OnlyTokensWaitingAtTheAddressAreDismissedAndOnlyWithTheTypedWord(string state, string confirmation, Refusal expected) =>
        Assert.Equal(expected, SatoraStableRefunds.CheckDismiss(Swap(state), confirmation));

    [Theory]
    [InlineData(null, null)]
    [InlineData(" ", null)]
    // The swap's own token, as the SDK sweeps it when given none.
    [InlineData(" 0xAF88D065E77C8CC2239327C5EDB3A432268E5831 ", null)]
    [InlineData($" {OtherToken} ", OtherToken)]
    public void NamingTheSwapsOwnTokenIsTheSameAsNamingNone(string? entered, string? token) =>
        Assert.Equal(token, SatoraStableRefunds.Token(Swap("waiting")!, entered));

    // A hundredth of a USDC is the least worth its gas; another token's decimals are unknown.
    [Theory]
    [InlineData(null, 9_999, false)]
    [InlineData(null, 10_000, true)]
    [InlineData(OtherToken, 0, false)]
    [InlineData(OtherToken, 1, true)]
    public void OnlyWhatIsWorthTheGasIsSentBack(string? token, long units, bool worth) =>
        Assert.Equal(worth, SatoraStableRefunds.Worth(Swap("waiting")!, token, units));

    [Fact]
    public void ShowsAmountsAndTheGasToSendInWholeUnits()
    {
        var swap = Swap("waiting")!;

        Assert.Equal("10.13 USDC", SatoraStableRefunds.Amount(swap, null, 10_130_000));
        Assert.Equal("0 USDC", SatoraStableRefunds.Amount(swap, null, 0));
        Assert.Equal($"5000 units of the token {OtherToken}", SatoraStableRefunds.Amount(swap, OtherToken, 5_000));
        Assert.Equal("0.0001 ETH", SatoraStableRefunds.Native(swap.ChainId, BigInteger.Pow(10, 14)));
        Assert.Equal("1.5 ETH", SatoraStableRefunds.Native(swap.ChainId, 15 * BigInteger.Pow(10, 17)));
        Assert.Equal("0.0001 ETH on Arbitrum One", SatoraStableRefunds.GasToSend(swap.ChainId));
    }

    // A transfer already mined, or one whose outcome was never learned, stays where it went.
    [Theory]
    [InlineData(false, null, "Nothing at the address is sent back.")]
    [InlineData(true, null, "An earlier refund attempt may still be mined, but nothing more is sent from the address.")]
    [InlineData(true, "0xsweep", "Its refund in 0xsweep already went out, and nothing more is sent from the address.")]
    public void ADismissalNotesWhatItLeaves(bool attempted, string? txHash, string leaves)
    {
        var swap = Swap("waiting")!;
        swap.SweepAttempted = attempted;
        swap.SweepTxHash = txHash;

        Assert.EndsWith(leaves, SatoraStableRefunds.DismissedNote(swap));
    }

    internal static SatoraStableSwap? Swap(string state)
    {
        if (state == "missing")
            return null;
        var swap = SatoraStableIntegrationTests.Swap("store", "swap", deposit: Deposit);
        swap.TokenAddress = Usdc;
        if (state != "open")
        {
            swap.NeedsAttentionAt = swap.CreatedAt;
            swap.AttentionReason = state == "htlc" ? SatoraStableAttention.HtlcRefund : SatoraStableAttention.Sweep;
        }
        swap.CompletedAt = state == "closed" ? swap.CreatedAt : null;
        swap.SweepAttempted = state == "attempted";
        return swap;
    }
}
