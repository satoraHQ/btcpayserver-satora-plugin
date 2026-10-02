using System.Collections.Concurrent;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SdkClient = Satora.Sdk.Client;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Services;

// One SDK client per store. A swap can only be funded, claimed or refunded by a client over the
// data directory that created it, and the first arkd connection is slow, so clients are long-lived.
public sealed class SatoraWalletRegistry : IHostedService, IDisposable
{
    private readonly ConcurrentDictionary<string, Lazy<Task<StoreWallet>>> _wallets = new();
    // Stores whose wallet is being replaced or evicted. GetAsync waits for the task.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _exclusive = new();
    private readonly ConcurrentDictionary<string, bool> _lockNotified = new();
    private readonly ConcurrentDictionary<Task, bool> _warmUps = new();
    private volatile bool _disposed;
    private readonly SatoraPluginDbContextFactory _dbContextFactory;
    private readonly SatoraSeedProtector _seedProtector;
    private readonly SatoraNotifier _notifier;
    private readonly ILogger<SatoraWalletRegistry> _logger;
    private readonly string _dataDir;
    private readonly SatoraEndpoints _endpoints = new(null, null, null);

    public SatoraWalletRegistry(
        SatoraPluginDbContextFactory dbContextFactory,
        SatoraSeedProtector seedProtector,
        SatoraNotifier notifier,
        BTCPayNetworkProvider networkProvider,
        IConfiguration configuration,
        IOptions<DataDirectories> dataDirectories,
        ILogger<SatoraWalletRegistry> logger)
    {
        _dbContextFactory = dbContextFactory;
        _seedProtector = seedProtector;
        _notifier = notifier;
        _logger = logger;
        _dataDir = dataDirectories.Value.DataDir;
        try
        {
            var chain = networkProvider.GetNetwork<BTCPayNetwork>("BTC")?.NBitcoinNetwork.ChainName
                        ?? throw new NotSupportedException("Satora needs BTCPay's BTC network.");
            var network = SatoraNetworks.FromChain(chain);
            Network = network;
            _endpoints = SatoraNetworks.Endpoints(configuration, network);
            if (network == SatoraBitcoinNetwork.Mainnet && SatoraNetworks.HasEndpointOverrides(configuration))
                _logger.LogWarning("Satora endpoint overrides are ignored on mainnet");
        }
        catch (NotSupportedException e)
        {
            NetworkError = e.Message;
        }
    }

    public SatoraBitcoinNetwork? Network { get; }

    // Set when BTCPay runs on a network Satora does not support; no wallet can be created then.
    public string? NetworkError { get; }

    public async Task<SatoraWalletStatus> ReadStatusAsync(string storeId)
    {
        SatoraSettings? settings;
        await using (var ctx = _dbContextFactory.CreateContext())
            settings = await ctx.SatoraSettings.AsNoTracking().FirstOrDefaultAsync(s => s.StoreId == storeId);
        var status = _seedProtector.Read(settings);
        if (status.State == SatoraWalletState.Locked)
            await NotifyLockedOnce(storeId);
        else
            _lockNotified.TryRemove(storeId, out _);
        return status;
    }

