using BTCPayServer.Abstractions.Models;
using BTCPayServer.Configuration;
using BTCPayServer.Logging;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NBitcoin;
using NBXplorer;
using Npgsql;
using Satora.Sdk;
using Xunit;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Tests;

// Opt-in, against a real Postgres and, for the tests that read a wallet's balance or address, a regtest arkd:
//   SATORA_TEST_POSTGRES  connection string to a server where the tests may create and drop a database
//   SATORA_TEST_ARKD      arkd URL, such as http://localhost:7070
public class SatoraWalletIntegrationTests(SatoraWalletIntegrationTests.Database db)
    : IClassFixture<SatoraWalletIntegrationTests.Database>
{
    private const string NeedsPostgres = "Set SATORA_TEST_POSTGRES to run";
    private const string NeedsArkd = "Set SATORA_TEST_POSTGRES and SATORA_TEST_ARKD to run";
    // Nothing listens here, so the client never reaches an Arkade server.
    private const string NoArkd = "http://127.0.0.1:9";
    private static readonly TimeSpan Blocked = TimeSpan.FromMilliseconds(500);

    public static bool HasPostgres => Database.Server is not null;
    public static bool HasArkd => HasPostgres && Database.Arkd is not null;

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task RestoresTheLegacySeedOfAMigratedMasterDatabaseAndRetiresItsCiphertextAndSwaps()
    {
        var (_, wallets) = db.Wallets();
        var before = await db.Settings(Database.LegacyStore);
        Assert.True(before is { CryptedSeed: Database.LegacyCiphertext, ProtectedSeed: null, BackupConfirmed: false, Enabled: true });

        Assert.Equal(SatoraWalletState.RestoreRequired, (await wallets.GetStatusAsync(Database.LegacyStore)).State);
        Assert.Equal(SeedWriteRefusal.RestoreOnly, (await wallets.GenerateAsync(Database.LegacyStore, null)).Refusal);
        Assert.Equal(1, await wallets.CountOpenOperationsAsync(Database.LegacyStore));
        Assert.Equal(SeedWriteRefusal.None, await wallets.RestoreAsync(Database.LegacyStore, SatoraMnemonic.Generate(), null));

        var after = await db.Settings(Database.LegacyStore);
        Assert.True(after is { CryptedSeed: null, ProtectedSeed: not null, BackupConfirmed: true, Enabled: false });
        Assert.Equal(64, after.LightningConnectionKey?.Length);
        var retired = Assert.Single(await db.Retired(Database.LegacyStore));
        Assert.Equal((SatoraRetiredSeed.LegacyFormat, Database.LegacyCiphertext), (retired.Format, retired.Ciphertext));
        // The legacy seed's Pending swap would otherwise refuse every replace of the restored wallet.
        Assert.Equal(0, await wallets.CountOpenOperationsAsync(Database.LegacyStore));
        await db.AddSwaps(Database.LegacyStore, "Pending");
        Assert.Equal(1, await wallets.CountOpenOperationsAsync(Database.LegacyStore));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task CountsTheSwapsThatStillNeedTheSeed()
    {
        var (_, wallets) = db.Wallets();
        await db.AddSwaps("count", "Pending", null, "ClientInvalidFunded", "ServerWontFund", "Expired", "ClientRedeemed", "ClientRefunded");
        await db.AddSwaps("count-other-store", "Pending");

        Assert.Equal(4, await wallets.CountOpenOperationsAsync("count"));
    }

    // A store taking payments all day can have many open at once.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task NamesOnlyTheFirstFewOfEachKindThatBlockAReplace()
    {
        var (_, wallets) = db.Wallets();
        await db.AddSwaps("name-many", "Pending", "Pending", "Pending", "Pending", "Pending", "Pending", "Pending");

        var named = await wallets.DescribeOpenOperationsAsync("name-many");

        Assert.Matches(@"^swaps of an earlier plugin version: [0-9a-f-]{36}(, [0-9a-f-]{36}){4} and 2 more$", named);
    }

    // BTCPay's log is the store's record of who changed the wallet when.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task LogsEachChangeToTheWalletAndEachRefusedSeedWrite()
    {
        var log = new RecordingLogger<SatoraWalletService>();
        var (_, wallets) = db.Wallets(logger: log);

        var phrase = await ReadyAndEnabled(wallets, "audit");
        Assert.True(await wallets.SetEnabledAsync("audit", true));
        await db.AddSwaps("audit", "Pending");
        Assert.Equal(SeedWriteRefusal.OpenOperations, (await wallets.GenerateAsync("audit", SeedWriteGuard.ConfirmationWord)).Refusal);
        Assert.Equal(SeedWriteRefusal.None, await wallets.RestoreAsync("audit", phrase, null));
        Assert.True(await wallets.SetEnabledAsync("audit", false));

        Assert.Equal([
                "Generated a new Satora wallet for store audit",
                "Enabled the Satora wallet of store audit",
                "Generating a new Satora wallet for store audit was refused (OpenOperations)",
                "Restored a Satora wallet for store audit from its recovery phrase",
                "Disabled the Satora wallet of store audit"
            ],
            log.Entries.Where(e => e.Level == LogLevel.Information).Select(e => e.Message));
    }

    [Fact(Skip = NeedsArkd, SkipUnless = nameof(HasArkd))]
    public async Task ReplacingAWalletRetiresItsSeedAndStartsTheNewOneDisabledWithANewConnectionKey()
    {
        var (registry, wallets) = db.Wallets(Database.Arkd);
        await ReadyAndEnabled(wallets, "replace");
        var old = await db.Settings("replace");
        var oldWallet = await registry.GetAsync("replace");
        var rules = new SatoraAutoWithdrawRules(db.Contexts);
        await rules.SaveAsync(AutoWithdrawRule("replace"), null, DateTimeOffset.UtcNow);

        var (refusal, phrase) = await wallets.GenerateAsync("replace", null);

        Assert.Equal(SeedWriteRefusal.None, refusal);
        var now = await db.Settings("replace");
        Assert.True(now is { Enabled: false, BackupConfirmed: false, BackupConfirmedAt: null });
        Assert.NotEqual(old.ProtectedSeed, now.ProtectedSeed);
        Assert.Equal(SatoraMnemonic.Fingerprint(phrase!), now.SeedFingerprint);
        Assert.NotEqual(old.LightningConnectionKey, now.LightningConnectionKey);
        Assert.Equal(64, now.LightningConnectionKey?.Length);
        Assert.False(registry.IsLightningKey("replace", old.LightningConnectionKey!));
        Assert.True(registry.IsLightningKey("replace", now.LightningConnectionKey!));
        // The new wallet starts disabled, so the store's BTC-LN config stops using this wallet.
        Assert.Equal((false, now.LightningConnectionKey), db.LightningConfig.Calls("replace").Last());
        var retired = Assert.Single(await db.Retired("replace"));
        Assert.Equal((SatoraRetiredSeed.DataProtectionFormat, old.ProtectedSeed, old.SeedFingerprint),
            (retired.Format, retired.Ciphertext, retired.SeedFingerprint));
        await AssertDisposed(oldWallet);
        Assert.NotSame(oldWallet, await registry.GetAsync("replace"));
        Assert.True(await rules.GetAsync("replace") is { Enabled: false, Destination: "tark1destination" });
    }

    // A rule set up for the wallet a new seed replaces must not send the new wallet's funds; restoring
    // the same wallet brings back the wallet it was set up for.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ANewWalletTurnsAutomaticWithdrawalOffAndTheSameWalletRestoredKeepsItOn()
    {
        var (_, wallets) = db.Wallets();
        var rules = new SatoraAutoWithdrawRules(db.Contexts);
        var phrase = await ReadyAndEnabled(wallets, "auto-same");
        await using (var ctx = db.Contexts.CreateContext())
        {
            ctx.SatoraSettings.Add(new SatoraSettings { StoreId = "auto-legacy", Enabled = true, CryptedSeed = Database.LegacyCiphertext });
            await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        foreach (var store in new[] { "auto-same", "auto-new", "auto-legacy" })
            Assert.True((await rules.SaveAsync(AutoWithdrawRule(store), null, DateTimeOffset.UtcNow)).Saved);

        Assert.Equal(SeedWriteRefusal.None, await wallets.RestoreAsync("auto-same", phrase, null));
        Assert.Equal(SeedWriteRefusal.None, (await wallets.GenerateAsync("auto-new", null)).Refusal);
        Assert.Equal(SeedWriteRefusal.None, await wallets.RestoreAsync("auto-legacy", SatoraMnemonic.Generate(), null));

        Assert.True(await rules.GetAsync("auto-same") is { Enabled: true, Version: 1 });
        Assert.True(await rules.GetAsync("auto-new") is { Enabled: false, Destination: "tark1destination" });
        Assert.True(await rules.GetAsync("auto-legacy") is { Enabled: false, Destination: "tark1destination" });
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task RestoringTheSameWalletKeepsItsClientAndConnectionKeyAndRetiresNothing()
    {
        var (registry, wallets) = db.Wallets();
        var phrase = await ReadyAndEnabled(wallets, "same");
        await db.AddSwaps("same", "ServerFunded");
        var key = (await db.Settings("same")).LightningConnectionKey;
        var wallet = await registry.GetAsync("same");

        Assert.Equal(SeedWriteRefusal.None, await wallets.RestoreAsync("same", phrase, null));

        Assert.Same(wallet, await registry.GetAsync("same"));
        Assert.True(await db.Settings("same") is { Enabled: true } now && now.LightningConnectionKey == key);
        Assert.Empty(await db.Retired("same"));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task RestoringTheSameWalletBeforeItsBackupIsConfirmedConfirmsItAndRetiresNothing()
    {
        var (_, wallets) = db.Wallets();
        var (_, phrase) = await wallets.GenerateAsync("unconfirmed", null);

        Assert.Equal(SeedWriteRefusal.None, await wallets.RestoreAsync("unconfirmed", phrase!, null));

        Assert.Equal(SatoraWalletState.Ready, (await wallets.GetStatusAsync("unconfirmed")).State);
        Assert.Empty(await db.Retired("unconfirmed"));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ALockedSeedIsUnlockedByItsOwnPhraseOnly()
    {
        var (_, wallets) = db.Wallets();
        var phrase = await ReadyAndEnabled(wallets, "locked");
        var before = await db.Settings("locked");
        var (_, otherKeys) = db.Wallets(keys: "other-keys");

        Assert.Equal(SatoraWalletState.Locked, (await otherKeys.GetStatusAsync("locked")).State);
        Assert.Equal(SeedWriteRefusal.Locked, (await otherKeys.GenerateAsync("locked", SeedWriteGuard.ConfirmationWord)).Refusal);
        Assert.Equal(SeedWriteRefusal.Locked, await otherKeys.RestoreAsync("locked", SatoraMnemonic.Generate(), SeedWriteGuard.ConfirmationWord));
        Assert.Equal(SeedWriteRefusal.None, await otherKeys.RestoreAsync("locked", phrase, null));

        Assert.Equal(new SatoraWalletStatus(SatoraWalletState.Ready, phrase, true), await otherKeys.GetStatusAsync("locked"));
        Assert.Equal(before.LightningConnectionKey, (await db.Settings("locked")).LightningConnectionKey);
        Assert.Equal(before.ProtectedSeed, Assert.Single(await db.Retired("locked")).Ciphertext);
    }

    // The registry says once, as an error, that the seed cannot be decrypted. The warm-up and every
    // balance read that then find the wallet locked log at Debug, without a stack trace.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ALockedWalletIsLoggedOnceAsAnErrorAndEachReadOfItOnlyAtDebug()
    {
        var (_, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "locked-log");
        var registryLog = new RecordingLogger<SatoraWalletRegistry>();
        var serviceLog = new RecordingLogger<SatoraWalletService>();
        var (registry, otherKeys) = db.Wallets(keys: "other-keys", logger: serviceLog, registryLogger: registryLog);

        registry.Warm("locked-log");
        await registry.WaitForWarmUpsAsync();
        Assert.Null(await otherKeys.TryGetBalanceAsync("locked-log"));
        Assert.Null(await otherKeys.TryGetBalanceAsync("locked-log"));

        Assert.Contains((LogLevel.Error, "The Satora wallet seed of store locked-log cannot be decrypted with BTCPay's data protection keys"),
            registryLog.Entries);
        Assert.Single(registryLog.Entries, e => e.Message.Contains("cannot be decrypted"));
        Assert.DoesNotContain(registryLog.Entries.Concat(serviceLog.Entries), e => e.Level == LogLevel.Warning);
        const string reason = "(The store's Satora wallet is not usable (Locked).)";
        Assert.Equal([$"Could not prepare the Satora wallet of store locked-log {reason}"],
            registryLog.Entries.Where(e => e.Level == LogLevel.Debug).Select(e => e.Message));
        Assert.Equal([$"Could not open the Satora wallet of store locked-log {reason}", $"Could not open the Satora wallet of store locked-log {reason}"],
            serviceLog.Entries.Where(e => e.Level == LogLevel.Debug).Select(e => e.Message));
        Assert.All(registryLog.Entries.Zip(registryLog.Errors).Where(e => e.First.Level == LogLevel.Debug), e => Assert.Null(e.Second));
        Assert.All(serviceLog.Errors, e => Assert.Null(e));
    }

    // A replace decides without a wallet it cannot build, so the balance is unknown. A wallet that is
    // only unusable, which the registry has already said, is logged at Debug; any other failure warns.
    [Theory(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReplaceLogsAWalletItCannotBuild(bool unusable)
    {
        var store = unusable ? "replace-unusable" : "replace-unbuilt";
        var (_, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, store);
        var log = new RecordingLogger<SatoraWalletRegistry>();
        var (registry, replacing) = db.Wallets(registryLogger: log);
        Exception failure = unusable ? new SatoraWalletUnavailableException("not usable") : new InvalidOperationException("arkd refused");
        registry.AfterBuild = wallet =>
        {
            wallet.Dispose();
            throw failure;
        };

        Assert.Equal(SeedWriteRefusal.BalanceUnknown, (await replacing.GenerateAsync(store, SeedWriteGuard.ConfirmationWord)).Refusal);

        var built = Assert.Single(log.Entries.Zip(log.Errors), e => e.First.Message.StartsWith($"Could not build the Satora wallet of store {store}"));
        Assert.Equal(unusable ? LogLevel.Debug : LogLevel.Warning, built.First.Level);
        Assert.Same(unusable ? null : failure, built.Second);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task DisablingKeepsTheClientWhileASwapNeedsItAndEvictsItAfterwards()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "evict");
        await db.AddSwaps("evict", "ClientFundedServerRefunded");
        var wallet = await registry.GetAsync("evict");

        Assert.True(await wallets.SetEnabledAsync("evict", false));
        Assert.Same(wallet, await registry.GetAsync("evict"));

        await db.SetSwapStatuses("evict", "ClientRefunded");
        Assert.True(await wallets.SetEnabledAsync("evict", false));
        Assert.NotSame(wallet, await registry.GetAsync("evict"));
        await AssertDisposed(wallet);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AReplaceWaitsForASwapBeingCreatedAndCountsIt()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "creating");
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var creating = registry.UseAsync("creating", async _ =>
        {
            started.SetResult();
            await finish.Task;
            await db.AddSwaps("creating", "Pending");
            return true;
        });
        await started.Task;

        var replace = wallets.GenerateAsync("creating", SeedWriteGuard.ConfirmationWord);
        await Task.Delay(Blocked, TestContext.Current.CancellationToken);
        Assert.False(replace.IsCompleted);
        finish.SetResult();

        Assert.True(await creating);
        Assert.Equal(SeedWriteRefusal.OpenOperations, (await replace).Refusal);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AReplaceWaitingForASwapDoesNotHoldUpAnotherStoresWallet()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "busy");
        var (_, phrase) = await wallets.GenerateAsync("idle", null);
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var creating = registry.UseAsync("busy", async _ =>
        {
            started.SetResult();
            await finish.Task;
            return true;
        });
        try
        {
            await started.Task;
            var replace = wallets.GenerateAsync("busy", SeedWriteGuard.ConfirmationWord);
            await Task.Delay(Blocked, TestContext.Current.CancellationToken);
            Assert.False(replace.IsCompleted);

            Assert.True(await wallets.ConfirmBackupAsync("idle", phrase).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

            finish.SetResult();
            Assert.True(await creating);
            Assert.Equal(SeedWriteRefusal.BalanceUnknown, (await replace).Refusal);
        }
        finally
        {
            finish.TrySetResult();
        }
    }

    // A withdrawal holds the spend lock for as long as an exit takes; invoices are still created meanwhile.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ASpendHoldsUpOtherSpendsButNotSwapCreation()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "spending");
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var spend = registry.SpendAsync("spending", async _ =>
        {
            started.SetResult();
            await finish.Task;
            return true;
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(await registry.UseAsync("spending", _ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var second = registry.SpendAsync("spending", _ => Task.FromResult(true));
            await Task.Delay(Blocked, TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted);

            finish.SetResult();
            Assert.True(await spend);
            Assert.True(await second);
        }
        finally
        {
            finish.TrySetResult();
        }
    }

    // BTCPay gives CreateInvoice 5 s and answers "did not reply in a timely manner" once its token is cancelled.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AReplaceWaitsForASpendAndASwapCreationAndHoldsOffNewOnesUntilTheirCallerGivesUp()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "exclusive");
        var spendStarted = new TaskCompletionSource();
        var createStarted = new TaskCompletionSource();
        var finishSpend = new TaskCompletionSource();
        var finishCreate = new TaskCompletionSource();
        var spend = registry.SpendAsync("exclusive", async _ =>
        {
            spendStarted.SetResult();
            await finishSpend.Task;
            return true;
        });
        var create = registry.UseAsync("exclusive", async _ =>
        {
            createStarted.SetResult();
            await finishCreate.Task;
            return true;
        });
        try
        {
            await Task.WhenAll(spendStarted.Task, createStarted.Task).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var replace = wallets.GenerateAsync("exclusive", SeedWriteGuard.ConfirmationWord);
            await Task.Delay(Blocked, TestContext.Current.CancellationToken);
            Assert.False(replace.IsCompleted);

            using var timeout = new CancellationTokenSource(Blocked);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                registry.UseAsync("exclusive", _ => Task.FromResult(true), timeout.Token).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            var waiting = registry.UseAsync("exclusive", _ => Task.FromResult(true));

            finishSpend.SetResult();
            Assert.True(await spend);
            await Task.Delay(Blocked, TestContext.Current.CancellationToken);
            Assert.False(replace.IsCompleted);
            Assert.False(waiting.IsCompleted);

            finishCreate.SetResult();
            Assert.True(await create);
            Assert.Equal(SeedWriteRefusal.BalanceUnknown, (await replace).Refusal);
            Assert.True(await waiting);
        }
        finally
        {
            finishSpend.TrySetResult();
            finishCreate.TrySetResult();
        }
    }

    // An offboard's withdrawal row stays unresolved while it runs, which can take many minutes. A
    // replace or a disable that the open row refuses anyway neither waits for it nor holds up claims.
    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AReplaceOrDisableThatOpenOperationsRefuseDoesNotWaitForAWalletActionInFlight()
    {
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, "precheck");
        Assert.True(await new SatoraWithdrawals(db.Contexts).TryAddAsync(new SatoraWithdrawal
        {
            Id = Guid.NewGuid().ToString("N"),
            StoreId = "precheck",
            Type = SatoraWithdrawalType.Bitcoin,
            Destination = "destination",
            Status = SatoraWithdrawalStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        }));
        var started = new TaskCompletionSource();
        var finish = new TaskCompletionSource();
        var offboard = registry.SpendAsync("precheck", async _ =>
        {
            started.SetResult();
            await finish.Task;
            return true;
        });
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal(SeedWriteRefusal.OpenOperations,
                (await wallets.GenerateAsync("precheck", SeedWriteGuard.ConfirmationWord).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Refusal);
            Assert.True(await wallets.SetEnabledAsync("precheck", false).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.True(await registry.UseAsync("precheck", _ => Task.FromResult(true)).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            Assert.False(offboard.IsCompleted);
        }
        finally
        {
            finish.TrySetResult();
        }
        Assert.True(await offboard);
    }

    [Fact(Skip = NeedsArkd, SkipUnless = nameof(HasArkd))]
    public async Task ASwapOrReadStartedDuringAReplaceGetsTheNewWallet()
    {
        var (registry, wallets) = db.Wallets(Database.Arkd);
        await ReadyAndEnabled(wallets, "during");
        var oldWallet = await registry.GetAsync("during");

        Task<(SeedWriteRefusal Refusal, string? Mnemonic)> replace;
        Task<StoreWallet> use, read;
        // Holding the retired seeds table makes the replace wait inside the registry's gate, where it
        // saves the new seed. Swaps are also counted before the gate, so a lock on them would not.
        await using (await db.LockRetiredSeedsTable())
        {
            replace = wallets.GenerateAsync("during", null);
            await db.WaitForABlockedQuery();
            use = registry.UseAsync("during", Task.FromResult);
            read = registry.GetAsync("during");
            await Task.Delay(Blocked, TestContext.Current.CancellationToken);
            Assert.False(use.IsCompleted);
            Assert.False(read.IsCompleted);
        }

        Assert.Equal(SeedWriteRefusal.None, (await replace).Refusal);
        var newWallet = await use;
        Assert.NotSame(oldWallet, newWallet);
        Assert.Same(newWallet, await read);
        await AssertDisposed(oldWallet);
    }

    [Fact(Skip = NeedsArkd, SkipUnless = nameof(HasArkd))]
    public async Task AnActionGivenAWalletJustBeforeAReplaceRunsOnTheNewWallet()
    {
        var (registry, wallets) = db.Wallets(Database.Arkd);
        await ReadyAndEnabled(wallets, "window");
        var oldWallet = await registry.GetAsync("window");
        var replace = new TaskCompletionSource<Task<(SeedWriteRefusal Refusal, string? Mnemonic)>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var replaceHoldsTheWallet = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<StoreWallet>();

        Task<StoreWallet> use;
        // Holding the retired seeds table makes the replace wait inside the registry's gate with the old wallet closed to new actions.
        await using (await db.LockRetiredSeedsTable())
        {
            registry.BeforeUse = async () =>
            {
                registry.BeforeUse = null;
                replace.SetResult(wallets.GenerateAsync("window", null));
                await db.WaitForABlockedQuery();
                replaceHoldsTheWallet.SetResult();
            };
            use = registry.UseAsync("window", wallet =>
            {
                seen.Add(wallet);
                return Task.FromResult(wallet);
            });
            await replaceHoldsTheWallet.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            await Task.Delay(Blocked, TestContext.Current.CancellationToken);
            Assert.False(use.IsCompleted);
        }

        Assert.Equal(SeedWriteRefusal.None, (await await replace.Task).Refusal);
        var newWallet = await use;
        Assert.NotSame(oldWallet, newWallet);
        Assert.Same(newWallet, Assert.Single(seen));
        Assert.Same(newWallet, await registry.GetAsync("window"));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AReplaceIsRefusedWhenTheBalanceCannotBeRead()
    {
        var (_, wallets) = db.Wallets(NoArkd);
        await ReadyAndEnabled(wallets, "unreadable");

        Assert.Equal(SeedWriteRefusal.BalanceUnknown, (await wallets.GenerateAsync("unreadable", SeedWriteGuard.ConfirmationWord)).Refusal);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task ADisposedRegistryBuildsNoWallet()
    {
        var (registry, wallets) = db.Wallets();
        await wallets.GenerateAsync("disposed", null);

        registry.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.GetAsync("disposed", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => registry.UseAsync("disposed", _ => Task.FromResult(true), TestContext.Current.CancellationToken));
        Assert.False(Directory.Exists(SatoraNetworks.WalletDirectory(db.DataDir, SatoraBitcoinNetwork.Regtest, "disposed")));
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task AWalletStillBeingBuiltWhenTheRegistryIsDisposedIsDisposedAndHandedToNobody()
    {
        var (registry, wallets) = db.Wallets();
        await wallets.GenerateAsync("late", null);
        var built = new TaskCompletionSource<StoreWallet>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource();
        registry.AfterBuild = async wallet =>
        {
            built.SetResult(wallet);
            await finish.Task;
        };
        var get = registry.GetAsync("late", TestContext.Current.CancellationToken);
        var wallet = await built.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        registry.Dispose();
        finish.SetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => get);
        await AssertDisposed(wallet);
    }

    [Fact(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    public async Task WaitingForWarmUpsWaitsForOneStillBuildingItsWallet()
    {
        var (registry, wallets) = db.Wallets();
        await wallets.GenerateAsync("warming", null);
        var built = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource();
        registry.AfterBuild = async _ =>
        {
            built.SetResult();
            await finish.Task;
        };
        registry.Warm("warming");
        await built.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var warmUps = registry.WaitForWarmUpsAsync();
        await Task.Delay(Blocked, TestContext.Current.CancellationToken);
        Assert.False(warmUps.IsCompleted);
        finish.SetResult();

        await warmUps.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    // The workers claim and refund a swap's VHTLC through UseAsync, so a replace or eviction waits for
    // one in flight, and one handed the wallet just before an eviction runs on the next client.
    [Theory(Skip = NeedsPostgres, SkipUnless = nameof(HasPostgres))]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AVhtlcClaimOrRefundGivenAWalletJustBeforeAnEvictionRunsOnTheNextClient(bool refund)
    {
        var store = refund ? "evict-refund" : "evict-claim";
        var (registry, wallets) = db.Wallets();
        await ReadyAndEnabled(wallets, store);
        var oldWallet = await registry.GetAsync(store, TestContext.Current.CancellationToken);
        registry.BeforeUse = async () =>
        {
            registry.BeforeUse = null;
            Assert.True(await wallets.SetEnabledAsync(store, false));
        };
        var lightning = new SatoraLightningWallets(registry);

        // No client knows this swap, but only one that is not disposed says so.
        await Assert.ThrowsAsync<SdkException>(() => refund
            ? new SatoraChainWallets(lightning, registry, null!).RefundAsync(store, "unknown", "bcrt1qunknown", TestContext.Current.CancellationToken)
            : lightning.ClaimAsync(store, "unknown", "tark1unknown", TestContext.Current.CancellationToken));

        Assert.NotSame(oldWallet, await registry.GetAsync(store, TestContext.Current.CancellationToken));
        await AssertDisposed(oldWallet);
    }

    [Fact(Skip = NeedsArkd, SkipUnless = nameof(HasArkd))]
    public async Task TheClientTalksToArkdAndKeepsItsDatabasesInTheStoreDirectory()
    {
        var (registry, wallets) = db.Wallets(Database.Arkd);
        await ReadyAndEnabled(wallets, "arkd");

        Assert.StartsWith("tark1", await (await registry.GetAsync("arkd")).GetArkadeAddressAsync(TestContext.Current.CancellationToken));
        Assert.True(await wallets.TryGetBalanceAsync("arkd") is { confirmedSats: 0, preConfirmedSats: 0, recoverableSats: 0 });
        var directory = SatoraNetworks.WalletDirectory(db.DataDir, SatoraBitcoinNetwork.Regtest, "arkd");
        Assert.True(File.Exists(Path.Combine(directory, "swaps.sqlite")));
        Assert.Single(Directory.GetFiles(directory, "contracts-*.sqlite"));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
    }

    // A disposed client still serves calls that were in flight when it was disposed, such as the address
    // lookup with which enabling a wallet warms it up, and is released once they finish.
    private static async Task AssertDisposed(StoreWallet wallet)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            try
            {
                await wallet.Client.GetArkadeBalanceAsync(timeout.Token);
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SdkException)
            {
            }
            await Task.Delay(100, timeout.Token);
        }
    }

    private static SatoraAutoWithdrawRule AutoWithdrawRule(string storeId) => new()
    {
        StoreId = storeId,
        DestinationType = SatoraWithdrawalType.Arkade,
        Destination = "tark1destination",
        ThresholdSats = 100_000,
        ReserveSats = 0,
        MaxFeeBps = 100
    };

    private static async Task<string> ReadyAndEnabled(SatoraWalletService wallets, string storeId)
    {
        var (refusal, phrase) = await wallets.GenerateAsync(storeId, null);
        Assert.Equal(SeedWriteRefusal.None, refusal);
        Assert.True(await wallets.ConfirmBackupAsync(storeId, phrase));
        Assert.True(await wallets.SetEnabledAsync(storeId, true));
        return phrase!;
    }

    public sealed class Database : IAsyncLifetime
    {
        public const string LegacyStore = "legacy";
        public const string LegacyCiphertext = "bGVnYWN5";
        // The last migration on master, which deployments of earlier plugin versions have applied.
        private const string MasterMigration = "20260623_StatusIdx";
        private const string Schema = "\"BTCPayServer.Plugins.Satora\"";

        public static readonly string? Server = Environment.GetEnvironmentVariable("SATORA_TEST_POSTGRES") is { Length: > 0 } s ? s : null;
        public static readonly string? Arkd = Environment.GetEnvironmentVariable("SATORA_TEST_ARKD") is { Length: > 0 } a ? a : null;

        private readonly string _name = $"satora_test_{Guid.NewGuid():N}";
        private readonly List<SatoraWalletRegistry> _registries = [];
        private string _scratch = null!;
        private SatoraPluginDbContextFactory _contexts = null!;
        private BTCPayNetworkProvider _networks = null!;

        public string DataDir => Path.Combine(_scratch, "data");

        public SatoraPluginDbContextFactory Contexts => _contexts;

        public RecordingLightningConfig LightningConfig { get; } = new();

        public RecordingChainConfig ChainConfig { get; } = new();

        public FakeLightningInvoices LightningInvoices { get; } = new();

        private string ConnectionString => new NpgsqlConnectionStringBuilder(Server) { Database = _name }.ConnectionString;

        public async ValueTask InitializeAsync()
        {
            if (Server is null)
                return;
            _scratch = Directory.CreateTempSubdirectory("satora-test-").FullName;
            await ExecuteOnServer($"CREATE DATABASE {_name}");
            _contexts = new SatoraPluginDbContextFactory(Options.Create(new DatabaseOptions { ConnectionString = ConnectionString }));
            var nbx = new NBXplorerNetworkProvider(ChainName.Regtest);
            _networks = new BTCPayNetworkProvider([new BTCPayNetwork { CryptoCode = "BTC", NBXplorerNetwork = nbx.GetFromCryptoCode("BTC") }], nbx, new Logs());

            // A database as master leaves it, with a legacy seed and a swap row.
            await using (var ctx = _contexts.CreateContext())
            {
                await MigrateUpToAsync(ctx, MasterMigration);
                await ctx.Database.ExecuteSqlRawAsync(
                    $"INSERT INTO {Schema}.\"SatoraSettings\" (\"StoreId\", \"Enabled\", \"CryptedSeed\") VALUES ('{LegacyStore}', true, '{LegacyCiphertext}')");
            }
            await AddSwaps(LegacyStore, "Pending");
            // What PluginMigrationRunner does at startup.
            await using (var ctx = _contexts.CreateContext())
                await ctx.Database.MigrateAsync();
        }

        // Applies the plugin's migrations up to and including lastId, as a deployment that stopped there
        // applied them. Migrator.MigrateAsync(target) only looks targets up by 14 digit ids.
        public static async Task MigrateUpToAsync(SatoraPluginDbContext ctx, string lastId)
        {
            var assembly = ctx.GetService<IMigrationsAssembly>();
            var generator = ctx.GetService<IMigrationsSqlGenerator>();
            var history = ctx.GetService<IHistoryRepository>();
            await ctx.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript());
            foreach (var (id, type) in assembly.Migrations.Where(m => string.CompareOrdinal(m.Key, lastId) <= 0))
            {
                var migration = assembly.CreateMigration(type, ctx.Database.ProviderName!);
                foreach (var command in generator.Generate(migration.UpOperations))
                    await ctx.Database.ExecuteSqlRawAsync(command.CommandText);
                await ctx.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, ProductInfo.GetVersion())));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (Server is null)
                return;
            await Task.WhenAll(_registries.Select(r => r.WaitForWarmUpsAsync()));
            foreach (var registry in _registries)
                registry.Dispose();
            NpgsqlConnection.ClearAllPools();
            await ExecuteOnServer($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)");
            Directory.Delete(_scratch, true);
        }

        public (SatoraWalletRegistry Registry, SatoraWalletService Wallets) Wallets(string? arkd = NoArkd, string keys = "keys",
            ILogger<SatoraWalletService>? logger = null, ILogger<SatoraWalletRegistry>? registryLogger = null)
        {
            var protector = new SatoraSeedProtector(DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_scratch, keys))));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [SatoraNetworks.BaseUrlKey] = NoArkd,
                [SatoraNetworks.ArkadeServerUrlKey] = arkd,
                [SatoraNetworks.EsploraUrlKey] = NoArkd
            }).Build();
            // No SatoraNotifier, which needs BTCPay's own database: the registry logs that it could not notify.
            var registry = new SatoraWalletRegistry(_contexts, protector, null!, _networks, configuration,
                Options.Create(new DataDirectories { DataDir = DataDir }), registryLogger ?? NullLogger<SatoraWalletRegistry>.Instance);
            _registries.Add(registry);
            return (registry, new SatoraWalletService(_contexts, protector, registry, LightningConfig, ChainConfig, new SatoraAutoWithdrawRules(_contexts),
                LightningInvoices, logger ?? NullLogger<SatoraWalletService>.Instance));
        }

        public async Task<SatoraSettings> Settings(string storeId)
        {
            await using var ctx = _contexts.CreateContext();
            return await ctx.SatoraSettings.AsNoTracking().SingleAsync(s => s.StoreId == storeId);
        }

        public async Task<List<SatoraRetiredSeed>> Retired(string storeId)
        {
            await using var ctx = _contexts.CreateContext();
            return await ctx.SatoraRetiredSeeds.AsNoTracking().Where(r => r.StoreId == storeId).ToListAsync();
        }

        public async Task AddSwaps(string storeId, params string?[] statuses)
        {
            await using var ctx = _contexts.CreateContext();
            foreach (var status in statuses)
                ctx.SatoraTransactions.Add(new SatoraTx
                {
                    TxID = Guid.NewGuid().ToString(),
                    StoreId = storeId,
                    Status = status,
                    Stablecoin = "USDC",
                    Blockchain = "Polygon",
                    BTCPayInvoiceId = "invoice",
                    DateT = DateTime.UtcNow
                });
            await ctx.SaveChangesAsync();
        }

        public async Task SetSwapStatuses(string storeId, string status)
        {
            await using var ctx = _contexts.CreateContext();
            await ctx.SatoraTransactions.Where(t => t.StoreId == storeId).ExecuteUpdateAsync(t => t.SetProperty(x => x.Status, status));
        }

        // A connection whose open transaction holds the swap table; disposing it closes the connection and releases the lock.
        public async Task<NpgsqlConnection> LockRetiredSeedsTable()
        {
            var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ConnectionString) { Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand($"LOCK TABLE {Schema}.\"SatoraRetiredSeeds\" IN ACCESS EXCLUSIVE MODE", connection, transaction);
            await command.ExecuteNonQueryAsync();
            return connection;
        }

        public async Task WaitForABlockedQuery()
        {
            await using var connection = new NpgsqlConnection(ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", connection);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while ((long)(await command.ExecuteScalarAsync(timeout.Token))! == 0)
                await Task.Delay(50, timeout.Token);
        }

        private async Task ExecuteOnServer(string sql)
        {
            await using var connection = new NpgsqlConnection(Server);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }
}
