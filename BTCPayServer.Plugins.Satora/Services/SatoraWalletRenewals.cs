using Satora.Sdk;

namespace BTCPayServer.Plugins.Satora.Services;

// Renews a store's funds about to expire. The SDK leaves VTXOs that expire within its margin out of
// every send, swap funding and exit, as an output expires with its inputs, and refuses a payment the
// wallet cannot cover without them; they become recoverable, and can be settled, only once expired.
// A renewal rolls them into the next Arkade batch as fresh VTXOs, after which the payment can be
// tried again. Each renewal holds the store's spend lock, so it runs after the spend in progress.
public class SatoraWalletRenewals(SatoraWalletAccess wallets, ILogger<SatoraWalletRenewals> logger) : IHostedService
{
    // The SDK names the call that renews them in both of its refusals for them.
    private const string RenewCall = "arkade_renew";

    private readonly SatoraStoreTicks _running = new();
    private readonly CancellationTokenSource _stopping = new();

    // A payment the wallet cannot cover without its funds about to expire, refused before the SDK
    // recorded or sent anything. Other refusals share its kind, so only its text tells it apart.
    public static bool IsExpiringRefusal(Exception? error) =>
        error is SdkException { Kind: SdkErrorKind.InvalidSwap } sdk && sdk.Message.Contains(RenewCall, StringComparison.Ordinal);

    // Also a retried swap funding that must spend a journaled input about to expire: it fails with
    // Transport until a renewal spends that input or the input expires.
    public static bool NeedsRenewal(Exception? error) =>
        IsExpiringRefusal(error) ||
        error is SdkException { Kind: SdkErrorKind.Transport } sdk && sdk.Message.Contains(RenewCall, StringComparison.Ordinal);

    // Starts a renewal of the store's funds about to expire on its own task, unless one is running.
    public virtual void Start(string storeId) =>
        _running.TryStart(storeId, async () =>
        {
            try
            {
                await RenewAsync(storeId, _stopping.Token);
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                logger.LogWalletFailure(e, "Renewing the funds about to expire in the Satora wallet of store {StoreId} failed", storeId);
            }
        });

    // The renewal's Arkade batch, or null when nothing was about to expire. cancellationToken stops the
    // wait for the spend lock, and once it holds the lock, the renewal before it starts.
    public virtual async Task<string?> RenewAsync(string storeId, CancellationToken cancellationToken)
    {
        var batch = await wallets.SpendAsync(storeId, wallet =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return wallet.RenewArkadeAsync();
        }, cancellationToken);
        if (batch is not null)
            logger.LogInformation("Renewed the funds about to expire in the Satora wallet of store {StoreId} in Arkade batch {TxId}", storeId, batch);
        return batch;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // A renewal pays only the wallet itself, so one the process exit cuts short loses nothing.
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await _running.WaitAsync(cancellationToken);
    }
}
