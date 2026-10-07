using System.Collections.Concurrent;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;
using Kinds = BTCPayServer.Plugins.Satora.Services.SatoraWalletNotification.Kinds;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Services;

// Settles the recoverable funds of every enabled store's wallet and runs its automatic withdrawal,
// both through SatoraWithdrawalService with the store's spend lock held. Each store runs on its own
// task, apart from the Lightning and on-chain workers, so neither ever holds up a claim. A store
// whose Satora is disabled is left alone, as disabling evicts its wallet once nothing needs it.
public class SatoraWalletAutomationWorker(
    SatoraPluginDbContextFactory dbContextFactory,
    SatoraWalletRegistry registry,
    SatoraWalletAccess wallets,
    SatoraWithdrawalService withdrawalService,
    SatoraWithdrawals withdrawals,
    SatoraAutoWithdrawRules rules,
    SatoraNotifier notifier,
    SatoraSentAlerts sentAlerts,
    ILogger<SatoraWalletAutomationWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    // Regtest VTXOs live 512 s, so off mainnet each store is visited on every tick.
    public static readonly TimeSpan MainnetStoreInterval = TimeSpan.FromMinutes(5);
    // The wait after each failure in a row; every failure past the last waits as long as it.
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromHours(1), TimeSpan.FromHours(6)];
    private static readonly TimeSpan BalanceTimeout = TimeSpan.FromSeconds(30);
    private const int FailuresBeforeAlert = 2;

    private readonly SatoraStoreTicks _ticks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextTick = new();
    // By store. Each entry is touched only by its store's tick, and a store runs one tick at a time.
    private readonly ConcurrentDictionary<string, Failures> _settleFailures = new();
    private readonly ConcurrentDictionary<string, Failures> _withdrawFailures = new();
    // The recoverable total ark-client last refused to settle as below dust.
    private readonly ConcurrentDictionary<string, long> _belowDust = new();
    private readonly ConcurrentDictionary<string, bool> _unreadable = new();
    // Interrupted automatic withdrawals the store was told about, by id.
    private readonly SatoraOnceAlerts _alerts = new(sentAlerts, "automation", logger);

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    protected virtual TimeSpan StoreInterval => registry.Network == SatoraBitcoinNetwork.Mainnet ? MainnetStoreInterval : Interval;

    // The registry logs why once at startup.
    protected virtual string? Unavailable => registry.NetworkError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (Unavailable is not null)
            return;
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                ScheduleDueStores(await ListStoresAsync(stoppingToken), stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Could not list the stores whose Satora wallet to settle and withdraw from");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _ticks.WaitAsync(cancellationToken);
    }

    protected virtual async Task<List<string>> ListStoresAsync(CancellationToken cancellationToken)
    {
        await using var ctx = dbContextFactory.CreateContext();
        return await ctx.SatoraSettings
            .Where(s => s.Enabled && s.ProtectedSeed != null)
            .Select(s => s.StoreId)
            .ToListAsync(cancellationToken);
    }

    internal void ScheduleDueStores(List<string> storeIds, CancellationToken stoppingToken)
    {
        var now = UtcNow;
        foreach (var storeId in storeIds)
        {
            if (!IsDue(_nextTick.GetValueOrDefault(storeId), now))
                continue;
            if (_ticks.TryStart(storeId, () => RunTickAsync(storeId, now, stoppingToken)))
                _nextTick[storeId] = now + StoreInterval;
        }
    }

    // now is read after the store listing, whose time varies, so a visit a whole number of intervals
    // after another can read a little less than that. Compared exactly, it would wait a whole tick more.
    private static bool IsDue(DateTimeOffset dueAt, DateTimeOffset now) => dueAt <= now + Interval / 2;

    private async Task RunTickAsync(string storeId, DateTimeOffset now, CancellationToken stoppingToken)
    {
        try
        {
            await TickAsync(storeId, now, stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogError(e, "Settling or withdrawing automatically from the Satora wallet of store {StoreId} stopped with an error", storeId);
        }
    }

    // A wallet that cannot be read, as when it is locked or the Arkade server is down, is skipped
    // until it can be: the registry tells the store once about a locked wallet. now is when the visit
    // was scheduled, which the next visit is scheduled from, so a backoff of whole store intervals
    // ends on a visit rather than just after one.
    internal async Task TickAsync(string storeId, DateTimeOffset now, CancellationToken stoppingToken)
    {
        long recoverableSats;
        try
        {
            var wallet = await wallets.GetAsync(storeId);
            recoverableSats = (long)(await wallet.GetBalanceAsync().WaitAsync(BalanceTimeout, stoppingToken)).recoverableSats;
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            if (_unreadable.TryAdd(storeId, true))
                logger.LogWalletFailure(e, "Could not read the Satora wallet of store {StoreId}; it is not settled or withdrawn from until it can be", storeId);
            return;
        }
        if (_unreadable.TryRemove(storeId, out _))
            logger.LogInformation("The Satora wallet of store {StoreId} can be read again", storeId);
        await SettleAsync(storeId, recoverableSats, now, stoppingToken);
        await AlertInterruptedAsync(storeId, now);
        await WithdrawAsync(storeId, now, stoppingToken);
    }

    // Recoverable funds depend on the Arkade server until they are settled, so they are settled within
    // a tick of turning recoverable, also while a withdrawal is unresolved. A total ark-client refused
    // as below dust is tried again once it changes.
    private async Task SettleAsync(string storeId, long recoverableSats, DateTimeOffset now, CancellationToken stoppingToken)
    {
        var failures = _settleFailures.GetOrAdd(storeId, _ => new Failures(0));
        if (recoverableSats == 0)
        {
            _belowDust.TryRemove(storeId, out _);
            failures.Reset();
            return;
        }
        if (_belowDust.TryGetValue(storeId, out var refused) && refused == recoverableSats || !IsDue(failures.RetryAt, now))
            return;
        var result = await withdrawalService.SettleAsync(storeId, automatic: true, stoppingToken);
        switch (result.Outcome)
        {
            case SatoraSettleOutcome.Settled:
                failures.Reset();
                break;
            case SatoraSettleOutcome.NothingToSettle:
                logger.LogInformation("Settling the recoverable funds of the Satora wallet of store {StoreId}: {Result}", storeId, result.Message);
                failures.Reset();
                break;
            case SatoraSettleOutcome.TooSmall:
                _belowDust[storeId] = recoverableSats;
                failures.Reset();
                break;
            case SatoraSettleOutcome.Failed:
                if (failures.Add(now))
                    await SendAsync(new SatoraWalletNotification { StoreId = storeId, Kind = Kinds.SettleFailing, Error = result.Error });
                break;
            case SatoraSettleOutcome.Blocked:
                // A withdrawal is running, or BTCPay is stopping.
                break;
        }
    }

    // An automatic withdrawal cut short, as by a restart, stays Pending without a swap; the SDK
    // reported nothing, so no Unknown notification went out, and only the owner can tell whether its
    // funds left.
    private async Task AlertInterruptedAsync(string storeId, DateTimeOffset now)
    {
        foreach (var withdrawal in await withdrawals.ListUnresolvedAsync(storeId))
        {
            if (withdrawal is not { Origin: SatoraWithdrawalOrigin.Auto, Status: SatoraWithdrawalStatus.Pending } ||
                !SatoraWithdrawals.OwnerCanResolve(withdrawal, withdrawalService.IsRunning(withdrawal.Id), now) ||
                !await _alerts.TryStartAsync(storeId, withdrawal.Id, Kinds.AutoWithdrawInterrupted))
                continue;
            logger.LogWarning("Automatic withdrawal {WithdrawalId} from the Satora wallet of store {StoreId} was interrupted", withdrawal.Id, storeId);
            await SendAsync(new SatoraWalletNotification { StoreId = storeId, Kind = Kinds.AutoWithdrawInterrupted, Destination = withdrawal.Destination });
        }
    }

    // A rule saved since the last failure, such as one with a destination fixed after the alert, is
    // tried at once. A Lightning payment fails, and its funds come back, only after its run returned
    // Pending, so it counts once SatoraOutgoingWorker has closed it.
    private async Task WithdrawAsync(string storeId, DateTimeOffset now, CancellationToken stoppingToken)
    {
        // Read before the spend lock, which ExecuteAutoAsync waits for, so a store without a rule never waits for it.
        if (await rules.GetAsync(storeId) is not { Enabled: true } rule)
        {
            _withdrawFailures.TryRemove(storeId, out _);
            return;
        }
        var failures = _withdrawFailures.AddOrUpdate(storeId, _ => new Failures(rule.Version),
            (_, current) => current.RuleVersion == rule.Version ? current : new Failures(rule.Version));
        if (failures.PendingId is { } pendingId)
        {
            var pending = await withdrawals.FindAsync(storeId, pendingId);
            if (pending?.Status == SatoraWithdrawalStatus.Pending)
                return;
            failures.PendingId = null;
            if (pending?.Status == SatoraWithdrawalStatus.Failed)
                await FailedAsync(storeId, rule, failures, now, pending.Error ?? "the Lightning payment failed");
            else
                failures.Reset();
        }
        if (!IsDue(failures.RetryAt, now))
            return;
        var result = await withdrawalService.ExecuteAutoAsync(storeId, stoppingToken);
        switch (result.Outcome)
        {
            // SatoraWithdrawalService logs each send and each Lightning payment it hands over.
            case SatoraAutoWithdrawOutcome.Sent:
                failures.Reset();
                break;
            case SatoraAutoWithdrawOutcome.Pending:
                failures.PendingId = result.WithdrawalId;
                break;
            case SatoraAutoWithdrawOutcome.Unknown:
                // Its row holds up every withdrawal, and the store was told, until the owner marks it.
                failures.Reset();
                break;
            case SatoraAutoWithdrawOutcome.Failed:
                await FailedAsync(storeId, rule, failures, now, result.Message);
                break;
            case SatoraAutoWithdrawOutcome.Waiting:
                logger.LogDebug("Automatic withdrawal from the Satora wallet of store {StoreId} waits: {Reason}", storeId, result.Message);
                break;
        }
    }

    private async Task FailedAsync(string storeId, SatoraAutoWithdrawRule rule, Failures failures, DateTimeOffset now, string reason)
    {
        var alert = failures.Add(now);
        logger.LogWarning("Automatic withdrawal from the Satora wallet of store {StoreId} to {Destination} sent nothing ({Reason}); trying again from {RetryAt}",
            storeId, rule.Destination, reason, failures.RetryAt);
        if (alert)
            await SendAsync(new SatoraWalletNotification { StoreId = storeId, Kind = Kinds.AutoWithdrawFailing, Destination = rule.Destination, Error = reason });
    }

    protected virtual async Task SendAsync(SatoraWalletNotification notification)
    {
        try
        {
            await notifier.SendAsync(notification.StoreId, notification);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not send the Satora wallet notification for store {StoreId}", notification.StoreId);
        }
    }

    // Failures in a row of a store's settle or automatic withdrawal, the latter under one version of
    // its rule, and when to try again. Kept in memory: a restart allows one more attempt, and a failed
    // attempt leaves the funds in the wallet.
    private sealed class Failures(int ruleVersion)
    {
        private int _count;
        private bool _alerted;

        public int RuleVersion { get; } = ruleVersion;

        public DateTimeOffset RetryAt { get; private set; }

        // The automatic Lightning withdrawal whose payment is on its way, until it is paid or refunded.
        public string? PendingId { get; set; }

        // Whether to tell the store: once per run of failures, from the second on.
        public bool Add(DateTimeOffset now)
        {
            _count++;
            RetryAt = now + Backoff[Math.Min(_count, Backoff.Length) - 1];
            if (_count < FailuresBeforeAlert || _alerted)
                return false;
            _alerted = true;
            return true;
        }

        public void Reset() => (_count, _alerted, RetryAt) = (0, false, default);
    }
}
