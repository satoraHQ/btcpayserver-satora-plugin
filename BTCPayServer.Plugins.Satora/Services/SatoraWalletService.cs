using System.Collections.Concurrent;
using System.Security.Cryptography;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;

namespace BTCPayServer.Plugins.Satora.Services;

public class SatoraWalletService(
    SatoraPluginDbContextFactory dbContextFactory,
    SatoraSeedProtector seedProtector,
    SatoraWalletRegistry registry,
    ILogger<SatoraWalletService> logger)
{
    private static readonly TimeSpan BalanceTimeout = TimeSpan.FromSeconds(20);

    // Wallet changes are rare. A lock per store keeps a generate, restore, backup confirmation or
    // toggle from acting on a seed another of them has just replaced, without a replace waiting for
    // one store's swaps holding up the others.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _walletLocks = new();

    public string? NetworkError => registry.NetworkError;

    public virtual Task<SatoraWalletStatus> GetStatusAsync(string storeId) => registry.ReadStatusAsync(storeId);

    public async Task<(SeedWriteRefusal Refusal, string? Mnemonic)> GenerateAsync(string storeId, string? confirmation)
    {
        var mnemonic = SatoraMnemonic.Generate();
        var refusal = Logged(storeId, SeedWriteKind.Generate, await WriteSeedAsync(storeId, mnemonic, SeedWriteKind.Generate, confirmation));
        return (refusal, refusal == SeedWriteRefusal.None ? mnemonic : null);
    }

    public async Task<SeedWriteRefusal> RestoreAsync(string storeId, string mnemonic, string? confirmation) =>
        Logged(storeId, SeedWriteKind.Restore, await WriteSeedAsync(storeId, mnemonic, SeedWriteKind.Restore, confirmation));

    private SeedWriteRefusal Logged(string storeId, SeedWriteKind kind, SeedWriteRefusal refusal)
    {
        if (refusal != SeedWriteRefusal.None)
            logger.LogInformation("{SeedWrite} for store {StoreId} was refused ({Refusal})",
                kind == SeedWriteKind.Generate ? "Generating a new Satora wallet" : "Restoring a Satora wallet", storeId, refusal);
        else if (kind == SeedWriteKind.Generate)
            logger.LogInformation("Generated a new Satora wallet for store {StoreId}", storeId);
        else
            logger.LogInformation("Restored a Satora wallet for store {StoreId} from its recovery phrase", storeId);
        return refusal;
    }

    public async Task<bool> ConfirmBackupAsync(string storeId, string? typedMnemonic)
    {
        if (!SatoraMnemonic.TryParse(typedMnemonic, out var mnemonic))
            return false;
        var walletLock = WalletLock(storeId);
        await walletLock.WaitAsync();
        try
        {
            await using var ctx = dbContextFactory.CreateContext();
            var settings = await ctx.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == storeId);
            var status = seedProtector.Read(settings);
            if (settings is null || status.State != SatoraWalletState.BackupRequired || status.Mnemonic != mnemonic)
                return false;
            settings.BackupConfirmed = true;
            settings.BackupConfirmedAt = DateTimeOffset.UtcNow;
            await ctx.SaveChangesAsync();
            return true;
        }
        finally
        {
            walletLock.Release();
        }
    }

    // Disabling stops new payments only: the client stays while swaps still need it to finish.
    public async Task<bool> SetEnabledAsync(string storeId, bool enabled)
    {
        var walletLock = WalletLock(storeId);
        await walletLock.WaitAsync();
        try
        {
            await using var ctx = dbContextFactory.CreateContext();
            var settings = await ctx.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == storeId);
            if (enabled && (registry.NetworkError is not null || seedProtector.Read(settings).State != SatoraWalletState.Ready))
                return false;
            if (settings is null)
                return true;
            var wasEnabled = settings.Enabled;
            settings.Enabled = enabled;
            await ctx.SaveChangesAsync();
            if (wasEnabled != enabled)
                logger.LogInformation("{Change} the Satora wallet of store {StoreId}", enabled ? "Enabled" : "Disabled", storeId);
            if (enabled)
                registry.Warm(storeId);
            else
                await registry.EvictAsync(storeId, async () => await CountOpenOperationsAsync(storeId) == 0);
            return true;
        }
        finally
        {
            walletLock.Release();
        }
    }

    public async Task<ArkadeBalance?> TryGetBalanceAsync(string storeId)
    {
        StoreWallet wallet;
        try
        {
            wallet = await registry.GetAsync(storeId);
        }
        catch (Exception e)
        {
            logger.LogWalletFailure(e, "Could not open the Satora wallet of store {StoreId}", storeId);
            return null;
        }
        return await TryReadBalanceAsync(wallet);
    }

    // Swaps (and, once they exist, withdrawals in an unknown state) that still need the store's
    // current seed. Every table that holds such rows adds its count here. Rows from before the
    // current seed was created belong to a seed it replaced: a replace needs none of them open. A
    // restore over a legacy seed goes ahead with rows open, as SeedWriteGuard says.
    public async Task<int> CountOpenOperationsAsync(string storeId)
    {
        await using var ctx = dbContextFactory.CreateContext();
        var open = await OpenOperationsAsync(ctx, storeId);
        return await open.Swaps.CountAsync();
    }

    // What CountOpenOperationsAsync counts, for the store to find, by id. A store can have many, so
    // only the first few of each kind are named.
    public async Task<string> DescribeOpenOperationsAsync(string storeId)
    {
        const int named = 5;
        await using var ctx = dbContextFactory.CreateContext();
        var open = await OpenOperationsAsync(ctx, storeId);
        var parts = new List<string>();
        Add("swaps of an earlier plugin version", await open.Swaps.OrderBy(t => t.DateT).Select(t => t.TxID).ToListAsync());
        return string.Join("; ", parts);

        void Add(string kind, List<string> ids)
        {
            var distinct = ids.Distinct().ToList();
            if (distinct.Count > 0)
                parts.Add($"{kind}: {string.Join(", ", distinct.Take(named))}" + (distinct.Count > named ? $" and {distinct.Count - named} more" : ""));
        }
    }

    private sealed record OpenOperations(
        IQueryable<SatoraTx> Swaps);

    private static async Task<OpenOperations> OpenOperationsAsync(SatoraPluginDbContext ctx, string storeId)
    {
        var walletCreated = await ctx.SatoraSettings
            .Where(s => s.StoreId == storeId)
            .Select(s => s.WalletCreated)
            .FirstOrDefaultAsync();
        var swaps = ctx.SatoraTransactions.Where(t =>
            t.StoreId == storeId && (t.Status == null || !SeedWriteGuard.SeedNotNeededStatuses.Contains(t.Status)));
        if (walletCreated is { } created)
        {
            var since = created.UtcDateTime;
            swaps = swaps.Where(t => t.DateT >= since);
        }
        return new OpenOperations(swaps);
    }

    private async Task<SeedWriteRefusal> WriteSeedAsync(string storeId, string mnemonic, SeedWriteKind kind, string? confirmation)
    {
        if (registry.NetworkError is not null)
            return SeedWriteRefusal.NetworkUnsupported;
        var walletLock = WalletLock(storeId);
        await walletLock.WaitAsync();
        try
        {
            await using var ctx = dbContextFactory.CreateContext();
            var settings = await ctx.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == storeId);
            var current = seedProtector.Read(settings);
            var fingerprint = SatoraMnemonic.Fingerprint(mnemonic);
            // A readable seed is compared whole: a fingerprint is 32 bits, so a phrase can be ground to match it.
            var sameWallet = kind == SeedWriteKind.Restore &&
                             (current.Mnemonic is not null ? current.Mnemonic == mnemonic : settings?.SeedFingerprint == fingerprint);

            async Task SaveAsync()
            {
                var now = DateTimeOffset.UtcNow;
                if (settings is null)
                {
                    settings = new SatoraSettings { StoreId = storeId };
                    ctx.SatoraSettings.Add(settings);
                }
                // A readable seed restored from its own phrase stays as it is. A locked one matched
                // only its 32-bit fingerprint, so its ciphertext is kept.
                if (!sameWallet || current.Mnemonic is null)
                    Retire(ctx, settings, now);
                settings.ProtectedSeed = seedProtector.Protect(storeId, mnemonic);
                settings.CryptedSeed = null;
                settings.SeedFingerprint = fingerprint;
                settings.BackupConfirmed = kind == SeedWriteKind.Restore;
                settings.BackupConfirmedAt = settings.BackupConfirmed ? now : null;
                if (sameWallet)
                {
                    settings.LightningConnectionKey ??= NewConnectionKey();
                }
                else
                {
                    settings.Enabled = false;
                    settings.WalletCreated = now;
                    settings.LightningConnectionKey = NewConnectionKey();
                }
                await ctx.SaveChangesAsync();
            }

            if (!SeedWriteGuard.IsReplace(current.State, kind, sameWallet))
            {
                // Nothing to evict: there is no client without a readable seed, and a restore of the
                // same wallet keeps the client it has.
                var refusal = SeedWriteGuard.Check(current.State, kind, sameWallet, 0, 0, confirmation);
                if (refusal != SeedWriteRefusal.None)
                    return refusal;
                await SaveAsync();
                if (settings!.Enabled)
                    registry.Warm(storeId);
                return SeedWriteRefusal.None;
            }
            // Counted inside the registry's gate, so no swap can be created from the old wallet after
            // the count and be stranded by the replace.
            return await registry.ReplaceAsync(storeId, async wallet =>
            {
                var refusal = SeedWriteGuard.Check(
                    current.State,
                    kind,
                    sameWallet,
                    await CountOpenOperationsAsync(storeId),
                    TotalSats(wallet is null ? null : await TryReadBalanceAsync(wallet)),
                    confirmation);
                if (refusal != SeedWriteRefusal.None)
                    return (refusal, false);
                await SaveAsync();
                return (SeedWriteRefusal.None, true);
            });
        }
        finally
        {
            walletLock.Release();
        }
    }

    private async Task<ArkadeBalance?> TryReadBalanceAsync(StoreWallet wallet)
    {
        try
        {
            return await wallet.Client.GetArkadeBalanceAsync().WaitAsync(BalanceTimeout);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read the Satora wallet balance of store {StoreId}", wallet.StoreId);
            return null;
        }
    }

    private static void Retire(SatoraPluginDbContext ctx, SatoraSettings settings, DateTimeOffset now)
    {
        if (!string.IsNullOrEmpty(settings.ProtectedSeed))
            ctx.SatoraRetiredSeeds.Add(new SatoraRetiredSeed
            {
                Id = Guid.NewGuid().ToString(),
                StoreId = settings.StoreId,
                Format = SatoraRetiredSeed.DataProtectionFormat,
                Ciphertext = settings.ProtectedSeed,
                SeedFingerprint = settings.SeedFingerprint,
                RetiredAt = now
            });
        if (!string.IsNullOrEmpty(settings.CryptedSeed))
            ctx.SatoraRetiredSeeds.Add(new SatoraRetiredSeed
            {
                Id = Guid.NewGuid().ToString(),
                StoreId = settings.StoreId,
                Format = SatoraRetiredSeed.LegacyFormat,
                Ciphertext = settings.CryptedSeed,
                RetiredAt = now
            });
    }

    private SemaphoreSlim WalletLock(string storeId) => _walletLocks.GetOrAdd(storeId, _ => new SemaphoreSlim(1, 1));

    private static ulong? TotalSats(ArkadeBalance? balance) =>
        balance is null ? null : balance.confirmedSats + balance.preConfirmedSats + balance.recoverableSats;

    private static string NewConnectionKey() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}
