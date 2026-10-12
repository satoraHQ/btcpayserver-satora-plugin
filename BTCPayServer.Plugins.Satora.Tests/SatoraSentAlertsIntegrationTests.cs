using BTCPayServer.Plugins.Satora.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Problems = BTCPayServer.Plugins.Satora.Chain.SatoraChainNotification.Problems;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against a real Postgres: set SATORA_TEST_POSTGRES as for SatoraWalletIntegrationTests.
public class SatoraSentAlertsIntegrationTests(SatoraWalletIntegrationTests.Database db)
    : IClassFixture<SatoraWalletIntegrationTests.Database>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    private SatoraSentAlerts Sent => new(db.Contexts);

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task RecordsEachAlertOnceAndForgetsOnlyWhatItIsAsked()
    {
        var now = DateTimeOffset.UtcNow;

        Assert.True(await Sent.TryAddAsync("alerts", "swap", "chain/Overpaid/", now));
        Assert.False(await Sent.TryAddAsync("alerts", "swap", "chain/Overpaid/", now.AddMinutes(1)));
        Assert.True(await Sent.TryAddAsync("alerts", "swap", "chain/UnexpectedStatus/Expired", now));
        Assert.True(await Sent.TryAddAsync("alerts", "swap", "stable/Overpaid/", now));
        Assert.True(await Sent.TryAddAsync("alerts", "", "chain/StatusUnavailable/", now));
        Assert.True(await Sent.TryAddAsync("alerts-other", "swap", "chain/Overpaid/", now));

        await Sent.RemoveAsync("alerts", "swap", "chain/");

        Assert.True(await Sent.TryAddAsync("alerts", "swap", "chain/Overpaid/", now));
        Assert.True(await Sent.TryAddAsync("alerts", "swap", "chain/UnexpectedStatus/Expired", now));
        Assert.False(await Sent.TryAddAsync("alerts", "swap", "stable/Overpaid/", now));
        Assert.False(await Sent.TryAddAsync("alerts", "", "chain/StatusUnavailable/", now));
        Assert.False(await Sent.TryAddAsync("alerts-other", "swap", "chain/Overpaid/", now));
    }

    // A second process on the same database, as after a restart, does not send what the first sent.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AWorkerAfterARestartDoesNotSendWhatTheOneBeforeSent()
    {
        var before = new SatoraOnceAlerts(Sent, "chain", NullLogger.Instance);
        var after = new SatoraOnceAlerts(Sent, "chain", NullLogger.Instance);

        Assert.True(await before.TryStartAsync("restart", "swap", Problems.Overpaid));
        Assert.True(await before.TryStartAsync("restart", null, Problems.StatusUnavailable));
        Assert.False(await after.TryStartAsync("restart", "swap", Problems.Overpaid));
        Assert.False(await after.TryStartAsync("restart", null, Problems.StatusUnavailable));

        await after.ForgetAsync("restart", Problems.StatusUnavailable);
        await after.ForgetSwapAsync("restart", "swap");

        Assert.True(await after.TryStartAsync("restart", null, Problems.StatusUnavailable));
        Assert.True(await after.TryStartAsync("restart", "swap", Problems.Overpaid));
    }
}
