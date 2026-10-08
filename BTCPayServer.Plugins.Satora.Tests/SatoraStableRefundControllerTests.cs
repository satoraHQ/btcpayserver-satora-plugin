using System.Numerics;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Stable;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Satora.Sdk;
using Xunit;
using Addresses = BTCPayServer.Plugins.Satora.Tests.SatoraStableRefundsTests;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraStableRefundControllerTests
{
    private readonly RecordingSwaps _swaps = new();
    private readonly FakeWallets _wallets = new();
    private readonly RecordingCredit _credit = new();
    private readonly RecordingLogger<SatoraStableRefundController> _log = new();

    [Fact]
    public async Task RecordsTheOwnersDestinationAndSaysWhatGasToSend()
    {
        Waiting();
        var controller = Controller();

        var result = await controller.RequestRefund("store", "swap",
            new SatoraStableRefundRequest { Destination = $" {Addresses.Customer} ", Token = "0xAF88D065E77C8CC2239327C5EDB3A432268E5831", Confirmation = "REFUND" });

        Assert.Equal(nameof(SatoraStableRefundController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
        // The swap's own token, named, is recorded as none, which the worker and the SDK sweep.
        Assert.Equal([("store", "swap", Addresses.Customer, (string?)null)], _swaps.Requests);
        Assert.Contains("send it about 0.0001 ETH on Arbitrum One from any wallet", (string)controller.TempData[WellKnownTempData.SuccessMessage]!);
    }

    [Fact]
    public async Task RecordsAnotherTokenSentByMistake()
    {
        Waiting();

        await Controller().RequestRefund("store", "swap",
            new SatoraStableRefundRequest { Destination = Addresses.Customer, Token = $" {Addresses.OtherToken}", Confirmation = "REFUND" });

        Assert.Equal([("store", "swap", Addresses.Customer, (string?)Addresses.OtherToken)], _swaps.Requests);
    }

    [Theory]
    [InlineData("0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAeD", null, "REFUND", "including any capital letters")]
    [InlineData(Addresses.Deposit, null, "REFUND", "cannot be the deposit address")]
    [InlineData(Addresses.Customer, "USDC", "REFUND", "contract of the token")]
    [InlineData(Addresses.Customer, null, "refund", "Type REFUND")]
    public async Task RefusesADestinationThatWouldLoseTheTokensOrAMissingConfirmation(string destination, string? token, string confirmation, string says)
    {
        Waiting();
        var controller = Controller();

        await controller.RequestRefund("store", "swap", new SatoraStableRefundRequest { Destination = destination, Token = token, Confirmation = confirmation });

        Assert.Empty(_swaps.Requests);
        Assert.Contains(says, (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    [Fact]
    public async Task RefusesASweepOfADepositInTheHtlc()
    {
        Waiting().AttentionReason = SatoraStableAttention.HtlcRefund;
        var controller = Controller();

        await controller.RequestRefund("store", "swap", new SatoraStableRefundRequest { Destination = Addresses.Customer, Confirmation = "REFUND" });

        Assert.Empty(_swaps.Requests);
        Assert.Contains("locked in the swap contract", (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    // The worker started the sweep between the check and the write, which then changes nothing.
    [Fact]
    public async Task ReportsASweepTheWorkerStartedMeanwhile()
    {
        Waiting();
        _swaps.Accept = false;
        var controller = Controller();

        await controller.RequestRefund("store", "swap", new SatoraStableRefundRequest { Destination = Addresses.Customer, Confirmation = "REFUND" });

        Assert.Contains("can no longer change", (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    [Theory]
    [InlineData("DISMISS", true)]
    [InlineData("REFUND", false)]
    [InlineData(null, false)]
    public async Task DismissesOnlyWithTheTypedConfirmation(string? confirmation, bool dismissed)
    {
        Waiting();
        var controller = Controller();

        await controller.Dismiss("store", "swap", new SatoraStableRefundRequest { Confirmation = confirmation });

        Assert.Equal(dismissed ? [("store", "swap")] : [], _swaps.Dismissals);
        Assert.NotNull(controller.TempData[dismissed ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage]);
        Assert.Equal(dismissed ? [("invoice-swap", InvoiceEventData.EventSeverity.Info)] : [], _credit.Notes.Select(n => (n.InvoiceId, n.Severity)));
        Assert.All(_credit.Notes, n => Assert.Contains("was dismissed", n.Message));
        Assert.Equal(dismissed ? ["Sending back the tokens of Satora stablecoin swap swap of store store, of invoice invoice-swap, was dismissed; the plugin no longer follows the swap"] : [],
            _log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message));
    }

    // The worker's sweep call may send: a dismissal is decided only once it has ended.
    [Fact]
    public async Task RefusesADismissalWhileASweepRuns()
    {
        Waiting();
        var during = Controller();
        using (await _swaps.HoldForSweepAsync("swap", TestContext.Current.CancellationToken))
            await during.Dismiss("store", "swap", new SatoraStableRefundRequest { Confirmation = "DISMISS" });

        Assert.Empty(_swaps.Dismissals);
        Assert.Contains("try again in a minute", (string)during.TempData[WellKnownTempData.ErrorMessage]!);
        await Controller().Dismiss("store", "swap", new SatoraStableRefundRequest { Confirmation = "DISMISS" });
        Assert.Equal([("store", "swap")], _swaps.Dismissals);
    }

    // Refusing it as running would tell another store that this swap's sweep is in flight.
    [Fact]
    public async Task AnotherStoresSwapIsNotFoundWhileItsSweepRuns()
    {
        Waiting();
        var controller = Controller();
        using (await _swaps.HoldForSweepAsync("swap", TestContext.Current.CancellationToken))
            await controller.Dismiss("other-store", "swap", new SatoraStableRefundRequest { Confirmation = "DISMISS" });

        Assert.Empty(_swaps.Dismissals);
        Assert.Contains("no such", (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    // What a sweep sends is the whole balance of the chosen token, which for an underpayment is not the deposit.
    [Fact]
    public async Task ShowsWhatEachAddressToSweepHoldsAndStillListsOneItCannotRead()
    {
        Waiting("underpaid");
        _wallets.Funds[("store-underpaid", null)] = new SatoraDepositorFunds(4_000_000, 0);
        var wrongToken = Waiting("wrong-token");
        wrongToken.SweepToken = Addresses.OtherToken;
        _wallets.Funds[("store-wrong-token", Addresses.OtherToken)] = new SatoraDepositorFunds(5, BigInteger.Pow(10, 14));
        Waiting("unreadable");
        Waiting("htlc").AttentionReason = SatoraStableAttention.HtlcRefund;

        var model = Assert.IsType<SatoraStableRefundPageModel>(Assert.IsType<ViewResult>(await Controller().Index("store")).Model);

        Assert.Equal(["store-underpaid", "store-wrong-token", "store-unreadable", "store-htlc"], model.Swaps.Select(s => s.SwapId));
        Assert.Equal(new SatoraDepositorFunds(4_000_000, 0), model.Funds["store-underpaid"]);
        Assert.Equal(new SatoraDepositorFunds(5, BigInteger.Pow(10, 14)), model.Funds["store-wrong-token"]);
        Assert.Null(model.Funds["store-unreadable"]);
        // A deposit in the HTLC is not at the address.
        Assert.False(model.Funds.ContainsKey("store-htlc"));
        // Each read has a timeout of its own, so a slow node cannot hold the page.
        Assert.All(_wallets.Reads, r => Assert.True(r.Token.CanBeCanceled));
    }

    [Fact]
    public async Task ACheckListsAClosedSwapWhoseAddressReceivedTokensAgainAndNotesIt()
    {
        var swap = Closed("late");
        _wallets.Funds[(swap.SwapId, null)] = new SatoraDepositorFunds(2_500_000, 0);
        var controller = Controller();

        await controller.CheckAddress("store", new SatoraStableCheckRequest { Id = $" {swap.SwapId} " });

        Assert.Equal([(swap.SwapId, swap.CompletedAt!.Value, (string?)null)], _swaps.Reopens);
        Assert.Equal($"The deposit address of swap {swap.SwapId} holds 2.5 USDC. The swap is listed below for a refund.",
            controller.TempData[WellKnownTempData.SuccessMessage]);
        var note = Assert.Single(_credit.Notes);
        Assert.Equal((swap.InvoiceId, InvoiceEventData.EventSeverity.Warning), (note.InvoiceId, note.Severity));
        // Read under the registry's use, so a seed replace waits for the reopen and counts it.
        Assert.Equal([swap.SwapId], _wallets.ReadsUnderUse);
    }

    // The page stops waiting for a slow read, which still reopens the swap: the invoice says so.
    [Fact]
    public async Task AReopenThatFinishesAfterThePageStoppedWaitingIsStillNoted()
    {
        var swap = Closed("late");
        _wallets.Funds[(swap.SwapId, null)] = new SatoraDepositorFunds(2_500_000, 0);
        _wallets.Gate = new TaskCompletionSource();
        using var aborted = new CancellationTokenSource();
        var controller = Controller();
        controller.HttpContext.RequestAborted = aborted.Token;

        var check = controller.CheckAddress("store", new SatoraStableCheckRequest { Id = swap.SwapId });
        await aborted.CancelAsync();
        await check;

        Assert.Equal($"The deposit address of swap {swap.SwapId} could not be read.", controller.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Empty(_credit.Notes);

        _wallets.Gate.SetResult();
        await _wallets.LastReadUnderUse!;

        Assert.Equal([(swap.SwapId, swap.CompletedAt!.Value, (string?)null)], _swaps.Reopens);
        var note = Assert.Single(_credit.Notes);
        Assert.Equal((swap.InvoiceId, InvoiceEventData.EventSeverity.Warning), (note.InvoiceId, note.Severity));
        Assert.Contains("held 2.5 USDC after the swap closed", note.Message);
    }

    // An invoice's renewed prompts each had a swap of their own. The current wallet's SDK storage has no
    // record of a swap a replaced seed made, so that one is refused once the SDK says so.
    [Fact]
    public async Task ACheckByInvoiceReadsEveryClosedSwapOfTheInvoiceAndRefusesOneOfAReplacedWallet()
    {
        var empty = Closed("empty");
        var other = Closed("other-token");
        var replaced = Closed("replaced");
        var open = Open("open");
        var waiting = Waiting("waiting");
        foreach (var swap in new[] { empty, other, replaced, open, waiting })
            swap.InvoiceId = "invoice";
        _wallets.Replaced.Add(replaced.SwapId);
        _wallets.Funds[(empty.SwapId, Addresses.OtherToken)] = new SatoraDepositorFunds(0, 0);
        _wallets.Funds[(other.SwapId, Addresses.OtherToken)] = new SatoraDepositorFunds(3, 0);
        var controller = Controller();

        await controller.CheckAddress("store", new SatoraStableCheckRequest { Id = "invoice", Token = Addresses.OtherToken });

        Assert.Equal([empty.SwapId, other.SwapId, replaced.SwapId], _wallets.ReadsUnderUse);
        Assert.Equal([(other.SwapId, other.CompletedAt!.Value, (string?)Addresses.OtherToken)], _swaps.Reopens);
        Assert.Equal(string.Join("\n",
                $"The deposit address of swap {empty.SwapId} holds 0 units of the token {Addresses.OtherToken}, too little to refund.",
                $"The deposit address of swap {other.SwapId} holds 3 units of the token {Addresses.OtherToken}. The swap is listed below for a refund.",
                $"Swap {replaced.SwapId} cannot be checked. A wallet this store has since replaced made it, or the wallet's SDK data is missing.",
                $"Swap {open.SwapId} is still open, and its deposit address is checked automatically.",
                $"Swap {waiting.SwapId} is listed below."),
            controller.TempData[WellKnownTempData.SuccessMessage]);
    }

    [Fact]
    public async Task ACheckSaysWhenTheAddressCannotBeReadOrTheSwapChangedMeanwhile()
    {
        var unreadable = Closed("unreadable");
        var changed = Closed("changed");
        foreach (var swap in new[] { unreadable, changed })
            swap.InvoiceId = "invoice";
        _wallets.Funds[(changed.SwapId, null)] = new SatoraDepositorFunds(2_500_000, 0);
        _swaps.Accept = false;
        var controller = Controller();

        await controller.CheckAddress("store", new SatoraStableCheckRequest { Id = "invoice" });

        Assert.Equal(string.Join("\n",
                $"The deposit address of swap {unreadable.SwapId} could not be read.",
                $"The deposit address of swap {changed.SwapId} holds 2.5 USDC, but the swap changed during the check. Check it again."),
            controller.TempData[WellKnownTempData.SuccessMessage]);
        Assert.Empty(_credit.Notes);
    }

    [Theory]
    [InlineData(" ", null, "Enter the swap id")]
    [InlineData("store-late", "USDC", "contract of the token")]
    [InlineData("unknown", null, "No stablecoin swap of this store")]
    public async Task ACheckRefusesAMissingOrUnknownIdOrAnInvalidToken(string id, string? token, string says)
    {
        Closed("late");
        var controller = Controller();

        await controller.CheckAddress("store", new SatoraStableCheckRequest { Id = id, Token = token });

        Assert.Empty(_wallets.ReadsUnderUse);
        Assert.Contains(says, (string)controller.TempData[WellKnownTempData.ErrorMessage]!);
    }

    [Fact]
    public async Task ACheckFindsNothingOfAnotherStore()
    {
        Closed("late");
        var controller = Controller();

        await controller.CheckAddress("other-store", new SatoraStableCheckRequest { Id = "store-late" });

        Assert.Empty(_wallets.ReadsUnderUse);
        Assert.NotNull(controller.TempData[WellKnownTempData.ErrorMessage]);
    }

    private SatoraStableSwap Waiting(string name = "swap")
    {
        var swap = Open(name);
        swap.NeedsAttentionAt = swap.CreatedAt;
        swap.AttentionReason = SatoraStableAttention.Sweep;
        return swap;
    }

    private SatoraStableSwap Closed(string name)
    {
        var swap = Open(name);
        swap.CompletedAt = swap.CreatedAt.AddHours(1);
        return swap;
    }

    private SatoraStableSwap Open(string name)
    {
        var swap = SatoraStableIntegrationTests.Swap("store", name, deposit: Addresses.Deposit);
        if (name == "swap")
            swap.SwapId = "swap";
        swap.InvoiceId = "invoice-" + swap.SwapId;
        _swaps.Rows[swap.SwapId] = swap;
        return swap;
    }

    private SatoraStableRefundController Controller()
    {
        var http = new DefaultHttpContext();
        return new SatoraStableRefundController(_swaps, _wallets, _credit, _log)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new MemoryTempDataProvider())
        };
    }

    private sealed class RecordingSwaps() : SatoraStableSwaps(null!)
    {
        public Dictionary<string, SatoraStableSwap> Rows { get; } = [];
        public List<(string StoreId, string SwapId, string Destination, string? Token)> Requests { get; } = [];
        public List<(string StoreId, string SwapId)> Dismissals { get; } = [];
        public List<(string SwapId, DateTimeOffset ClosedAt, string? Token)> Reopens { get; } = [];
        public bool Accept { get; set; } = true;

        public override Task<SatoraStableSwap?> FindAsync(string storeId, string swapId) =>
            Task.FromResult(Rows.TryGetValue(swapId, out var row) && row.StoreId == storeId ? row : null);

        public override Task<List<SatoraStableSwap>> ListNeedingAttentionAsync(string storeId) =>
            Task.FromResult(Rows.Values.Where(s => s.StoreId == storeId && s.NeedsAttentionAt != null && s.CompletedAt == null).ToList());

        public override Task<List<SatoraStableSwap>> FindByIdAsync(string storeId, string id) =>
            Task.FromResult(Rows.Values.Where(s => s.StoreId == storeId && (s.SwapId == id || s.InvoiceId == id)).ToList());

        public override Task<bool> RequestSweepAsync(string storeId, string swapId, string destination, string? token)
        {
            if (Accept)
                Requests.Add((storeId, swapId, destination, token));
            return Task.FromResult(Accept);
        }

        public override Task<bool> DismissAsync(string storeId, string swapId, DateTimeOffset now)
        {
            Dismissals.Add((storeId, swapId));
            return Task.FromResult(true);
        }

        public override Task<bool> ReopenForSweepAsync(string storeId, string swapId, DateTimeOffset closedAt, string? token, DateTimeOffset now)
        {
            if (Accept)
                Reopens.Add((swapId, closedAt, token));
            return Task.FromResult(Accept);
        }
    }

    private sealed class FakeWallets() : SatoraStableWallets(null!, null!, null!, null!)
    {
        // A swap without an entry cannot be read.
        public Dictionary<(string SwapId, string? Token), SatoraDepositorFunds> Funds { get; } = [];
        // Swaps a replaced seed made, which the current wallet's SDK storage does not hold.
        public HashSet<string> Replaced { get; } = [];
        public List<(string SwapId, CancellationToken Token)> Reads { get; } = [];
        public List<string> ReadsUnderUse { get; } = [];

        public override Task<SatoraDepositorFunds> GetFundsAsync(SatoraStableSwap swap, string? token, CancellationToken cancellationToken)
        {
            Reads.Add((swap.SwapId, cancellationToken));
            return Read(swap, token);
        }

        // Holds each read under use until it opens, as a slow node would; the read goes on after the page
        // stops waiting for it, as the SDK's does.
        public TaskCompletionSource? Gate { get; set; }
        public Task? LastReadUnderUse { get; private set; }

        public override Task<T> ReadThenAsync<T>(SatoraStableSwap swap, string? token, Func<SatoraDepositorFunds, Task<T>> then,
            CancellationToken cancellationToken)
        {
            ReadsUnderUse.Add(swap.SwapId);
            var read = ReadThen(swap, token, then);
            LastReadUnderUse = read;
            return read;
        }

        private async Task<T> ReadThen<T>(SatoraStableSwap swap, string? token, Func<SatoraDepositorFunds, Task<T>> then)
        {
            if (Gate is not null)
                await Gate.Task;
            return await then(await Read(swap, token));
        }

        // An empty address of the swap's own token is read as holding nothing, another token as unreadable.
        private Task<SatoraDepositorFunds> Read(SatoraStableSwap swap, string? token) =>
            Replaced.Contains(swap.SwapId) ? Task.FromException<SatoraDepositorFunds>(new SdkException(SdkErrorKind.InvalidSwap, "swap record missing"))
            : Funds.TryGetValue((swap.SwapId, token), out var funds) ? Task.FromResult(funds)
            : token is null && !swap.SwapId.EndsWith("unreadable") ? Task.FromResult(new SatoraDepositorFunds(0, 0))
            : Task.FromException<SatoraDepositorFunds>(new HttpRequestException("node down"));
    }

    private sealed class RecordingCredit() : SatoraStableCredit(null!, null!, null!, null!, NullLogger<SatoraStableCredit>.Instance)
    {
        public List<(string InvoiceId, InvoiceEventData.EventSeverity Severity, string Message)> Notes { get; } = [];

        public override Task NoteAsync(SatoraStableSwap swap, string message, InvoiceEventData.EventSeverity severity)
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
