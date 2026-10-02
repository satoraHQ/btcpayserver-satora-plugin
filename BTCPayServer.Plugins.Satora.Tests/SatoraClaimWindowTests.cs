using BTCPayServer.Plugins.Satora.Lightning;
using Xunit;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraClaimWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    // Mainnet's 1800 s VHTLC lock, less the SDK's 300 s claim margin and 300 s for paying and claiming.
    [Theory]
    [InlineData(15 * 60, true)]
    [InlineData(20 * 60, true)]
    [InlineData(20 * 60 + 1, false)]
    [InlineData(30 * 60, false)]
    public void AMainnetInvoiceMayExpireUpToTwentyMinutesAfterItIsCreated(int expirySeconds, bool fits)
    {
        var window = new SatoraClaimWindow(SatoraBitcoinNetwork.Mainnet).Current!.Value;

        Assert.Equal(TimeSpan.FromMinutes(20), SatoraClaimWindow.MaxInvoiceExpiry(window));
        Assert.Equal(fits, SatoraClaimWindow.Fits(TimeSpan.FromSeconds(expirySeconds), window));
    }

    // Off mainnet only each created swap shows the window, so nothing is refused before one exists.
    [Theory]
    [InlineData(SatoraBitcoinNetwork.Signet)]
    [InlineData(SatoraBitcoinNetwork.Regtest)]
    public void OffMainnetNothingIsRefusedBeforeASwapShowsTheWindow(SatoraBitcoinNetwork network)
    {
        Assert.Null(new SatoraClaimWindow(network).Current);
        Assert.Null(new SatoraClaimWindow(null).Current);
        // The default regtest and signet servers leave no time at all to claim.
        Assert.False(SatoraClaimWindow.Fits(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(120)));
    }

    [Fact]
    public void MeasuresTheWindowFromTheServersCreationTime()
    {
        var locktime = Now.AddSeconds(1800).ToUnixTimeSeconds();

        Assert.Equal(TimeSpan.FromSeconds(1800), SatoraClaimWindow.Observed("2026-10-02T12:00:00Z", locktime, Now.AddMinutes(3)));
        Assert.Equal(TimeSpan.FromSeconds(1800), SatoraClaimWindow.Observed("2026-10-02T14:00:00+02:00", locktime, Now.AddMinutes(3)));
        Assert.Equal(TimeSpan.FromSeconds(1620), SatoraClaimWindow.Observed("not a time", locktime, Now.AddMinutes(3)));
    }

    [Fact]
    public void TheClaimCutoffIsTheLocktimeLessTheSdksMargin()
    {
        var locktime = Now.ToUnixTimeSeconds();

        Assert.Equal(Now.AddSeconds(-300), SatoraClaimWindow.ClaimCutoff(locktime));
    }

    [Fact]
    public void TheInvoiceExpiresAtTheEarlierOfItsBolt11ExpiryAndTheClaimCutoff()
    {
        Assert.Equal(Now, SatoraClaimWindow.ExpiresAt(Now.AddHours(4), Now));
        Assert.Equal(Now, SatoraClaimWindow.ExpiresAt(Now, Now.AddMinutes(25)));
    }
}
