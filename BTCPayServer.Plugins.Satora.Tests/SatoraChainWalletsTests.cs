using System.Net;
using System.Text;
using System.Text.Json;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// The same bodies and expected rates as the SDK's fee tests (rust-sdk bitcoin/esplora.rs), so the
// plugin's estimate stays the SDK's refund rate at 140 vB.
public class SatoraChainWalletsTests : IDisposable
{
    private const string Esplora = "http://esplora.test/api";
    // Captured on 2026-10-05: fee-estimates from the regtest stack's mempool over Fulcrum (404) and
    // from mempool.space (203, its deprecation moved to a header), mempool.space's recommended fees,
    // and blockstream.info's 404 for recommended fees and its estimates.
    private const string RegtestFeeEstimates404 = "<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>Error</title>\n</head>\n<body>\n<pre>Cannot GET /api/v1/fee-estimates</pre>\n</body>\n</html>\n";
    private const string MempoolSpaceFeeEstimates203 = """{"1":1.269,"2":1.269,"3":0.786,"4":0.786,"5":0.786,"6":0.396,"7":0.396,"8":0.396,"9":0.396,"10":0.396,"11":0.396,"12":0.396,"13":0.396,"14":0.396,"15":0.396,"16":0.396,"17":0.396,"18":0.396,"19":0.396,"20":0.396,"21":0.396,"22":0.396,"23":0.396,"24":0.396,"25":0.396,"144":0.2,"504":0.2,"1008":0.1}""";
    private const string MempoolSpaceRecommended = """{"fastestFee":2,"halfHourFee":1,"hourFee":1,"economyFee":1,"minimumFee":1}""";
    private const string BlockstreamRecommended404 = "endpoint does not exist \"/v1/fees/recommended\"";
    private const string BlockstreamFeeEstimates = """{"6":0.381,"7":0.381,"2":1.066,"5":0.42200000000000004,"12":0.284,"11":0.284,"18":0.284,"20":0.284,"3":1.066,"17":0.284,"21":0.284,"15":0.284,"144":0.25,"9":0.301,"1":1.066,"13":0.284,"16":0.284,"24":0.284,"19":0.284,"8":0.301,"23":0.284,"14":0.284,"25":0.27499999999999997,"504":0.25,"22":0.284,"1008":0.09999999999999999,"10":0.301,"4":0.42200000000000004}""";
    // mempool.space's body on 2026-10-04, shortened, with the deprecation entry no strict parse accepts.
    private const string MempoolSpaceFeeEstimatesWarning = """{"1":2.012,"3":1.517,"6":0.493,"144":0.2,"warning":"This endpoint is deprecated and will be removed in a future release. Please use /api/v1/fees/precise instead."}""";
    // A rate for each target, so the one taken shows which target was read.
    private const string DistinctRecommended = """{"fastestFee":5,"halfHourFee":4,"hourFee":3,"economyFee":2,"minimumFee":1}""";

    private readonly SatoraWalletRegistry _registry;
    private readonly FakeEsplora _esplora = new();
    private readonly SatoraChainWallets _wallets;

