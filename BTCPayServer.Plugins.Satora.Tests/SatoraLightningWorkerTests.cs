using System.Text.Json;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Satora.Sdk;
using Xunit;
using Problems = BTCPayServer.Plugins.Satora.Lightning.SatoraLightningNotification.Problems;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;

namespace BTCPayServer.Plugins.Satora.Tests;

// One worker tick per test, over rows kept in memory and an SDK that answers as each test sets it up.
public class SatoraLightningWorkerTests
{
    private const string Store = "store";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly List<string> _events = [];
    private readonly MemorySwaps _swaps = new();
    private readonly FakeWallets _wallets;
    private readonly RecordingCredit _credit;
    private readonly SatoraLightningInvoiceHub _hub = new();
    private readonly MemorySentAlerts _sentAlerts = new();
    private readonly TestWorker _worker;

    public SatoraLightningWorkerTests()
    {
        _wallets = new FakeWallets(_events);
        _credit = new RecordingCredit(_events);
        _worker = new TestWorker(_swaps, _wallets, _credit, _hub, sentAlerts: _sentAlerts);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ClaimsIntoTheWalletsOwnAddressAndCreditsOnlyWithTheReceipt()
    {
        using var listener = _hub.Subscribe(Store);
        Open("paid", new SdkSwapStatus.ServerFunded());

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim paid own-address", "credit paid ark-paid"], _events);
        Assert.True(_swaps.Rows["paid"] is { ClaimTxId: "ark-paid", Credited: true, CompletedAt: not null, Status: SatoraSwapStatus.ServerFunded });
        // The worker recorded the payment; the listener recording it again would only hit the primary key.
        await AssertNothingPublished(listener);
    }

    // CancelInvoice marks the row while the worker is between listing it and claiming it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DoesNotClaimASwapMarkedDoNotClaimAfterItWasListed(bool whileFetchingTheAddress)
    {
        Open("cancelled", new SdkSwapStatus.ServerFunded());
        void Cancel() => _swaps.Rows["cancelled"].DoNotClaim = true;
        if (whileFetchingTheAddress)
            _wallets.Address = () =>
            {
                Cancel();
                return "own-address";
            };
        else
            _wallets.DuringStatusRead = Cancel;

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.True(_swaps.Rows["cancelled"] is { ClaimTxId: null, Credited: false, CompletedAt: null });
        Assert.Empty(_worker.Sent);
    }

    // BTCPay cancels the invoice while a claim it cannot stop is under way, and the claim's outcome is
    // unknown: the cancel is refused, so a later tick resumes the claim from the SDK's journal, also
    // past the cutoff.
    [Fact]
    public async Task ResumesAClaimWhoseInvoiceWasCancelledWhileItWasUnderWay()
    {
        Open("claiming", new SdkSwapStatus.ServerFunded());
        _wallets.ClaimError = new SdkException(SdkErrorKind.Timeout, "arkd did not answer");
        bool? cancelled = null;
        _wallets.DuringClaim = () => cancelled = _swaps.Cancel("claiming");
        await _worker.TickAsync(Store, Ct);
        Assert.False(cancelled);
        Assert.True(_swaps.Rows["claiming"] is { DoNotClaim: false, ClaimStartedAt: not null, CompletedAt: null });

        _wallets.DuringClaim = null;
        _wallets.ClaimError = null;
        _wallets.Statuses["claiming"] = new SdkSwapStatus.ClientRedeemed();
        _swaps.Rows["claiming"].ClaimCutoff = Now.AddMinutes(-1);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim claiming own-address", "claim claiming own-address", "credit claiming ark-claiming"], _events);
        Assert.True(_swaps.Rows["claiming"] is { ClaimTxId: "ark-claiming", Credited: true });
    }

    [Fact]
    public async Task AFailedClaimLeavesTheRowUncreditedAndAlertsOnTheSecondFailure()
    {
        Open("failing", new SdkSwapStatus.ClientRedeeming());
        _wallets.ClaimError = new InvalidOperationException("arkd unreachable");

        await _worker.TickAsync(Store, Ct);
        Assert.Empty(_worker.Sent);
        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim failing own-address", "claim failing own-address", "claim failing own-address"], _events);
        Assert.True(_swaps.Rows["failing"] is { ClaimTxId: null, Credited: false, CompletedAt: null });
        Assert.Equal([Problems.ClaimFailing], _worker.Sent.Select(n => n.Problem));
    }

    // The worker starts stopping while the claim address is read, which then fails as a cancelled
    // SDK call does, or returns.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClaimTheWorkerStopsDuringIsNeitherStartedNorCountedAsFailing(bool addressReadFails)
    {
        Open("failing", new SdkSwapStatus.ClientRedeeming());
        _wallets.ClaimError = new InvalidOperationException("arkd unreachable");
        await _worker.TickAsync(Store, Ct);
        using var stopping = new CancellationTokenSource();
        _wallets.Address = () =>
        {
            stopping.Cancel();
            return addressReadFails ? throw new OperationCanceledException(stopping.Token) : "own-address";
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.TickAsync(Store, stopping.Token));

        Assert.Equal(["claim failing own-address"], _events);
        Assert.Empty(_worker.Sent);
        _wallets.Address = () => "own-address";
        await _worker.TickAsync(Store, Ct);
        Assert.Equal([Problems.ClaimFailing], _worker.Sent.Select(n => n.Problem));
    }

    // Past the cutoff a failed claim alerts and can complete the swap uncredited.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AClaimPastItsCutoffTheWorkerStopsDuringNeitherCompletesTheSwapNorAlerts(bool addressReadFails)
    {
        Open("redeemed", new SdkSwapStatus.ClientRedeemed(), cutoff: Now.AddMinutes(-1));
        using var stopping = new CancellationTokenSource();
        _wallets.Address = () =>
        {
            stopping.Cancel();
            return addressReadFails ? throw new OperationCanceledException(stopping.Token) : "own-address";
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _worker.TickAsync(Store, stopping.Token));

        Assert.Empty(_events);
        Assert.True(_swaps.Rows["redeemed"] is { Credited: false, CompletedAt: null });
        Assert.Empty(_worker.Sent);
    }

    // A claim that went through while its call timed out: claiming again returns the journaled
    // receipt, also once the cutoff has passed.
    [Theory]
    [InlineData(typeof(SdkSwapStatus.ClientRedeemed), false)]
    [InlineData(typeof(SdkSwapStatus.ServerRedeemed), false)]
    [InlineData(typeof(SdkSwapStatus.ClientRedeemed), true)]
    [InlineData(typeof(SdkSwapStatus.ServerRedeemed), true)]
    public async Task ClaimsARedeemedSwapAgainForItsReceiptBeforeCrediting(Type status, bool pastCutoff)
    {
        Open("redeemed", (SdkSwapStatus)Activator.CreateInstance(status)!, cutoff: pastCutoff ? Now.AddMinutes(-1) : null);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim redeemed own-address", "credit redeemed ark-redeemed"], _events);
        Assert.True(_swaps.Rows["redeemed"] is { ClaimTxId: "ark-redeemed", Credited: true });
        Assert.Empty(_worker.Sent);
    }

    // The claim failed before the cutoff, and after it the SDK says no claim can complete.
    [Fact]
    public async Task NeverCreditsARedeemedSwapPastItsCutoffWithoutAReceipt()
    {
        using var listener = _hub.Subscribe(Store);
        Open("unverified", new SdkSwapStatus.ServerRedeemed());
        _wallets.ClaimError = new InvalidOperationException("arkd unreachable");
        await _worker.TickAsync(Store, Ct);
        _swaps.Rows["unverified"].ClaimCutoff = Now.AddMinutes(-1);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim unverified own-address", "claim unverified own-address"], _events);
        Assert.Equal(1, _wallets.ClaimPossibleReads);
        Assert.True(_swaps.Rows["unverified"] is { ClaimTxId: null, Credited: false, CompletedAt: not null });
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.ClaimUnverified, "unverified", Store), (alert.Problem, alert.SwapId, alert.StoreId));
        Assert.Equal(LightningInvoiceStatus.Expired, (await listener.WaitInvoice(Ct)).Status);
    }

    // The SDK reaches arkd before it reads its journal, so an outage past the cutoff fails the claim and
    // the query alike: the swap stays open until the SDK says no claim can complete. Meanwhile the server
    // settles the hold invoice without arkd, which alerts nothing new.
    [Fact]
    public async Task KeepsFinishingARedeemedSwapPastItsCutoffWhileAClaimCanComplete()
    {
        Open("redeemed", new SdkSwapStatus.ClientRedeemed(), cutoff: Now.AddMinutes(-1));
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        _wallets.ClaimPossible = true;
        await _worker.TickAsync(Store, Ct);
        _wallets.Statuses["redeemed"] = new SdkSwapStatus.ServerRedeemed();
        _wallets.ClaimPossibleError = new SdkException(SdkErrorKind.Transport, "arkd unreachable");
        await _worker.TickAsync(Store, Ct);
        Assert.True(_swaps.Rows["redeemed"] is { Credited: false, CompletedAt: null });

        _wallets.ClaimError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim redeemed own-address", "claim redeemed own-address", "claim redeemed own-address", "credit redeemed ark-redeemed"], _events);
        Assert.True(_swaps.Rows["redeemed"] is { ClaimTxId: "ark-redeemed", Credited: true, CompletedAt: not null });
        Assert.Equal([Problems.ClaimUnverified], _worker.Sent.Select(n => n.Problem));
    }

    // Past the cutoff the SDK only finishes a claim it journaled, which a transport error can interrupt,
    // so it is asked again every tick until the VHTLC locktime; the swap stays open for the server's refund.
    [Fact]
    public async Task KeepsTryingToFinishAJournaledClaimPastTheCutoffAndAlertsOnce()
    {
        Open("late", new SdkSwapStatus.ServerFunded(), cutoff: Now.AddMinutes(-1));
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd connection reset");

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);
        Assert.False(_swaps.Rows["late"].Credited);
        _wallets.ClaimError = null;
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim late own-address", "claim late own-address", "claim late own-address", "credit late ark-late"], _events);
        Assert.True(_swaps.Rows["late"] is { Credited: true, CompletedAt: not null });
        Assert.Equal([Problems.NearClaimCutoff], _worker.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task StopsTryingToFinishAClaimAtTheVhtlcLocktime()
    {
        Open("late", new SdkSwapStatus.ServerFunded(), cutoff: Now - SatoraClaimWindow.ClaimMargin - TimeSpan.FromSeconds(1));

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.Null(_swaps.Rows["late"].CompletedAt);
    }

    // Past the cutoff the claim is retried every tick, as often as every 5 s, so only its first failure
    // there is a warning.
    [Fact]
    public async Task LogsAClaimThatKeepsFailingPastTheCutoffAsAWarningOnce()
    {
        var logs = new RecordingLogger<SatoraLightningWorker>();
        var worker = new TestWorker(_swaps, _wallets, _credit, _hub, logs);
        Open("open", new SdkSwapStatus.ServerFunded());
        Open("late", new SdkSwapStatus.ServerFunded(), cutoff: Now.AddMinutes(-1));
        _wallets.ClaimError = new SdkException(SdkErrorKind.Transport, "arkd connection reset");

        for (var tick = 0; tick < 3; tick++)
            await worker.TickAsync(Store, Ct);

        IEnumerable<LogLevel> Levels(string swapId) =>
            logs.Entries.Where(e => e.Message.StartsWith($"Claiming Satora Lightning swap {swapId} ", StringComparison.Ordinal)).Select(e => e.Level);
        Assert.Equal([LogLevel.Warning, LogLevel.Warning, LogLevel.Warning], Levels("open"));
        Assert.Equal([LogLevel.Warning, LogLevel.Debug, LogLevel.Debug], Levels("late"));
    }

    [Fact]
    public async Task DoesNotClaimARedeemedSwapMarkedDoNotClaimPastItsCutoff()
    {
        var addressReads = 0;
        _wallets.Address = () => $"address-{++addressReads}";
        Open("cancelled", new SdkSwapStatus.ClientRedeemed(), cutoff: Now.AddMinutes(-1), doNotClaim: true);

        await _worker.TickAsync(Store, Ct);

        Assert.Equal((0, 0, 0), (addressReads, _events.Count, _wallets.ClaimPossibleReads));
        Assert.True(_swaps.Rows["cancelled"] is { Credited: false, CompletedAt: not null });
        Assert.Equal([Problems.ClaimUnverified], _worker.Sent.Select(n => n.Problem));
    }

    [Fact]
    public async Task RetriesAFailedCreditWithoutClaimingAgain()
    {
        Open("credit", new SdkSwapStatus.ServerFunded());
        _credit.FailOnce.Add("credit");

        await _worker.TickAsync(Store, Ct);
        Assert.True(_swaps.Rows["credit"] is { ClaimTxId: "ark-credit", Credited: false, CompletedAt: null });
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim credit own-address", "credit credit ark-credit", "credit credit ark-credit"], _events);
        Assert.True(_swaps.Rows["credit"].Credited);
    }

    // The receipt is all a claimed swap needs: a server that no longer knows it must not close it uncredited.
    [Fact]
    public async Task CreditsAClaimedSwapTheServerNoLongerKnowsPastItsCutoff()
    {
        Open("claimed", new SdkSwapStatus.ServerFunded());
        _credit.FailOnce.Add("claimed");
        await _worker.TickAsync(Store, Ct);
        _wallets.Statuses.Remove("claimed");
        _wallets.NotFound.Add("claimed");
        _swaps.Rows["claimed"].ClaimCutoff = Now.AddMinutes(-1);
        _credit.FailOnce.Add("claimed");

        await _worker.TickAsync(Store, Ct);
        Assert.True(_swaps.Rows["claimed"] is { Credited: false, CompletedAt: null });
        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim claimed own-address", "credit claimed ark-claimed", "credit claimed ark-claimed", "credit claimed ark-claimed"], _events);
        Assert.True(_swaps.Rows["claimed"] is { Credited: true, CompletedAt: not null });
        Assert.Empty(_worker.Sent);
    }

    [Fact]
    public async Task CreditsAClaimedSwapWhileStatusesCannotBeRead()
    {
        Open("claimed", new SdkSwapStatus.ServerFunded());
        _credit.FailOnce.Add("claimed");
        await _worker.TickAsync(Store, Ct);
        _wallets.StatusError = new TimeoutException();

        await _worker.TickAsync(Store, Ct);

        Assert.True(_swaps.Rows["claimed"].Credited);
    }

    // The registry has logged the locked wallet once already, as an error.
    [Fact]
    public async Task LogsStatusesALockedWalletCannotReadAtDebugWithoutAStackTrace()
    {
        var logs = new RecordingLogger<SatoraLightningWorker>();
        var worker = new TestWorker(_swaps, _wallets, _credit, _hub, logs);
        Open("open", new SdkSwapStatus.ServerFunded());
        _wallets.StatusError = new SatoraWalletUnavailableException("The store's Satora wallet is not usable (Locked).");

        await worker.TickAsync(Store, Ct);

        Assert.Equal((LogLevel.Debug, "Could not read the Satora Lightning swaps of store store; retrying (The store's Satora wallet is not usable (Locked).)"),
            Assert.Single(logs.Entries));
        Assert.Null(Assert.Single(logs.Errors));
    }

    [Fact]
    public async Task OneFailingRowDoesNotHoldUpTheOthers()
    {
        Open("first", new SdkSwapStatus.ServerFunded());
        Open("second", new SdkSwapStatus.ServerFunded());
        _credit.FailOnce.Add("first");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["second"], _credit.Credited);
        Assert.True(_swaps.Rows["second"].Credited);
    }

    // arkd may rotate its signer key between claims; a destination under the old key is refused.
    [Fact]
    public async Task ReadsTheClaimAddressForEveryClaim()
    {
        var reads = 0;
        _wallets.Address = () => $"address-{++reads}";
        Open("first", new SdkSwapStatus.ServerFunded());
        Open("second", new SdkSwapStatus.ServerFunded());

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(["claim first address-1", "claim second address-2"], _events.Where(e => e.StartsWith("claim", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AlertsOnlyAboutARefundedSwapItMeantToClaim()
    {
        Open("missed", new SdkSwapStatus.ClientFundedServerRefunded());
        Open("superseded", new SdkSwapStatus.ClientFundedServerRefunded(), doNotClaim: true);

        await _worker.TickAsync(Store, Ct);

        Assert.All(_swaps.Rows.Values, row => Assert.True(row is { CompletedAt: not null, Credited: false }));
        var alert = Assert.Single(_worker.Sent);
        Assert.Equal((Problems.RefundedUnclaimed, "missed"), (alert.Problem, alert.SwapId));
        Assert.Empty(_events);
    }

    // The swap stays open until its cutoff, so every BTCPay start would otherwise tell the store again.
    [Fact]
    public async Task ASwapTheServerDoesNotKnowIsAlertedOnceAcrossARestart()
    {
        Open("unknown", null);
        _wallets.NotFound.Add("unknown");
        await _worker.TickAsync(Store, Ct);

        var restarted = new TestWorker(_swaps, _wallets, _credit, _hub, sentAlerts: _sentAlerts);
        await restarted.TickAsync(Store, Ct);

        Assert.Equal([Problems.UnknownToServer], _worker.Sent.Select(n => n.Problem));
        Assert.Empty(restarted.Sent);
    }

    // BTCPay's invoice search knows the invoice, not the swap. An alert recorded as sent goes out also
    // when the invoice cannot be found, as it would never go out again.
    [Fact]
    public async Task AnAlertNamesTheInvoiceThatTracksTheSwap()
    {
        Open("tracked", null);
        Open("untracked", null);
        _wallets.NotFound.AddRange(["tracked", "untracked"]);
        _credit.Invoices["tracked"] = "invoice-1";
        await _worker.TickAsync(Store, Ct);
        Open("unreadable", null);
        _wallets.NotFound.Add("unreadable");
        _credit.FindError = new InvalidOperationException("BTCPay's database did not answer");

        await _worker.TickAsync(Store, Ct);

        Assert.Equal(new Dictionary<string, string?> { ["tracked"] = "invoice-1", ["untracked"] = null, ["unreadable"] = null },
            _worker.Sent.ToDictionary(n => n.SwapId!, n => n.InvoiceId));
    }

    // The plugin's own page lists no swaps. A payment out of the wallet pays no invoice of the store's.
    [Theory]
    [InlineData(Problems.NearClaimCutoff, "invoice-1", "UIInvoice/Invoice/invoice-1", "Satora swap swap of invoice invoice-1 is paid")]
    [InlineData(Problems.UnknownToServer, "invoice-1", "UIInvoice/Invoice/invoice-1", "The Satora swap server does not know swap swap of invoice invoice-1.")]
    // Sent before alerts named the invoice, or about a payment out of the wallet.
    [InlineData(Problems.UnknownToServer, null, "SatoraPlugin/Index/store", "The Satora swap server does not know swap swap.")]
    [InlineData(Problems.RefundedWithoutServer, null, "SatoraWithdraw/Index/store", "The funds of Lightning payment swap were refunded")]
    public void AnAlertAboutAnIncomingSwapNamesAndLeadsToItsInvoice(Problems problem, string? invoiceId, string link, string body)
    {
        INotificationHandler handler = new SatoraLightningNotification.Handler(new RouteLinks(), new BTCPayServerOptions { RootPath = "/" });
        var vm = new NotificationViewModel { Created = Now };

        handler.FillViewModel(new SatoraLightningNotification
        {
            StoreId = Store,
            SwapId = "swap",
            InvoiceId = invoiceId,
            Problem = problem,
            ClaimCutoff = Now.AddMinutes(5)
        }, vm);

        Assert.Equal(link, vm.ActionLink);
        Assert.StartsWith(body, vm.Body);
    }

    [Fact]
    public async Task CompletesASwapTheServerDoesNotKnowOncePastItsCutoff()
    {
        Open("lapsed", null, cutoff: Now.AddMinutes(-1));
        Open("open", null);
        _wallets.NotFound.AddRange(["lapsed", "open"]);

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.NotNull(_swaps.Rows["lapsed"].CompletedAt);
        Assert.Null(_swaps.Rows["open"].CompletedAt);
        Assert.Equal([("lapsed", Problems.UnknownToServer), ("open", Problems.UnknownToServer)],
            _worker.Sent.Select(n => (n.SwapId, n.Problem)).Order());
    }

    [Fact]
    public async Task ClosesASwapInAnUnhandledStatusOncePastItsCutoff()
    {
        Open("lapsed", new SdkSwapStatus.ClientRefunded(), cutoff: Now.AddMinutes(-1));
        Open("open", new SdkSwapStatus.ClientRefunded());

        await _worker.TickAsync(Store, Ct);
        await _worker.TickAsync(Store, Ct);

        Assert.NotNull(_swaps.Rows["lapsed"].CompletedAt);
        Assert.Null(_swaps.Rows["open"].CompletedAt);
        Assert.Equal([("lapsed", Problems.UnexpectedStatus), ("open", Problems.UnexpectedStatus)],
            _worker.Sent.Select(n => (n.SwapId, n.Problem)).Order());
        Assert.Empty(_events);
    }

    [Fact]
    public async Task MovesNothingWhileStatusesCannotBeRead()
    {
        Open("paid", new SdkSwapStatus.ServerFunded());
        _wallets.StatusError = new TimeoutException();

        await _worker.TickAsync(Store, Ct);

        Assert.Empty(_events);
        Assert.Empty(_worker.Sent);
    }

    [Fact]
    public void NotifiesAboutFailingStatusReadsAfterFiveMinutesWhileASwapCanStillBeClaimed()
    {
        var claimable = Row("claimable", Now.AddMinutes(10));
        var later = Row("later", Now.AddMinutes(20));
        var refused = Row("refused", Now.AddMinutes(1));
        refused.DoNotClaim = true;
        var lapsed = Row("lapsed", Now);

        Assert.Equal(TimeSpan.FromMinutes(5), SatoraLightningWorker.StatusFailureAlertAfter);
        Assert.Null(SatoraLightningWorker.StatusOutageCutoff([claimable], Now.AddMinutes(-5).AddSeconds(1), Now));
        Assert.Equal(claimable.ClaimCutoff, SatoraLightningWorker.StatusOutageCutoff([later, claimable, refused, lapsed], Now.AddMinutes(-5), Now));
        Assert.Null(SatoraLightningWorker.StatusOutageCutoff([refused, lapsed], Now.AddHours(-1), Now));
    }

    private void Open(string swapId, SdkSwapStatus? status, DateTimeOffset? cutoff = null, bool doNotClaim = false)
    {
        var row = Row(swapId, cutoff ?? Now.AddMinutes(20));
        row.DoNotClaim = doNotClaim;
        _swaps.Rows[swapId] = row;
        if (status is not null)
            _wallets.Statuses[swapId] = status;
    }

    private static SatoraLightningSwap Row(string swapId, DateTimeOffset cutoff) => new()
    {
        SwapId = swapId,
        StoreId = Store,
        Direction = SatoraLightningDirection.LightningToArkade,
        Status = SatoraSwapStatus.Pending,
        PaymentHash = swapId,
        Bolt11 = "lnbc" + swapId,
        AmountMsat = 12_345_000,
        ReceiveSats = 12_000,
        ClaimCutoff = cutoff,
        ExpiresAt = cutoff,
        CreatedAt = Now.AddMinutes(-5)
    };

    private static async Task AssertNothingPublished(ILightningInvoiceListener listener)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listener.WaitInvoice(wait.Token));
    }

    private sealed class MemorySwaps() : SatoraLightningSwaps(null!)
    {
        public Dictionary<string, SatoraLightningSwap> Rows { get; } = [];

        // Copies, so only what the worker writes through these methods reaches the rows.
        public override Task<List<SatoraLightningSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.Values
                .Where(s => s.StoreId == storeId && s.CompletedAt == null)
                .Select(s => JsonSerializer.Deserialize<SatoraLightningSwap>(JsonSerializer.Serialize(s))!)
                .ToList());

        public override Task<bool> StartClaimAsync(string storeId, string swapId, DateTimeOffset now)
        {
            if (!Rows.TryGetValue(swapId, out var row) || row.StoreId != storeId || row.DoNotClaim)
                return Task.FromResult(false);
            row.ClaimStartedAt ??= now;
            return Task.FromResult(true);
        }

        // As SetDoNotClaimAsync, which CancelInvoice calls.
        public bool Cancel(string swapId)
        {
            var row = Rows[swapId];
            if (row.Credited || row.ClaimStartedAt is not null)
                return false;
            row.DoNotClaim = true;
            return true;
        }

        public override Task SetStatusAsync(string swapId, SatoraSwapStatus status) => Set(swapId, row => row.Status = status);

        public override Task SetClaimedAsync(string swapId, string claimTxId) => Set(swapId, row => row.ClaimTxId = claimTxId);

        public override Task SetCreditedAsync(string swapId, DateTimeOffset paidAt) => Set(swapId, row =>
        {
            row.Credited = true;
            row.PaidAt = row.CompletedAt = paidAt;
        });

        public override Task SetCompletedAsync(string swapId, DateTimeOffset completedAt) => Set(swapId, row => row.CompletedAt = completedAt);

        private Task Set(string swapId, Action<SatoraLightningSwap> update)
        {
            update(Rows[swapId]);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWallets(List<string> events) : SatoraLightningWallets(null!)
    {
        public Dictionary<string, SdkSwapStatus> Statuses { get; } = [];
        public List<string> NotFound { get; } = [];
        public Exception? StatusError { get; set; }
        public Exception? ClaimError { get; set; }
        public bool ClaimPossible { get; set; }
        public Exception? ClaimPossibleError { get; set; }
        // Kept apart from the events, whose order the other tests pin.
        public int ClaimPossibleReads { get; private set; }
        public Action? DuringStatusRead { get; set; }
        public Action? DuringClaim { get; set; }
        public Func<string> Address { get; set; } = () => "own-address";

        public override Task<BulkStatus> GetBulkStatusAsync(string storeId, IEnumerable<string> swapIds, CancellationToken cancellationToken)
        {
            DuringStatusRead?.Invoke();
            if (StatusError is not null)
                throw StatusError;
            var ids = swapIds.ToList();
            return Task.FromResult(new BulkStatus(
                ids.Where(Statuses.ContainsKey).Select(id => new SwapStatusEntry(id, Statuses[id])).ToArray(),
                ids.Where(NotFound.Contains).ToArray()));
        }

        public override Task<string> GetClaimAddressAsync(string storeId, CancellationToken cancellationToken) => Task.FromResult(Address());

        public override Task<SdkArkadeSettlement> ClaimAsync(string storeId, string swapId, string destination, CancellationToken cancellationToken)
        {
            events.Add($"claim {swapId} {destination}");
            DuringClaim?.Invoke();
            if (ClaimError is not null)
                throw ClaimError;
            return Task.FromResult<SdkArkadeSettlement>(new SdkArkadeSettlement.Offchain("ark-" + swapId, 12_000));
        }

        public override Task<bool> ClaimPossibleAsync(string storeId, string swapId)
        {
            ClaimPossibleReads++;
            return ClaimPossibleError is not null ? throw ClaimPossibleError : Task.FromResult(ClaimPossible);
        }
    }

    private sealed class RecordingCredit(List<string> events)
        : SatoraLightningCredit(null!, null!, null!, null!, null!, NullLogger<SatoraLightningCredit>.Instance)
    {
        public HashSet<string> FailOnce { get; } = [];
        public List<string> Credited { get; } = [];
        // The invoices tracking payment hashes, as BTCPay's database would answer.
        public Dictionary<string, string> Invoices { get; } = [];
        public Exception? FindError { get; set; }

        public override Task<IReadOnlyDictionary<string, string>> FindInvoiceIdsAsync(IReadOnlyCollection<string> paymentHashes) =>
            FindError is not null
                ? throw FindError
                : Task.FromResult<IReadOnlyDictionary<string, string>>(paymentHashes.Where(Invoices.ContainsKey).ToDictionary(h => h, h => Invoices[h]));

        public override Task<SatoraCreditOutcome> CreditAsync(SatoraLightningSwap swap, DateTimeOffset paidAt)
        {
            events.Add($"credit {swap.SwapId} {swap.ClaimTxId}");
            if (FailOnce.Remove(swap.SwapId))
                throw new InvalidOperationException("The invoice could not be updated.");
            Credited.Add(swap.SwapId);
            return Task.FromResult(SatoraCreditOutcome.Recorded);
        }
    }

    private sealed class TestWorker(SatoraLightningSwaps swaps, SatoraLightningWallets wallets, SatoraLightningCredit credit, SatoraLightningInvoiceHub hub,
        ILogger<SatoraLightningWorker>? logger = null, SatoraSentAlerts? sentAlerts = null)
        : SatoraLightningWorker(swaps, wallets, credit, hub, null!, sentAlerts ?? new MemorySentAlerts(), logger ?? NullLogger<SatoraLightningWorker>.Instance)
    {
        public List<SatoraLightningNotification> Sent { get; } = [];

        protected override Task SendAsync(SatoraLightningNotification notification)
        {
            Sent.Add(notification);
            return Task.CompletedTask;
        }
    }
}
