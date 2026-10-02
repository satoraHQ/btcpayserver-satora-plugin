using System.Text;
using System.Web;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBitcoin.Crypto;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using Xunit;
using SdkLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;

namespace BTCPayServer.Plugins.Satora.Tests;

// Withdrawals to an LNURL through BTCPay's LNURL library and HTTP clients, against a payee served
// over plain http as the regtest lnurl-server serves it. The swap server must never be asked to
// resolve the destination: it refuses an http LNURL, cannot reach an onion one, and would learn the
// payee. It only quotes and pays the invoices the payee issued here.
public class SatoraLnurlWithdrawalTests
{
    private const string Store = "store";
    private const string Metadata = "[[\"text/plain\",\"Satora test payee\"]]";
    private static readonly Uri Endpoint = new("http://localhost:9090/lnurl/fef40fbec14165d5fdd14bc0a38ff142");
    private static readonly Uri OnionEndpoint = new("http://satoratestpayee2vkgxdnqhrqyx5dzpc7hw6ikbpgqrwbzu6mfxc6lyd.onion/lnurl/payee");

    private readonly OutgoingEvents _events = new();
    private readonly MemoryOutgoingSwaps _swaps;
    private readonly FakeWallet _wallet;
    private readonly MemoryWithdrawals _withdrawals;
    private readonly Payee _payee = new();
    private readonly Clients _clients;
    private readonly Service _service;

    public SatoraLnurlWithdrawalTests()
    {
        _swaps = new MemoryOutgoingSwaps(_events);
        _wallet = new FakeWallet(_events)
        {
            QuoteFor = QuoteOf,
            Create = destination =>
            {
                var bolt11 = ((SdkLightningDestination.Invoice)destination).invoice;
                return FakeWallet.ArkadeToLightning("swap", long.Parse(QuoteOf(destination).SourceAmount), bolt11);
            }
        };
        _withdrawals = new MemoryWithdrawals(_events, _swaps);
        var wallets = new FakeWalletAccess(_wallet, _events);
        _clients = new Clients(_payee);
        _service = new Service(wallets, _withdrawals, new SatoraLightningPayments(_swaps, wallets, NullLogger<SatoraLightningPayments>.Instance), _swaps, _clients);
    }