    public SatoraChainWalletsTests()
    {
        var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
        var networks = new BTCPayNetworkProvider([new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") }], nbx, new Logs());
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [SatoraNetworks.EsploraUrlKey] = Esplora }).Build();
        // A connection string Npgsql rejects outright, so the registry's key load fails at once.
        var contexts = new SatoraPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = "NotAKeyword=1" }));
        _registry = new SatoraWalletRegistry(contexts, null!, null!, networks, configuration,
            Options.Create(new DataDirectories { DataDir = Path.GetTempPath() }), NullLogger<SatoraWalletRegistry>.Instance);
        _wallets = new SatoraChainWallets(null!, _registry, _esplora);
    }

    public void Dispose() => _registry.Dispose();

    [Theory]
    [InlineData(HttpStatusCode.NotFound, RegtestFeeEstimates404)]
    [InlineData(HttpStatusCode.OK, MempoolSpaceFeeEstimatesWarning)]
    [InlineData(HttpStatusCode.NonAuthoritativeInformation, MempoolSpaceFeeEstimates203)]
    public async Task ARefundFeeIsMempoolsRecommendedHourFeeWhateverItsFeeEstimatesSay(HttpStatusCode status, string feeEstimates)
    {
        _esplora.Serve("v1/fees/recommended", HttpStatusCode.OK, DistinctRecommended);
        _esplora.Serve("fee-estimates", status, feeEstimates);

        Assert.Equal(3 * 140, await _wallets.EstimateRefundFeeSatsAsync(CancellationToken.None));
        Assert.Equal(["/api/v1/fees/recommended"], _esplora.Requests);

        _esplora.Serve("v1/fees/recommended", HttpStatusCode.OK, MempoolSpaceRecommended);
        Assert.Equal(140, await _wallets.EstimateRefundFeeSatsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AnEsploraWithoutMempoolsApiGivesItsOwnEstimatesNeverBelowTheRelayFee()
    {
        _esplora.Serve("v1/fees/recommended", HttpStatusCode.NotFound, BlockstreamRecommended404, "text/plain");
        _esplora.Serve("fee-estimates", HttpStatusCode.OK, """{"1":4.0,"3":3.5,"6":2.004,"144":1.0}""");

        Assert.Equal(281, await _wallets.EstimateRefundFeeSatsAsync(CancellationToken.None));
        Assert.Equal(["/api/v1/fees/recommended", "/api/fee-estimates"], _esplora.Requests);

        _esplora.Serve("fee-estimates", HttpStatusCode.OK, BlockstreamFeeEstimates);
        Assert.Equal(140, await _wallets.EstimateRefundFeeSatsAsync(CancellationToken.None));
    }

    // As strict as the SDK, which fails rather than read a rate it cannot trust: only a 404 falls back.
    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "busy", typeof(HttpRequestException))]
    [InlineData(HttpStatusCode.OK, "{}", typeof(JsonException))]
    [InlineData(HttpStatusCode.OK, """{"fastestFee":2,"halfHourFee":1,"economyFee":1,"minimumFee":1}""", typeof(JsonException))]
    [InlineData(HttpStatusCode.OK, "null", typeof(FormatException))]
    [InlineData(HttpStatusCode.OK, """{"fastestFee":2,"halfHourFee":1,"hourFee":0,"economyFee":1,"minimumFee":1}""", typeof(FormatException))]
    public async Task AnyOtherAnswerToRecommendedFeesIsAnError(HttpStatusCode status, string body, Type error)
    {
        _esplora.Serve("v1/fees/recommended", status, body);
        _esplora.Serve("fee-estimates", HttpStatusCode.OK, """{"6":2.0}""");

        Assert.IsType(error, await Record.ExceptionAsync(() => _wallets.EstimateRefundFeeSatsAsync(CancellationToken.None)));
        Assert.Equal(["/api/v1/fees/recommended"], _esplora.Requests);
    }

    [Fact]
    public void TakesTheRateForTheMostBlocksUpToSixElseForTheFewest()
    {
        Assert.Equal(4 * 140, SatoraChainWallets.RefundFee([(1, 5), (3, 4)]));
        Assert.Equal(2 * 140, SatoraChainWallets.RefundFee([(25, 2), (144, 1)]));
        Assert.Equal(210, SatoraChainWallets.RefundFee([(1, 2), (3, 1.5)]));
        Assert.Equal(140, SatoraChainWallets.RefundFee([(1, 2), (3, 1.5), (6, 0.5)]));
        Assert.Throws<FormatException>(() => SatoraChainWallets.RefundFee([]));
        Assert.Throws<FormatException>(() => SatoraChainWallets.RefundFee([(6, -1)]));
        Assert.Throws<FormatException>(() => SatoraChainWallets.Estimates((Dictionary<string, double>?)null));
    }

    private sealed class FakeEsplora : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Dictionary<string, (HttpStatusCode Status, string Body, string MediaType)> _routes = [];

        public List<string> Requests { get; } = [];

        public void Serve(string path, HttpStatusCode status, string body, string? mediaType = null)
        {
            _routes[$"{Esplora}/{path}"] = (status, body, mediaType ?? (body.StartsWith('<') ? "text/html" : "application/json"));
            Requests.Clear();
        }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(request.RequestUri.AbsolutePath);
            var (status, body, mediaType) = _routes.TryGetValue(url, out var route) ? route : (HttpStatusCode.NotFound, "", "text/plain");
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
        }
    }
}
