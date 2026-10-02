using BTCPayServer.Plugins.Satora.Services;
using Xunit;

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

        Assert.True(await Sent.TryAddAsync("alerts", "swap", "lightning/NearClaimCutoff/", now));
        Assert.False(await Sent.TryAddAsync("alerts", "swap", "lightning/NearClaimCutoff/", now.AddMinutes(1)));
        Assert.True(await Sent.TryAddAsync("alerts", "swap", "lightning/UnexpectedStatus/ClientInvalidFunded", now));
        Assert.True(await Sent.TryAddAsync("alerts", "", "lightning/StatusUnavailable/", now));
        Assert.True(await Sent.TryAddAsync("alerts-other", "swap", "lightning/NearClaimCutoff/", now));

        await Sent.RemoveAsync("alerts", "swap", "lightning/");

        Assert.True(await Sent.TryAddAsync("alerts", "swap", "lightning/NearClaimCutoff/", now));
        Assert.True(await Sent.TryAddAsync("alerts", "swap", "lightning/UnexpectedStatus/ClientInvalidFunded", now));
        Assert.False(await Sent.TryAddAsync("alerts", "", "lightning/StatusUnavailable/", now));
        Assert.False(await Sent.TryAddAsync("alerts-other", "swap", "lightning/NearClaimCutoff/", now));
    }
}
