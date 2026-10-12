using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Satora.Sdk;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraWalletRenewalsTests
{
    private const string Store = "store";

    // The SDK's refusals, as it words them.
    public const string ShortOfExpiring =
        "insufficient spendable Arkade balance: 0 < 15045 sats; 15045 sats expire within 256 s and can be renewed with arkade_renew, or settled with arkade_settle once expired";
    public const string OffboardShortOfExpiring =
        "insufficient spendable Arkade balance: 1000 < 50000 (send) + 200 (fee) + 330 (change) sats, as the wallet must keep at least 330 sats as Arkade change after an exit; 60000 sats expire within 256 s and can be renewed with arkade_renew, or settled with arkade_settle once expired";
    public const string JournaledInputExpiring =
        "a previous VHTLC funding's input of 15045 sats expires within 256 s, and a new payment must spend it while that funding may still be in flight; renew it with arkade_renew, or retry once it has expired";

    private readonly OutgoingEvents _events = new();
    private readonly FakeWallet _wallet;
    private readonly FakeWalletAccess _wallets;
    private readonly RecordingLogger<SatoraWalletRenewals> _log = new();
    private readonly SatoraWalletRenewals _renewals;

    public SatoraWalletRenewalsTests()
    {
        _wallet = new FakeWallet(_events) { RenewedBatch = "commitment-renew" };
        _wallets = new FakeWalletAccess(_wallet, _events);
        _renewals = new SatoraWalletRenewals(_wallets, _log);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(SdkErrorKind.InvalidSwap, ShortOfExpiring, true, true)]
    [InlineData(SdkErrorKind.InvalidSwap, ShortOfExpiring + "; settle the 134955 recoverable sats with arkade_settle first", true, true)]
    [InlineData(SdkErrorKind.InvalidSwap, OffboardShortOfExpiring, true, true)]
    [InlineData(SdkErrorKind.Transport, JournaledInputExpiring, false, true)]
    [InlineData(SdkErrorKind.InvalidSwap, "insufficient spendable Arkade balance: 0 < 15045 sats; settle the 134955 recoverable sats with arkade_settle first", false, false)]
    [InlineData(SdkErrorKind.Transport, "fund_arkade_swap: connection refused", false, false)]
    [InlineData(SdkErrorKind.Api, "can be renewed with arkade_renew", false, false)]
    public void TellsTheRefusalsForFundsAboutToExpireApart(SdkErrorKind kind, string message, bool refusal, bool needsRenewal)
    {
        var error = new SdkException(kind, message);

        Assert.Equal((refusal, needsRenewal), (SatoraWalletRenewals.IsExpiringRefusal(error), SatoraWalletRenewals.NeedsRenewal(error)));
    }

    [Fact]
    public void OnlyTheSdksErrorsCount()
    {
        Assert.False(SatoraWalletRenewals.IsExpiringRefusal(new InvalidOperationException(ShortOfExpiring)));
        Assert.False(SatoraWalletRenewals.NeedsRenewal(new InvalidOperationException(JournaledInputExpiring)));
        Assert.False(SatoraWalletRenewals.NeedsRenewal(null));
    }

    [Fact]
    public async Task RenewsAfterTheSpendInProgressAndLogsTheBatch()
    {
        var release = new TaskCompletionSource();
        var spend = _wallets.SpendAsync(Store, async _ =>
        {
            await release.Task;
            return 0;
        }, Ct);

        _renewals.Start(Store);
        _renewals.Start(Store);
        await Task.Delay(50, Ct);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("renew", StringComparison.Ordinal));
        release.SetResult();
        await spend;
        await Eventually(() => _log.Entries.Count > 0);
        await _renewals.StopAsync(Ct);

        Assert.Equal(["renew (gated)"], _events.All);
        var entry = Assert.Single(_log.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("commitment-renew", entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingDueIsNotLogged()
    {
        _wallet.RenewedBatch = null;

        Assert.Null(await _renewals.RenewAsync(Store, Ct));
        Assert.Equal(["renew (gated)"], _events.All);
        Assert.Empty(_log.Entries);
    }

    [Fact]
    public async Task AFailedRenewalIsLogged()
    {
        _wallet.RenewError = new SdkException(SdkErrorKind.Transport, "renew: batch failed");

        _renewals.Start(Store);
        await Eventually(() => _log.Entries.Count > 0);
        await _renewals.StopAsync(Ct);

        Assert.Equal(LogLevel.Warning, Assert.Single(_log.Entries).Level);
        Assert.Equal(["renew (gated)"], _events.All);
    }

    // BTCPay is stopping: a renewal still waiting for the spend lock never starts.
    [Fact]
    public async Task StoppingDropsARenewalWaitingForTheSpendLock()
    {
        var release = new TaskCompletionSource();
        var spend = _wallets.SpendAsync(Store, async _ =>
        {
            await release.Task;
            return 0;
        }, Ct);
        _renewals.Start(Store);

        await _renewals.StopAsync(Ct);
        release.SetResult();
        await spend;
        await Task.Delay(50, Ct);

        Assert.Empty(_events.All);
        Assert.Empty(_log.Entries);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10, Ct);
        Assert.True(condition());
    }
}

// Records each renewal started instead of running it.
public sealed class RecordingRenewals() : SatoraWalletRenewals(null!, NullLogger<SatoraWalletRenewals>.Instance)
{
    public List<string> Started { get; } = [];

    public override void Start(string storeId) => Started.Add(storeId);
}
