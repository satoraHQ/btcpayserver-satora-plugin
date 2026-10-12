using BTCPayServer.Abstractions.Models;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against a real Postgres: set SATORA_TEST_POSTGRES as for SatoraWalletIntegrationTests.
public class SatoraAutoWithdrawIntegrationTests(SatoraWalletIntegrationTests.Database db)
    : IClassFixture<SatoraWalletIntegrationTests.Database>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";
    // The last migration before automatic withdrawals.
    private const string BeforeAutoWithdraw = "20261007_LightningClaimStarted";
    private const string Schema = "\"BTCPayServer.Plugins.Satora\"";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public static bool HasPostgres => SatoraWalletIntegrationTests.HasPostgres;

    private SatoraAutoWithdrawRules Rules => new(db.Contexts);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Every withdrawal recorded before automatic withdrawals existed was made by hand.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task MarksTheWithdrawalsOfAnEarlierVersionAsManual()
    {
        var name = $"satora_test_{Guid.NewGuid():N}";
        await ExecuteOnServer($"CREATE DATABASE {name}");
        try
        {
            var contexts = new SatoraPluginDbContextFactory(Options.Create(new DatabaseOptions
            {
                ConnectionString = new NpgsqlConnectionStringBuilder(SatoraWalletIntegrationTests.Database.Server) { Database = name }.ConnectionString
            }));
            await using (var ctx = contexts.CreateContext())
            {
                await SatoraWalletIntegrationTests.Database.MigrateUpToAsync(ctx, BeforeAutoWithdraw);
                await ctx.Database.ExecuteSqlRawAsync(
                    $"INSERT INTO {Schema}.\"SatoraWithdrawals\" (\"Id\", \"StoreId\", \"Type\", \"Destination\", \"Status\", \"CreatedAt\", \"UpdatedAt\") " +
                    "VALUES ('earlier', 'store', 'Arkade', 'tark1earlier', 'Sent', now(), now())", Ct);
                await ctx.Database.MigrateAsync(Ct);
            }
            var withdrawals = new SatoraWithdrawals(contexts);
            Assert.True(await withdrawals.TryAddAsync(Withdrawal("store", "automatic", SatoraWithdrawalOrigin.Auto)));

            Assert.Equal(SatoraWithdrawalOrigin.Manual, (await withdrawals.FindAsync("store", "earlier"))?.Origin);
            Assert.Equal(SatoraWithdrawalOrigin.Auto, (await withdrawals.FindAsync("store", "automatic"))?.Origin);
            Assert.Null(await new SatoraAutoWithdrawRules(contexts).GetAsync("store"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServer($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
        }
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task SavesARuleOnlyOverTheVersionItsFormShowed()
    {
        var store = NewStore();

        Assert.True((await Rules.SaveAsync(Rule(store, "first"), null, Now)).Saved);
        Assert.False((await Rules.SaveAsync(Rule(store, "first again"), null, Now)).Saved);
        var (saved, previous) = await Rules.SaveAsync(Rule(store, "second"), 1, Now);
        Assert.True(saved);
        Assert.Equal("first", previous?.Destination);
        Assert.False((await Rules.SaveAsync(Rule(store, "stale"), 1, Now)).Saved);

        Assert.True(await Rules.GetAsync(store) is { Enabled: true, Destination: "second", Version: 2, ThresholdSats: 100_000, ReserveSats: 10_000, MaxFeeBps: 100 });
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task OfSavesOverTheSameVersionOnlyOneLands()
    {
        var store = NewStore();
        await Rules.SaveAsync(Rule(store, "first"), null, Now);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Rules.SaveAsync(Rule(store, $"concurrent {i}"), 1, Now)));

        var winner = Assert.Single(results, r => r.Saved);
        Assert.Equal("first", winner.Previous?.Destination);
        var rule = await Rules.GetAsync(store);
        Assert.Equal(2, rule?.Version);
        Assert.StartsWith("concurrent", rule?.Destination);
    }

    // A form shown before the rule was turned off cannot turn it back on.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task TurningARuleOffAppliesAtAnyVersionAndOutdatesEveryFormShownBefore()
    {
        var store = NewStore();
        await Rules.SaveAsync(Rule(store, "first"), null, Now);

        Assert.True(await Rules.DisableAsync(store, Now));
        Assert.False(await Rules.DisableAsync(store, Now));
        Assert.False(await Rules.DisableAsync(NewStore(), Now));

        Assert.True(await Rules.GetAsync(store) is { Enabled: false, Destination: "first", Version: 2 });
        Assert.False((await Rules.SaveAsync(Rule(store, "stale"), 1, Now)).Saved);
        Assert.True((await Rules.SaveAsync(Rule(store, "first"), 2, Now)).Saved);
        Assert.True(await Rules.GetAsync(store) is { Enabled: true, Version: 3 });
    }

    private static string NewStore() => $"auto-{Guid.NewGuid():N}";

    private static SatoraAutoWithdrawRule Rule(string storeId, string destination) => new()
    {
        StoreId = storeId,
        DestinationType = SatoraWithdrawalType.Arkade,
        Destination = destination,
        ThresholdSats = 100_000,
        ReserveSats = 10_000,
        MaxFeeBps = 100
    };

    private static SatoraWithdrawal Withdrawal(string storeId, string id, SatoraWithdrawalOrigin origin) => new()
    {
        Id = id,
        StoreId = storeId,
        Origin = origin,
        Type = SatoraWithdrawalType.Arkade,
        Destination = "tark1destination",
        Status = SatoraWithdrawalStatus.Pending,
        CreatedAt = Now,
        UpdatedAt = Now
    };

    private static async Task ExecuteOnServer(string sql)
    {
        await using var connection = new NpgsqlConnection(SatoraWalletIntegrationTests.Database.Server);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }
}
