using System.Collections.Concurrent;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using Problems = BTCPayServer.Plugins.Satora.Lightning.SatoraLightningNotification.Problems;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;

namespace BTCPayServer.Plugins.Satora.Lightning;

// The only place incoming Lightning swaps are claimed and credited. Each store runs on its own task,
// one tick at a time, so a slow claim in one store never holds up another. Once the worker is
// stopping no claim starts, and a failure, such as a call on a client BTCPay has disposed, counts
// against no swap.
public class SatoraLightningWorker(
    SatoraLightningSwaps swaps,
    SatoraLightningWallets wallets,
    SatoraLightningCredit credit,
    SatoraLightningInvoiceHub invoiceHub,
    SatoraNotifier notifier,
    SatoraSentAlerts sentAlerts,
    ILogger<SatoraLightningWorker> logger) : BackgroundService
{
    // While a store has an invoice that can still be paid and claimed, a payment should land within seconds.
    public static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(30);
    // Long enough to ride out a swap server restart, short against the 20 minutes a mainnet invoice stays payable.
    public static readonly TimeSpan StatusFailureAlertAfter = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private const int ClaimFailuresBeforeAlert = 2;

    private readonly SatoraStoreTicks _ticks = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextTick = new();
    private readonly ConcurrentDictionary<string, int> _claimFailures = new();
    private readonly ConcurrentDictionary<string, bool> _lateClaimFailureLogged = new();
    private readonly SatoraOnceAlerts _alerts = new(sentAlerts, "lightning", logger);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _statusFailingSince = new();

    public static TimeSpan Interval(IEnumerable<DateTimeOffset> openClaimCutoffs, DateTimeOffset now) =>
        openClaimCutoffs.Any(cutoff => cutoff > now) ? ActiveInterval : IdleInterval;

    // The earliest claim cutoff still ahead once status reads have failed for StatusFailureAlertAfter.
    // Null while a store has nothing left the outage could stop it claiming.
    public static DateTimeOffset? StatusOutageCutoff(IEnumerable<SatoraLightningSwap> open, DateTimeOffset failingSince, DateTimeOffset now) =>
        now - failingSince < StatusFailureAlertAfter
            ? null
            : open.Where(s => !s.DoNotClaim && s.ClaimCutoff > now).Select(s => (DateTimeOffset?)s.ClaimCutoff).Min();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ActiveInterval);
        do
        {
            try
            {
                ScheduleDueStores(await swaps.ListOpenAsync(stoppingToken), stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Could not list the open Satora Lightning swaps");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _ticks.WaitAsync(cancellationToken);
    }

    private void ScheduleDueStores(List<(string StoreId, DateTimeOffset ClaimCutoff)> open, CancellationToken stoppingToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var store in open.GroupBy(s => s.StoreId))
        {
            var storeId = store.Key;
            if (_nextTick.GetValueOrDefault(storeId) > now)
                continue;
            var interval = Interval(store.Select(s => s.ClaimCutoff), now);
            _ticks.TryStart(storeId, async () =>
            {
                try
                {
                    await TickAsync(storeId, stoppingToken);
                }
                catch (Exception e) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogError(e, "Satora Lightning swaps of store {StoreId} could not be processed", storeId);
                }
                finally
                {
                    _nextTick[storeId] = DateTimeOffset.UtcNow + interval;
                }
            });
        }
    }

    internal async Task TickAsync(string storeId, CancellationToken stoppingToken)
    {
        var open = await swaps.ListOpenAsync(storeId, stoppingToken);
        // The wallet's receipt is all a claimed swap needs to be credited: neither a status the server
        // reports nor one it cannot read may hold it up or close it.
        foreach (var swap in open.Where(s => s.ClaimTxId is not null && !s.Credited))
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            try
            {
                await CreditAsync(swap);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Claimed Satora Lightning swap {SwapId} of store {StoreId} could not be credited; retrying", swap.SwapId, storeId);
            }
        }
        var unclaimed = open.Where(s => s.ClaimTxId is null).ToList();
        if (unclaimed.Count == 0)
            return;
        BulkStatus bulk;
        try
        {
            bulk = await wallets.GetBulkStatusAsync(storeId, unclaimed.Select(s => s.SwapId), stoppingToken).WaitAsync(StatusTimeout, stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            await StatusReadFailedAsync(storeId, unclaimed, e);
            return;
        }
        _statusFailingSince.TryRemove(storeId, out _);
        await _alerts.ForgetAsync(storeId, Problems.StatusUnavailable);

        var statuses = bulk.Statuses.ToDictionary(s => s.Id, s => SatoraSwapStatuses.From(s.Status));
        var notFound = bulk.NotFound.ToHashSet();
        foreach (var swap in unclaimed)
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            try
            {
                if (statuses.TryGetValue(swap.SwapId, out var status))
                {
                    if (status != swap.Status)
                    {
                        await swaps.SetStatusAsync(swap.SwapId, status);
                        swap.Status = status;
                    }
                    await ActAsync(swap, status, stoppingToken);
                }
                else if (notFound.Contains(swap.SwapId))
                {
                    await UnknownToServerAsync(swap);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Satora Lightning swap {SwapId} of store {StoreId} could not be processed; retrying", swap.SwapId, storeId);
            }
        }
    }

    private async Task StatusReadFailedAsync(string storeId, List<SatoraLightningSwap> open, Exception error)
    {
        var now = DateTimeOffset.UtcNow;
        if (_statusFailingSince.TryAdd(storeId, now))
            logger.LogWalletFailure(error, "Could not read the Satora Lightning swaps of store {StoreId}; retrying", storeId);
        if (StatusOutageCutoff(open, _statusFailingSince.GetValueOrDefault(storeId, now), now) is { } cutoff)
            await AlertOnce(storeId, null, cutoff, Problems.StatusUnavailable);
    }

    private async Task ActAsync(SatoraLightningSwap swap, SatoraSwapStatus status, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        switch (SatoraLightningActions.Decide(swap, status, now))
        {
            case SatoraLightningAction.Claim:
                if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else if (SatoraLightningActions.NearCutoff(swap, status, DateTimeOffset.UtcNow))
                    await AlertOnce(swap.StoreId, swap, swap.ClaimCutoff, Problems.NearClaimCutoff);
                break;
            case SatoraLightningAction.FinishClaim:
                // Past the cutoff the SDK starts no new claim, but finishes one it journaled, also after a
                // transport error or timeout. Without one, the server refunds its VHTLC and the payer gets
                // the payment back.
                if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else if (SatoraLightningActions.NearCutoff(swap, status, now))
                    await AlertOnce(swap.StoreId, swap, swap.ClaimCutoff, Problems.NearClaimCutoff);
                break;
            case SatoraLightningAction.Wait:
                if (SatoraLightningActions.NearCutoff(swap, status, now))
                    await AlertOnce(swap.StoreId, swap, swap.ClaimCutoff, Problems.NearClaimCutoff);
                break;
            case SatoraLightningAction.Credit:
                await CreditAsync(swap);
                break;
            case SatoraLightningAction.Complete:
                await CompleteAsync(swap, now);
                break;
            case SatoraLightningAction.CompleteRefunded:
                logger.LogWarning("Satora Lightning swap {SwapId} of store {StoreId} was paid, but the swap server refunded it before it was claimed",
                    swap.SwapId, swap.StoreId);
                await AlertOnce(swap.StoreId, swap, swap.ClaimCutoff, Problems.RefundedUnclaimed);
                await CompleteAsync(swap, now);
                break;
            case SatoraLightningAction.CompleteUnverified:
                // Past the cutoff the SDK starts no new claim, but still returns the receipt of one it journaled.
                if (!swap.DoNotClaim && await ClaimAsync(swap, cancellationToken))
                {
                    await CreditAsync(swap);
                    break;
                }
                await AlertOnce(swap.StoreId, swap, swap.ClaimCutoff, Problems.ClaimUnverified);
                // The SDK reaches arkd before it reads its journal, so a failed attempt says nothing about a
                // claim it may still finish: only its word that none can complete, which is final, closes
                // the swap. A query that fails moves nothing.
                if (!swap.DoNotClaim && await wallets.ClaimPossibleAsync(swap.StoreId, swap.SwapId))
                    break;
                logger.LogError("Satora Lightning swap {SwapId} of store {StoreId} is {Status} on the swap server, but the wallet has no receipt for claiming it and no claim can complete; it was not credited",
                    swap.SwapId, swap.StoreId, status);
                await CompleteAsync(swap, now);
                break;
            case SatoraLightningAction.Alert:
                logger.LogError("Satora Lightning swap {SwapId} of store {StoreId} reached an unexpected status, {Status}; nothing was moved",
                    swap.SwapId, swap.StoreId, status);
                await AlertOnce(swap.StoreId, swap, null, Problems.UnexpectedStatus, status);
                if (now >= swap.ClaimCutoff)
                    await CompleteAsync(swap, now);
                break;
        }
    }

    // The SDK journals the claim before submitting it, so calling again after any error resumes it
    // rather than claiming twice, and a claim that already went through returns its receipt.
    private async Task<bool> ClaimAsync(SatoraLightningSwap swap, CancellationToken cancellationToken)
    {
        try
        {
            var destination = await wallets.GetClaimAddressAsync(swap.StoreId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // After the address, which can be slow to fetch, so a CancelInvoice meanwhile still stops the
            // claim; one after it is refused.
            if (!await swaps.StartClaimAsync(swap.StoreId, swap.SwapId, DateTimeOffset.UtcNow))
            {
                swap.DoNotClaim = true;
                return false;
            }
            var txid = await wallets.ClaimAsync(swap.StoreId, swap.SwapId, destination, cancellationToken) switch
            {
                SdkArkadeSettlement.Offchain offchain => offchain.arkTxid,
                SdkArkadeSettlement.Delegated delegated => delegated.commitmentTxid,
                var other => throw new InvalidOperationException($"The SDK settled the claim with an unexpected outcome, {other}.")
            };
            await swaps.SetClaimedAsync(swap.SwapId, txid);
            swap.ClaimTxId = txid;
            logger.LogInformation("Claimed Satora Lightning swap {SwapId} of store {StoreId}", swap.SwapId, swap.StoreId);
            return true;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            var failures = _claimFailures.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1);
            var claimable = DateTimeOffset.UtcNow < swap.ClaimCutoff;
            // Past the cutoff the claim is retried every tick, as often as every 5 s, while the SDK may
            // still finish it; one warning is enough.
            var level = claimable || _lateClaimFailureLogged.TryAdd(swap.SwapId, true) ? LogLevel.Warning : LogLevel.Debug;
            logger.Log(level, e, "Claiming Satora Lightning swap {SwapId} of store {StoreId} failed ({Failures} so far)", swap.SwapId, swap.StoreId, failures);
            // Past the cutoff the caller alerts about the swap instead, once.
            if (failures == ClaimFailuresBeforeAlert && claimable)
                await Notify(swap.StoreId, swap, swap.ClaimCutoff, Problems.ClaimFailing, null);
            return false;
        }
    }

    // Nothing goes to the listener hub: the payment is recorded here, and BTCPay's listener recording
    // it again would only fail on the payments primary key.
    private async Task CreditAsync(SatoraLightningSwap swap)
    {
        var paidAt = DateTimeOffset.UtcNow;
        var outcome = await credit.CreditAsync(swap, paidAt);
        await swaps.SetCreditedAsync(swap.SwapId, paidAt);
        await ForgetAsync(swap);
        logger.LogInformation("Satora Lightning swap {SwapId} of store {StoreId} credited ({Outcome})", swap.SwapId, swap.StoreId, outcome);
    }

    // The listener stops waiting for an invoice once it is reported expired.
    private async Task CompleteAsync(SatoraLightningSwap swap, DateTimeOffset now)
    {
        await swaps.SetCompletedAsync(swap.SwapId, now);
        await ForgetAsync(swap);
        swap.CompletedAt = now;
        invoiceHub.Publish(swap.StoreId, SatoraLightningActions.ToInvoice(swap, now));
    }

    // A swap the server does not know cannot be claimed after its cutoff, whatever happened to it.
    private async Task UnknownToServerAsync(SatoraLightningSwap swap)
    {
        await AlertOnce(swap.StoreId, swap, null, Problems.UnknownToServer);
        var now = DateTimeOffset.UtcNow;
        if (now >= swap.ClaimCutoff)
            await CompleteAsync(swap, now);
    }

    private async Task ForgetAsync(SatoraLightningSwap swap)
    {
        _claimFailures.TryRemove(swap.SwapId, out _);
        _lateClaimFailureLogged.TryRemove(swap.SwapId, out _);
        await _alerts.ForgetSwapAsync(swap.StoreId, swap.SwapId);
    }

    // swap is null for a problem with the store's swaps as a whole.
    private async Task AlertOnce(string storeId, SatoraLightningSwap? swap, DateTimeOffset? cutoff, Problems problem, SatoraSwapStatus? status = null)
    {
        if (await _alerts.TryStartAsync(storeId, swap?.SwapId, problem, status))
            await Notify(storeId, swap, cutoff, problem, status);
    }

    // Names the invoice, which the store looks a payment up by, as well as the swap.
    private async Task Notify(string storeId, SatoraLightningSwap? swap, DateTimeOffset? cutoff, Problems problem, SatoraSwapStatus? status) =>
        await SendAsync(new SatoraLightningNotification
        {
            StoreId = storeId,
            SwapId = swap?.SwapId,
            InvoiceId = swap is null ? null : await FindInvoiceIdAsync(swap),
            Problem = problem,
            Status = status?.ToString(),
            ClaimCutoff = cutoff
        });

    // An alert goes out whether or not its invoice can be found: one recorded as sent and then lost
    // would never go out.
    private async Task<string?> FindInvoiceIdAsync(SatoraLightningSwap swap)
    {
        try
        {
            return (await credit.FindInvoiceIdsAsync([swap.PaymentHash])).GetValueOrDefault(swap.PaymentHash);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not find the invoice of Satora Lightning swap {SwapId} of store {StoreId}", swap.SwapId, swap.StoreId);
            return null;
        }
    }

    protected virtual async Task SendAsync(SatoraLightningNotification notification)
    {
        try
        {
            await notifier.SendAsync(notification.StoreId, notification);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not send the Satora Lightning notification for swap {SwapId} of store {StoreId}", notification.SwapId, notification.StoreId);
        }
    }
}
