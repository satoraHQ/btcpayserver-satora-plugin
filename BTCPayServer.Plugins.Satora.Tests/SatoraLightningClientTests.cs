using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Satora.Sdk;
using Xunit;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;
using SdkSwapDirection = uniffi.satora_sdk_ffi.SwapDirection;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;
using SdkTokenId = uniffi.satora_sdk_ffi.TokenId;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraLightningClientTests : IDisposable
{
    private const string Key = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string _dataDir = Directory.CreateTempSubdirectory("satora-ln-test-").FullName;
    private readonly SatoraWalletRegistry _registry;
    private readonly SatoraLightningConnectionStringHandler _handler;

    public SatoraLightningClientTests()
    {
        var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
        var networks = new BTCPayNetworkProvider([new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") }], nbx, new Logs());
        // A connection string Npgsql rejects outright, so the registry's key load fails at once rather than
        // retrying, and the registry holds only the keys set here.
        var contexts = new SatoraPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = "NotAKeyword=1" }));
        _registry = new SatoraWalletRegistry(contexts, null!, null!, networks, new ConfigurationBuilder().Build(),
            Options.Create(new DataDirectories { DataDir = _dataDir }), NullLogger<SatoraWalletRegistry>.Instance);
        _registry.SetLightningKey("store", Key);
        _handler = new SatoraLightningConnectionStringHandler(_registry, null!, new SatoraLightningInvoiceHub(),
            new SatoraClaimWindow(SatoraBitcoinNetwork.Regtest), null!, NullLogger<SatoraLightningClient>.Instance);
    }

    public void Dispose()
    {
        _registry.Dispose();
        Directory.Delete(_dataDir, true);
    }

    [Fact]
    public void CreatesAClientOnlyForTheStoresCurrentKey()
    {
        var connectionString = SatoraLightningConnectionString.Format("store", Key);

        var client = _handler.Create(connectionString, Network.RegTest, out var error);

        Assert.Null(error);
        // BTCPay saves client.ToString() as the connection string (LightningPaymentMethodConfig.SetLightningUrl).
        Assert.Equal(connectionString, Assert.IsType<SatoraLightningClient>(client).ToString());
        Assert.Equal(connectionString, new LightningClientFactory([_handler], Network.RegTest).Create(connectionString).ToString());
    }

    [Theory]
    [InlineData("type=satora;storeid=store;key=" + "1123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("type=satora;storeid=store;key=0123")]
    [InlineData("type=satora;storeid=store")]
    [InlineData("type=satora;storeid=other;key=" + Key)]
    [InlineData("type=satora;key=" + Key)]
    public void RefusesAStringWithoutTheStoresKey(string connectionString)
    {
        Assert.Null(_handler.Create(connectionString, Network.RegTest, out var error));
        Assert.NotNull(error);
        Assert.ThrowsAny<FormatException>(() => new LightningClientFactory([_handler], Network.RegTest).Create(connectionString));
    }

    [Fact]
    public void LeavesOtherConnectionStringsToTheirHandlers()
    {
        Assert.Null(_handler.Create("type=lnd-rest;server=https://lnd.example;macaroon=00", Network.RegTest, out var error));
        Assert.Null(error);
    }

    [Fact]
    public void RefusesANetworkTheWalletDoesNotRunOn()
    {
        Assert.Null(_handler.Create(SatoraLightningConnectionString.Format("store", Key), Network.Main, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public async Task AClientStopsCreatingInvoicesOnceItsKeyIsRotated()
    {
        var client = _handler.Create(SatoraLightningConnectionString.Format("store", Key), Network.RegTest, out _)!;

        _registry.SetLightningKey("store", new string('f', 64));

        Assert.Null(_handler.Create(SatoraLightningConnectionString.Format("store", Key), Network.RegTest, out _));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            client.CreateInvoice(new CreateInvoiceParams(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(15)), Ct));
    }

    [Fact]
    public void TellsWhichStoreASatoraStringNames()
    {
        Assert.Equal("store", SatoraLightningConnectionString.StoreIdOf("type=satora;storeid=store;key=old"));
        Assert.Equal("store", SatoraLightningConnectionString.StoreIdOf("TYPE=Satora;storeid=store"));
        Assert.Null(SatoraLightningConnectionString.StoreIdOf("type=lnd-rest;server=https://lnd.example;macaroon=00"));
        Assert.Null(SatoraLightningConnectionString.StoreIdOf("not a connection string"));
        Assert.Null(SatoraLightningConnectionString.StoreIdOf("type=satora;storeid"));
        Assert.Null(SatoraLightningConnectionString.StoreIdOf(";;="));
        Assert.Null(SatoraLightningConnectionString.StoreIdOf(null));
    }

    // The registry, wallet and swap table are all null: a refusal that reached the backend would throw something else.
    [Fact]
    public async Task RefusesInvoicesTheSwapCannotHonourBeforeCreatingAnything()
    {
        var client = Offline(SatoraBitcoinNetwork.Mainnet);

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.CreateInvoice(new CreateInvoiceParams(LightMoney.Zero, "top-up", TimeSpan.FromMinutes(15)), Ct));
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.CreateInvoice(new CreateInvoiceParams(LightMoney.Satoshis(1000), "lnurl", TimeSpan.FromMinutes(15)) { DescriptionHashOnly = true }, Ct));
#pragma warning disable CS0618 // The older way to ask for a description hash; BTCPay itself now sets DescriptionHashOnly.
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.CreateInvoice(new CreateInvoiceParams(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(15)) { DescriptionHash = new uint256(1) }, Ct));
#pragma warning restore CS0618
        var tooLong = await Assert.ThrowsAsync<NotSupportedException>(() =>
            client.CreateInvoice(new CreateInvoiceParams(LightMoney.Satoshis(1000), "order", TimeSpan.FromMinutes(21)), Ct));
        Assert.Contains("20 minutes", tooLong.Message);
    }

    // The registry, wallet and swap table are all null: a refusal that reached them would throw something else.
    [Fact]
    public async Task RefusesWhatItCannotPayBeforeTouchingTheWallet()
    {
        var client = Offline(SatoraBitcoinNetwork.Mainnet);

        Assert.Equal(PayResult.Error, (await client.Pay("lnbc1", Ct)).Result);
        Assert.Equal(PayResult.Error, (await client.Pay("lnbc1", new PayInvoiceParams(), Ct)).Result);
        Assert.Equal(PayResult.Error, (await client.Pay(new PayInvoiceParams { PaymentHash = new uint256(1) }, Ct)).Result);
    }

    // LightningLikePaymentHandler.GetNodeInfo treats NotSupportedException as "no node info" and skips the
    // block height check that BlockHeight 0 would fail.
    [Fact]
    public async Task GetInfoIsNotSupported()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Offline(SatoraBitcoinNetwork.Mainnet).GetInfo(Ct));
    }

    [Fact]
    public void ReportsConfirmedSatsAsLocalAndPreConfirmedAsOpening()
    {
        var balance = SatoraLightningClient.ToNodeBalance(new ArkadeBalance(1000, 200, 30));

        Assert.Null(balance.OnchainBalance);
        Assert.Equal(LightMoney.Satoshis(1000), balance.OffchainBalance.Local);
        Assert.Equal(LightMoney.Satoshis(200), balance.OffchainBalance.Opening);
        Assert.Null(balance.OffchainBalance.Remote);
        Assert.Null(balance.OffchainBalance.Closing);
    }

    [Fact]
    public void RecordsAClaimableSwapForAnInvoiceThatFits()
    {
        var row = Row(Created("11875", TimeSpan.FromMinutes(30)), 12_345, out var refusal);

        Assert.Null(refusal);
        Assert.False(row.DoNotClaim);
        Assert.Equal(("swap", "store", SatoraLightningCreditTests.PaymentHash), (row.SwapId, row.StoreId, row.PaymentHash));
        Assert.Equal((12_345_000L, 11_875L), (row.AmountMsat, row.ReceiveSats));
        Assert.Equal(Now.AddMinutes(25), row.ClaimCutoff);
    }

    // Each refused swap is still recorded, do-not-claim, so a payment that reaches it anyway is never
    // claimed against an invoice BTCPay was told failed.
    [Theory]
    [InlineData(12_000UL, "11000", 30, typeof(InvalidOperationException))]
    [InlineData(12_345UL, "11874", 30, typeof(InvalidOperationException))]
    [InlineData(12_345UL, "12345000", 30, typeof(InvalidOperationException))]
    [InlineData(12_345UL, "12.345", 30, typeof(InvalidOperationException))]
    [InlineData(12_345UL, "0", 30, typeof(InvalidOperationException))]
    [InlineData(12_345UL, "12000", 2, typeof(NotSupportedException))]
    public void RecordsARefusedSwapDoNotClaim(ulong sats, string receiveAmount, int windowMinutes, Type refusalType)
    {
        var row = Row(Created(receiveAmount, TimeSpan.FromMinutes(windowMinutes)), sats, out var refusal);

        Assert.IsType(refusalType, refusal);
        Assert.True(row.DoNotClaim);
        Assert.Equal("swap", row.SwapId);
    }

    [Fact]
    public void RefusesAResponseWithoutAnInvoiceBeforeRecordingAnything()
    {
        var created = Created("12000", TimeSpan.FromMinutes(30)) with { Funding = new SdkSwapFunding.ArkadeAddress("tark1") };

        Assert.Throws<InvalidOperationException>(() => Row(created, 12_345, out _));
    }

    // The server's defaults for this route keep 0.3% plus a sat; a receive amount in the wrong unit
    // keeps nearly everything.
    [Theory]
    [InlineData(1_000L)]
    [InlineData(12_345L)]
    [InlineData(1_000_000L)]
    public void TheFeeCeilingAllowsTheServersFeesAndCatchesUnitErrors(long dueSats)
    {
        Assert.Equal(dueSats * 3 / 100 + 100, SatoraLightningClient.MaxFeeSats(dueSats));
        Assert.True(dueSats * 30 / 10_000 + 2 <= SatoraLightningClient.MaxFeeSats(dueSats));
        Assert.True(dueSats - dueSats / 1000 > SatoraLightningClient.MaxFeeSats(dueSats));
    }

    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static SatoraLightningSwap Row(SwapDetails created, ulong sats, out Exception? refusal) =>
        SatoraLightningClient.ToRow(created, "store", Network.Main, TimeSpan.FromMinutes(15), sats, Now, out refusal);

    // A swap of the mainnet invoice for 12 345 sats, created now on the server's clock.
    private static SwapDetails Created(string receiveAmount, TimeSpan window) => new(
        "swap", new SdkSwapStatus.Pending(), new SdkSwapDirection.LightningToArkade(),
        new SdkSwapFunding.Bolt11Invoice(SatoraLightningCreditTests.Bolt11), "12345", new SdkTokenId.Btc(),
        "tark1", receiveAmount, new SdkTokenId.Btc(), Now.ToString("O"), (Now + window).ToUnixTimeSeconds(), null, null, null);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SatoraLightningClient Offline(SatoraBitcoinNetwork network) => new(
        "store", Key, Network.Main, null!, null!, new SatoraLightningInvoiceHub(), new SatoraClaimWindow(network),
        new SatoraLightningPayments(null!, null!, null!, NullLogger<SatoraLightningPayments>.Instance), NullLogger<SatoraLightningClient>.Instance);
}