    [Fact]
    public async Task ReviewsAndSendsAnAmountToAnHttpLnurlThatTheSwapServerNeverSees()
    {
        var lnurl = LNURL.LNURL.EncodeBech32(Endpoint);

        var review = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = lnurl, AmountBtc = "0.00011" });
        Assert.True(review is { Error: null, Key: not null, AmountSats: 11_000, FeeSats: 33, TotalSats: 11_033 }, review.Error);
        var result = await _service.ExecuteAsync(Store, Request(lnurl, review, "0.00011"));

        Assert.Equal(SatoraWithdrawResultKind.Pending, result.Kind);
        Assert.Equal(2, _payee.Invoices.Count);
        Assert.All(_payee.Invoices, invoice => Assert.Equal(LightMoney.Satoshis(11_000), Parse(invoice).MinimumAmount));
        // The review quoted the first invoice; the withdrawal paid the second, bound to its payment hash.
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: true } swap && swap.Bolt11 == _payee.Invoices[1] &&
                    swap.PaymentHash == Parse(_payee.Invoices[1]).PaymentHash!.ToString());
        AssertTheSwapServerSawOnlyThePayeesInvoices();
        Assert.All(_clients.Names, name => Assert.Equal(LightningLikePayoutHandler.LightningLikePayoutHandlerClearnetNamedClient, name));
    }

    // The swap server's amount-only quote, which names no destination, says what the wallet's sats
    // pay after fees; the payee is then asked for an invoice of that amount.
    [Fact]
    public async Task SendsEverythingToAnHttpLnurlForWhatTheWalletCanSendAfterFees()
    {
        var lnurl = LNURL.LNURL.EncodeBech32(Endpoint);

        var review = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = lnurl, SendAll = true });
        Assert.True(review is { Error: null, AmountSats: 12_345, FeeSats: 38, TotalSats: 12_383 }, review.Error);
        var result = await _service.ExecuteAsync(Store, Request(lnurl, review, null));

        Assert.Equal(SatoraWithdrawResultKind.Pending, result.Kind);
        Assert.Equal(["send-max quote 100000", "send-max quote 100000 (gated)"], _events.All.Where(e => e.StartsWith("send-max", StringComparison.Ordinal)));
        Assert.Equal(_payee.Invoices[^1], _swaps.Rows["swap"].Bolt11);
        AssertTheSwapServerSawOnlyThePayeesInvoices();
    }

    [Fact]
    public async Task AsksAnOnionLnurlThroughBTCPaysTorClient()
    {
        var review = await _service.ReviewAsync(Store, new SatoraWithdrawRequest { Destination = LNURL.LNURL.EncodeBech32(OnionEndpoint), AmountBtc = "0.00011" });

        Assert.Null(review.Error);
        Assert.Equal([OnionEndpoint.ToString(), $"{OnionEndpoint}/callback?amount=11000000"], _payee.Requests);
        Assert.Equal([LightningLikePayoutHandler.LightningLikePayoutHandlerOnionNamedClient, LightningLikePayoutHandler.LightningLikePayoutHandlerOnionNamedClient],
            _clients.Names);
    }

    // The swap server is asked only for amount-only quotes and about invoices the payee issued.
    private void AssertTheSwapServerSawOnlyThePayeesInvoices()
    {
        var asked = _events.All.Where(e => e.StartsWith("quote", StringComparison.Ordinal) || e.StartsWith("create", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(asked);
        Assert.All(asked, e => Assert.Contains(_payee.Invoices, invoice => e.Contains(new SdkLightningDestination.Invoice(invoice).ToString(), StringComparison.Ordinal)));
    }

    private static SatoraWithdrawRequest Request(string destination, SatoraWithdrawReview review, string? amountBtc) => new()
    {
        Destination = destination,
        AmountBtc = amountBtc,
        SendAll = amountBtc is null,
        Key = review.Key,
        MaxTotalSats = review.TotalSats,
        MaxFeeSats = review.FeeSats
    };

    private static BOLT11PaymentRequest Parse(string bolt11) => BOLT11PaymentRequest.Parse(bolt11, Network.RegTest);

    // The swap server's quote for paying an invoice: 0.3% and no Lightning fee, as on regtest.
    private static LightningSendQuote QuoteOf(SdkLightningDestination destination)
    {
        var sats = Parse(((SdkLightningDestination.Invoice)destination).invoice).MinimumAmount.MilliSatoshi / 1000;
        var protocolFee = (long)Math.Ceiling(sats * 0.003m);
        return new LightningSendQuote("1", 1_000_000, 1_000, 0, 0.003, (ulong)protocolFee, (sats + protocolFee).ToString(), (ulong)(sats + protocolFee), (ulong)sats);
    }

    // An LNURL-pay endpoint and its callback, as arkade-lnurl answers them: from 1,000 to 100,000 sats,
    // each invoice committing to the endpoint's metadata.
    private sealed class Payee : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        public List<string> Invoices { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri.ToString());
            JObject body;
            if (uri.AbsolutePath.EndsWith("/callback", StringComparison.Ordinal))
            {
                var msat = long.Parse(HttpUtility.ParseQueryString(uri.Query)["amount"]!);
                var invoice = TestBolt11.Create(msat / 1000, Network.RegTest, Hashes.SHA256(Encoding.UTF8.GetBytes(Metadata)));
                Invoices.Add(invoice);
                body = new JObject { ["pr"] = invoice, ["routes"] = new JArray() };
            }
            else
            {
                body = new JObject
                {
                    ["tag"] = "payRequest",
                    ["callback"] = $"{uri.GetLeftPart(UriPartial.Path)}/callback",
                    ["minSendable"] = 1_000_000,
                    ["maxSendable"] = 100_000_000,
                    ["metadata"] = Metadata
                };
            }
            return Task.FromResult(new HttpResponseMessage { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") });
        }
    }

    // BTCPay's named HTTP clients, recording which one each request went through.
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private sealed class Service(SatoraWalletAccess wallets, SatoraWithdrawals withdrawals,
        SatoraLightningPayments payments, SatoraOutgoingSwaps outgoing, IHttpClientFactory clients)
        : SatoraWithdrawalService(wallets, withdrawals, payments, outgoing, null!, null!, null!, null!, clients, NullLogger<SatoraWithdrawalService>.Instance)
    {
        public override string? Unavailable => null;

        protected override Network Network => Network.RegTest;

        protected override Task NotifyUnknownAsync(SatoraWithdrawal withdrawal) => Task.CompletedTask;
    }
}