    // For reads (balance, address). A replace may retire the wallet right after it is returned, so
    // swaps are created and funds spent through UseAsync.
    public async Task<StoreWallet> GetAsync(string storeId)
    {
        while (true)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_exclusive.TryGetValue(storeId, out var exclusive))
            {
                await exclusive.Task;
                continue;
            }
            var entry = _wallets.GetOrAdd(storeId, NewEntry);
            var wallet = await GetOrRemoveAsync(storeId, entry);
            ObjectDisposedException.ThrowIf(_disposed, this);
            // A replace or eviction that started while the client was being built decides without this caller.
            if (!wallet.Retired && !_exclusive.ContainsKey(storeId))
                return wallet;
        }
    }

    // Lets a test start a replace after GetAsync has handed UseAsync a wallet and before UseAsync takes its SpendLock.
    internal Func<Task>? BeforeSpendLock { get; set; }

    // Runs action with the store's current wallet and its SpendLock held: a replace or eviction counts
    // open swaps only before action starts or after it returns, and action never gets a wallet one of
    // them retired. action writes the rows CountOpenOperationsAsync counts before it returns. It does
    // not call GetAsync or UseAsync for the same store, nor any SatoraWalletService write method, each
    // of which would wait for a replace or eviction that is waiting for action.
    public async Task<T> UseAsync<T>(string storeId, Func<StoreWallet, Task<T>> action)
    {
        while (true)
        {
            var wallet = await GetAsync(storeId);
            if (BeforeSpendLock is { } beforeSpendLock)
                await beforeSpendLock();
            await wallet.SpendLock.WaitAsync();
            try
            {
                if (!wallet.Retired)
                    return await action(wallet);
            }
            finally
            {
                wallet.SpendLock.Release();
            }
        }
    }

    // Builds the client and connects to arkd in the background, so the first payment request does not wait for it.
    public void Warm(string storeId)
    {
        var warmUp = Task.Run(async () =>
        {
            try
            {
                var wallet = await GetAsync(storeId);
                await wallet.GetArkadeAddressAsync();
            }
            catch (Exception e)
            {
                _logger.LogWalletFailure(e, "Could not prepare the Satora wallet of store {StoreId}", storeId);
            }
        });
        _warmUps.TryAdd(warmUp, true);
        _ = warmUp.ContinueWith(done => _warmUps.TryRemove(done, out _), CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // Dispose cannot stop a native call already running, and the warm-up's call can still write to the
    // store's data directory, so a test waits for the warm-ups it started before deleting it.
    internal Task WaitForWarmUpsAsync() => Task.WhenAll(_warmUps.Keys);

    // Seed replace. decide gets the store's current wallet, built from the stored seed if there is
    // none yet (null if it cannot be built), and returns whether it replaced the seed.
    public Task<T> ReplaceAsync<T>(string storeId, Func<StoreWallet?, Task<(T Result, bool Replaced)>> decide) =>
        ExclusiveAsync(storeId, build: true, decide);

    // Retires the store's wallet, if one was built, when canEvict says nothing needs it any more.
    public Task EvictAsync(string storeId, Func<Task<bool>> canEvict) =>
        ExclusiveAsync(storeId, build: false, async wallet => (true, wallet is not null && await canEvict()));

    // GetAsync and UseAsync wait while body runs, and body starts only once a UseAsync action holding
    // the wallet's SpendLock has returned, so whatever body counts cannot change under it. A wallet
    // body retires is disposed and dropped before GetAsync can hand it out again. body must not call
    // GetAsync or UseAsync for the store.
    private async Task<T> ExclusiveAsync<T>(string storeId, bool build, Func<StoreWallet?, Task<(T Result, bool Retire)>> body)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        while (!_exclusive.TryAdd(storeId, gate))
        {
            if (_exclusive.TryGetValue(storeId, out var other))
                await other.Task;
        }
        try
        {
            var entry = build ? _wallets.GetOrAdd(storeId, NewEntry) : _wallets.GetValueOrDefault(storeId);
            StoreWallet? wallet = null;
            if (entry is not null)
            {
                try
                {
                    wallet = await GetOrRemoveAsync(storeId, entry);
                }
                catch (Exception e)
                {
                    _logger.LogWalletFailure(e, "Could not build the Satora wallet of store {StoreId}", storeId);
                }
            }
            if (wallet is null)
                return (await body(null)).Result;
            await wallet.SpendLock.WaitAsync();
            try
            {
                var (result, retire) = await body(wallet);
                if (retire)
                {
                    _wallets.TryRemove(KeyValuePair.Create(storeId, entry!));
                    wallet.Retire();
                }
                return result;
            }
            finally
            {
                wallet.SpendLock.Release();
            }
        }
        finally
        {
            _exclusive.TryRemove(KeyValuePair.Create(storeId, gate));
            gate.SetResult();
        }
    }

    private Lazy<Task<StoreWallet>> NewEntry(string storeId) => new(() => CreateAsync(storeId));

    private async Task<StoreWallet> GetOrRemoveAsync(string storeId, Lazy<Task<StoreWallet>> entry)
    {
        try
        {
            return await entry.Value;
        }
        catch
        {
            _wallets.TryRemove(KeyValuePair.Create(storeId, entry));
            throw;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (NetworkError is not null)
        {
            _logger.LogWarning("Satora wallets are unavailable: {Reason}", NetworkError);
            return Task.CompletedTask;
        }
        _ = Task.Run(WarmEnabledStores, CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // _disposed is set first, so a build the walk misses disposes its own client. A build still
    // running when the walk reaches it has its client disposed once it completes.
    public void Dispose()
    {
        _disposed = true;
        foreach (var entry in _wallets.Values)
        {
            if (entry.IsValueCreated)
                _ = entry.Value.ContinueWith(build =>
                {
                    if (build.IsCompletedSuccessfully)
                        build.Result.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        _wallets.Clear();
    }

    // Lets a test hold a build once its client is made.
    internal Func<StoreWallet, Task>? AfterBuild { get; set; }

    private async Task<StoreWallet> CreateAsync(string storeId)
    {
        if (Network is not { } network)
            throw new SatoraWalletUnavailableException(NetworkError ?? "Unsupported network.");
        var status = await ReadStatusAsync(storeId);
        if (status.Mnemonic is null)
            throw new SatoraWalletUnavailableException($"The store's Satora wallet is not usable ({status.State}).");
        var directory = SatoraNetworks.WalletDirectory(_dataDir, network, storeId);
        SatoraNetworks.CreateOwnerOnlyDirectory(directory);
        var client = new SdkClient(
            status.Mnemonic,
            network,
            baseUrl: _endpoints.BaseUrl,
            arkadeServerUrl: _endpoints.ArkadeServerUrl,
            esploraUrl: _endpoints.EsploraUrl,
            dataDirectory: directory);
        var wallet = new StoreWallet(storeId, client);
        if (AfterBuild is { } afterBuild)
            await afterBuild(wallet);
        if (_disposed)
        {
            wallet.Dispose();
            throw new ObjectDisposedException(GetType().FullName);
        }
        return wallet;
    }

    private async Task WarmEnabledStores()
    {
        try
        {
            List<string> storeIds;
            await using (var ctx = _dbContextFactory.CreateContext())
                storeIds = await ctx.SatoraSettings
                    .Where(s => s.Enabled && s.ProtectedSeed != null)
                    .Select(s => s.StoreId)
                    .ToListAsync();
            foreach (var storeId in storeIds)
                Warm(storeId);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not list the stores whose Satora wallet to prepare");
        }
    }

    private async Task NotifyLockedOnce(string storeId)
    {
        if (!_lockNotified.TryAdd(storeId, true))
            return;
        _logger.LogError("The Satora wallet seed of store {StoreId} cannot be decrypted with BTCPay's data protection keys", storeId);
        try
        {
            await _notifier.SendAsync(storeId, new SatoraWalletLockedNotification { StoreId = storeId });
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not send the Satora locked wallet notification for store {StoreId}", storeId);
        }
    }
}

public sealed class StoreWallet : IDisposable
{
    private string? _arkadeAddress;
    private volatile bool _retired;

    internal StoreWallet(string storeId, SdkClient client)
    {
        StoreId = storeId;
        Client = client;
    }

    public string StoreId { get; }

    public SdkClient Client { get; }

    // Held by UseAsync actions, and by a replace or eviction while it decides.
    internal SemaphoreSlim SpendLock { get; } = new(1, 1);

    internal bool Retired => _retired;

    public async Task<string> GetArkadeAddressAsync(CancellationToken cancellationToken = default) =>
        _arkadeAddress ??= await Client.GetArkadeAddressAsync(cancellationToken);

    internal void Retire()
    {
        _retired = true;
        Client.Dispose();
    }

    public void Dispose() => Client.Dispose();
}

// Thrown for a wallet its state keeps from being built, such as a locked seed, which ReadStatusAsync
// logs once as an error and tells the store about.
public sealed class SatoraWalletUnavailableException(string message) : Exception(message);

public static class SatoraWalletLogging
{
    // A failure to reach a store's wallet. One that only repeats what the registry has already said,
    // as for a locked wallet, goes to Debug without its stack trace; any other is a warning.
    public static void LogWalletFailure(this ILogger logger, Exception error, string message, params object?[] args)
    {
        if (error is SatoraWalletUnavailableException)
            logger.LogDebug(message + " ({Reason})", [.. args, error.Message]);
        else
            logger.LogWarning(error, message, args);
    }
}
