using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using NBXplorer;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraChainRefundControllerTests
{
    private static readonly string Address = new Key().PubKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();

    private readonly RecordingSwaps _swaps = new();
    private readonly FakeWallets _wallets = new();
    private readonly RecordingCredit _credit = new();
    private readonly RecordingLogger<SatoraChainRefundController> _log = new();

    [Fact]
    public async Task RecordsTheOwnersAddressAndSaysWhenThePluginFirstTriesTheRefund()
    {
        var swap = Waiting();
        var controller = Controller();

        var result = await controller.RequestRefund("store", "swap", new SatoraChainRefundRequest { Address = $" {Address} ", Confirmation = "REFUND" });

        Assert.Equal(nameof(SatoraChainRefundController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal([("store", "swap", Address)], _swaps.Requests);
        // The first attempt fixes the address, so the store is told that time, not when median time past surely allows it.
        Assert.Contains(swap.BtcRefundLocktime.ToString("u"), (string)controller.TempData[WellKnownTempData.SuccessMessage]!);
    }

    [Theory]
    [InlineData("mainnet", "REFUND")]
    [InlineData("regtest", "refund")]
    [InlineData("regtest", null)]
    public async Task RefusesAnAddressOffTheStoresNetworkOrAMissingConfirmation(string network, string? confirmation)
    {
        Waiting();
        var address = network == "mainnet" ? new Key().PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.Main).ToString() : Address;
        var controller = Controller();

        await controller.RequestRefund("store", "swap", new SatoraChainRefundRequest { Address = address, Confirmation = confirmation });

        Assert.Empty(_swaps.Requests);
        Assert.NotNull(controller.TempData[WellKnownTempData.ErrorMessage]);
    }

    // The worker started the refund between the check and the write, which then changes nothing.
    [Fact]
    public async Task ReportsARefundTheWorkerStartedMeanwhile()
    {
        Waiting();
        _swaps.Accept = false;
        var controller = Controller();

        await controller.RequestRefund("store", "swap", new SatoraChainRefundRequest { Address = Address, Confirmation = "REFUND" });

        Assert.Contains("can no longer change", (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    [Theory]
    [InlineData("DISMISS", true)]
    [InlineData("REFUND", false)]
    [InlineData(null, false)]
    public async Task DismissesARefundOnlyWithTheTypedConfirmation(string? confirmation, bool dismissed)
    {
        Waiting();
        var controller = Controller();

        var result = await controller.Dismiss("store", "swap", new SatoraChainRefundRequest { Confirmation = confirmation });

        Assert.Equal(nameof(SatoraChainRefundController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal(dismissed ? [("store", "swap")] : [], _swaps.Dismissals);
        Assert.NotNull(controller.TempData[dismissed ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage]);
        Assert.Equal(dismissed ? [("invoice-swap", InvoiceEventData.EventSeverity.Info)] : [], _credit.Notes.Select(n => (n.InvoiceId, n.Severity)));
        Assert.All(_credit.Notes, n => Assert.Contains("dismissed", n.Message));
        Assert.Equal(dismissed ? ["The refund of Satora on-chain swap swap of store store, of invoice invoice-swap, was dismissed; the plugin no longer follows the swap"] : [],
            _log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message));
    }

    // The worker's refund call may broadcast: a dismissal is decided only once it has ended.
    [Fact]
    public async Task RefusesADismissalWhileARefundAttemptRuns()
    {
        Waiting();
        var during = Controller();
        using (await _swaps.HoldForRefundAsync("swap", TestContext.Current.CancellationToken))
            await during.Dismiss("store", "swap", new SatoraChainRefundRequest { Confirmation = "DISMISS" });

        Assert.Empty(_swaps.Dismissals);
        Assert.Empty(_credit.Notes);
        Assert.Contains("try again in a minute", (string)during.TempData[WellKnownTempData.ErrorMessage]!);
        await Controller().Dismiss("store", "swap", new SatoraChainRefundRequest { Confirmation = "DISMISS" });
        Assert.Equal([("store", "swap")], _swaps.Dismissals);
    }

    // A refund already broadcast, or one whose outcome was never learned, can still confirm.
    [Theory]
    [InlineData(null, false, ". Nothing at its address is sent back.")]
    [InlineData(null, true, ". An earlier refund attempt may still confirm, but nothing more is sent from its address.")]
    [InlineData("refund-1", true, ". Its refund in refund-1 already went out, and nothing more is sent from its address.")]
    public async Task NotesWhatADismissalLeavesToConfirm(string? refundTxId, bool attempted, string leaves)
    {
        var swap = Waiting();
        swap.RefundAddress = Address;
        swap.RefundAttempted = attempted;
        swap.RefundTxId = refundTxId;

        await Controller().Dismiss("store", "swap", new SatoraChainRefundRequest { Confirmation = "DISMISS" });

        Assert.EndsWith(leaves, Assert.Single(_credit.Notes).Message);
    }

    [Fact]
    public async Task DismissesNothingInAnotherStore()
    {
        Waiting();
        var controller = Controller();

        await controller.Dismiss("other-store", "swap", new SatoraChainRefundRequest { Confirmation = "DISMISS" });

        Assert.Empty(_swaps.Dismissals);
        Assert.Contains("no such", (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    // Refusing it as running would tell another store that this swap's refund is in flight.
    [Fact]
    public async Task AnotherStoresSwapIsNotFoundWhileItsRefundAttemptRuns()
    {
        Waiting();
        var controller = Controller();
        using (await _swaps.HoldForRefundAsync("swap", TestContext.Current.CancellationToken))
            await controller.Dismiss("other-store", "swap", new SatoraChainRefundRequest { Confirmation = "DISMISS" });

        Assert.Empty(_swaps.Dismissals);
        Assert.Contains("no such", (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    // What a refund sends is what sits at the address, which for an underpayment is not the deposit.
    [Fact]
    public async Task ShowsWhatSitsAtEachSwapsAddressAndStillListsOneItCannotRead()
    {
        var underpaid = Waiting("underpaid");
        underpaid.DepositSats = 21_221;
        _wallets.Outputs[underpaid.HtlcAddress] = [new SatoraHtlcOutput("funding", 0, 16_000, true)];
        _wallets.ReadErrors[Waiting("unreadable").HtlcAddress] = new HttpRequestException("esplora down");
        Waiting("refunded").RefundTxId = "refund-1";

        var model = Assert.IsType<SatoraChainRefundPageModel>(Assert.IsType<ViewResult>(await Controller().Index("store")).Model);

        Assert.Equal(["underpaid", "unreadable", "refunded"], model.Swaps.Select(s => s.SwapId));
        Assert.Equal(16_000, model.Outputs["underpaid"]!.Sum(o => o.ValueSats));
        Assert.Null(model.Outputs["unreadable"]);
        Assert.False(model.Outputs.ContainsKey("refunded"));
        Assert.Equal(["htlc-underpaid", "htlc-unreadable"], _wallets.Reads.Select(r => r.Address).Order());
        // Each read has a timeout of its own, so a slow esplora cannot hold the page.
        Assert.All(_wallets.Reads, r => Assert.True(r.Token.CanBeCanceled));
    }

    private SatoraChainSwap Waiting(string swapId = "swap")
    {
        var swap = SatoraChainActionsTests.Swap(swapId: swapId, needsRefund: true);
        _swaps.Rows[swap.SwapId] = swap;
        return swap;
    }

    private SatoraChainRefundController Controller()
    {
        var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
        var networks = new BTCPayNetworkProvider([new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") }], nbx, new Logs());
        var http = new DefaultHttpContext();
        return new SatoraChainRefundController(_swaps, _wallets, _credit, networks, _log)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new MemoryTempDataProvider())
        };
    }

    private sealed class RecordingSwaps() : SatoraChainSwaps(null!)
    {
        public Dictionary<string, SatoraChainSwap> Rows { get; } = [];
        public List<(string StoreId, string SwapId, string Address)> Requests { get; } = [];
        public List<(string StoreId, string SwapId)> Dismissals { get; } = [];
        public bool Accept { get; set; } = true;

        public override Task<bool> DismissRefundAsync(string storeId, string swapId, DateTimeOffset now)
        {
            Dismissals.Add((storeId, swapId));
            return Task.FromResult(true);
        }

        public override Task<SatoraChainSwap?> FindAsync(string storeId, string swapId) =>
            Task.FromResult(Rows.TryGetValue(swapId, out var row) && row.StoreId == storeId ? row : null);

        public override Task<List<SatoraChainSwap>> ListNeedingRefundAsync(string storeId) =>
            Task.FromResult(Rows.Values.Where(s => s.StoreId == storeId && s.NeedsRefundAt != null && s.CompletedAt == null).ToList());

        public override Task<bool> RequestRefundAsync(string storeId, string swapId, string address)
        {
            if (Accept)
                Requests.Add((storeId, swapId, address));
            return Task.FromResult(Accept);
        }
    }

    private sealed class FakeWallets() : SatoraChainWallets(null!, null!, null!)
    {
        public Dictionary<string, IReadOnlyList<SatoraHtlcOutput>> Outputs { get; } = [];
        public Dictionary<string, Exception> ReadErrors { get; } = [];
        public List<(string Address, CancellationToken Token)> Reads { get; } = [];

        public override Task<IReadOnlyList<SatoraHtlcOutput>> ListUnspentAsync(string address, CancellationToken cancellationToken)
        {
            Reads.Add((address, cancellationToken));
            return ReadErrors.TryGetValue(address, out var error)
                ? Task.FromException<IReadOnlyList<SatoraHtlcOutput>>(error)
                : Task.FromResult(Outputs.GetValueOrDefault(address) ?? []);
        }
    }

    private sealed class RecordingCredit() : SatoraChainCredit(null!, null!, null!, null!, NullLogger<SatoraChainCredit>.Instance)
    {
        public List<(string InvoiceId, InvoiceEventData.EventSeverity Severity, string Message)> Notes { get; } = [];

        public override Task NoteAsync(SatoraChainSwap swap, string message, InvoiceEventData.EventSeverity severity)
        {
            Notes.Add((swap.InvoiceId, severity, message));
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
