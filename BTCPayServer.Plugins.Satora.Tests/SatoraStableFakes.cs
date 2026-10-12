using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Stable;
using Microsoft.Extensions.Configuration;
using Satora.Sdk;

namespace BTCPayServer.Plugins.Satora.Tests;

public static class SatoraStableFakes
{
    // A typical payment of 100 000 sats costs 10 000 000 units, 10 USDC: 100 units a sat before fees.
    public static Quote Quote(double protocolFeeRate = 0.0025, ulong networkFee = 300, ulong minAmount = 1_000, ulong maxAmount = 100_000_000) => new(
        "0.00001",
        networkFee,
        0,
        (ulong)(SatoraStableActions.TypicalSats * protocolFeeRate),
        protocolFeeRate,
        minAmount,
        maxAmount,
        "10000000",
        SatoraStableActions.TypicalSats.ToString(),
        "10055000",
        SatoraStableActions.TypicalSats.ToString(),
        null);

    // The swap creation, which needs the store's wallet and the swap server, answers as the server would.
    public sealed class Handler(BTCPayNetwork network, IConfiguration? configuration = null, SwapTable? swapTable = null)
        : SatoraStablePaymentHandler(SatoraStablePaymentMethod.UsdcArbitrum, network, null!, swapTable ?? new SwapTable(), null!, configuration ?? new ConfigurationBuilder().Build())
    {
        public List<long> Requested { get; } = [];

        public List<SatoraStableSwap> Swaps { get; } = [];

        protected override Task<SatoraStableSwap> CreateSwapAsync(string storeId, string invoiceId, long sats, DateTimeOffset invoiceExpiresAt,
            decimal paymentTolerance)
        {
            Requested.Add(sats);
            var swap = SatoraStableIntegrationTests.Swap(storeId, $"swap{Requested.Count}", deposit: $"0x{Requested.Count:x40}");
            swap.InvoiceId = invoiceId;
            swap.ReceiveSats = sats;
            swap.InvoiceExpiresAt = invoiceExpiresAt;
            Swaps.Add(swap);
            return Task.FromResult(swap);
        }
    }

    // The swaps table, as far as the payment handler reads it before making a swap: when the server
    // made its last swap and that swap's VHTLC window, none by default.
    public sealed class SwapTable() : SatoraStableSwaps(null!)
    {
        public (DateTimeOffset CreatedAt, TimeSpan VhtlcWindow)? LastSwap { get; set; }

        public override Task<TimeSpan?> LastVhtlcWindowAsync(string paymentMethodId, DateTimeOffset since) =>
            Task.FromResult(LastSwap is { } last && last.CreatedAt >= since ? last.VhtlcWindow : (TimeSpan?)null);
    }

    // The swap server's quotes, without a wallet.
    public sealed class Quotes() : SatoraStableQuotes(null!)
    {
        public Quote Quote { get; set; } = SatoraStableFakes.Quote();

        public Exception? Failure { get; set; }

        public List<SatoraStablecoin> Reads { get; } = [];

        public Quote? Last { get; set; }

        public override Quote? LastRead(SatoraStablecoin coin) => Last;

        public override Task<Quote> GetAsync(string storeId, SatoraStablecoin coin, CancellationToken cancellationToken)
        {
            Reads.Add(coin);
            return Failure is null ? Task.FromResult(Quote) : Task.FromException<Quote>(Failure);
        }
    }

    // A JSON-RPC node on loopback answering eth_chainId with chainId and null to anything else,
    // recording each method asked for.
    public sealed class EvmNode : IDisposable
    {
        private readonly System.Net.HttpListener _listener = new();

        public System.Collections.Concurrent.ConcurrentQueue<string> Methods { get; } = new();

        public string Url { get; }

        public EvmNode(long chainId)
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    System.Net.HttpListenerContext context;
                    try
                    {
                        context = await _listener.GetContextAsync();
                    }
                    catch (Exception) when (!_listener.IsListening)
                    {
                        return;
                    }
                    using var reader = new StreamReader(context.Request.InputStream);
                    using var request = System.Text.Json.JsonDocument.Parse(await reader.ReadToEndAsync());
                    var method = request.RootElement.GetProperty("method").GetString()!;
                    Methods.Enqueue(method);
                    var result = method == "eth_chainId" ? $"\"0x{chainId:x}\"" : "null";
                    var body = System.Text.Encoding.UTF8.GetBytes(
                        $"{{\"jsonrpc\":\"2.0\",\"id\":{request.RootElement.GetProperty("id").GetRawText()},\"result\":{result}}}");
                    context.Response.ContentType = "application/json";
                    await context.Response.OutputStream.WriteAsync(body);
                    context.Response.Close();
                }
            });
        }

        public void Dispose() => _listener.Close();
    }
}
