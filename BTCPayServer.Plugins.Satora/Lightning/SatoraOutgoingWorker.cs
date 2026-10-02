using System.Collections.Concurrent;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using Problems = BTCPayServer.Plugins.Satora.Lightning.SatoraLightningNotification.Problems;
using SdkArkadeSpend = uniffi.satora_sdk_ffi.ArkadeSpend;

namespace BTCPayServer.Plugins.Satora.Lightning;

// The only place outgoing Lightning swaps are resolved after their first funding attempt: funded
// again from the SDK's journal, recorded as paid, or refunded. Each store runs on its own task, apart
// from SatoraLightningWorker, so a slow funding or refund never holds up a store's claims. Once the
// worker is stopping no funding or refund starts, and a failure, such as a call on a client BTCPay
// has disposed, counts against no swap.
public class SatoraOutgoingWorker(
    SatoraOutgoingSwaps swaps,
    SatoraWalletAccess wallets,
    SatoraWithdrawals withdrawals,
    SatoraNotifier notifier,
    SatoraSentAlerts sentAlerts,
    ILogger<SatoraOutgoingWorker> logger) : BackgroundService
{
    // A paid swap only needs recording, and BTCPay's payout listener reads it every 10 minutes.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private const int FailuresBeforeAlert = 2;

    private enum FundingOutcome
    {
        Funded,
        // The SDK cannot tell yet whether an earlier attempt paid the VHTLC.
        Unresolved,
        // Closed instead: the worker no longer follows the swap.
        Closed,
        Failed
    }

    private readonly SatoraStoreTicks _ticks = new();
    private readonly ConcurrentDictionary<string, int> _refundFailures = new();
    private readonly ConcurrentDictionary<string, int> _fundingFailures = new();
    private readonly SatoraOnceAlerts _alerts = new(sentAlerts, "outgoing", logger);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _statusFailingSince = new();

    protected virtual DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                foreach (var storeId in await swaps.ListOpenStoresAsync(stoppingToken))
                    Schedule(storeId, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Could not list the open outgoing Satora Lightning swaps");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await _ticks.WaitAsync(cancellationToken);
    }

    private void Schedule(string storeId, CancellationToken stoppingToken) =>
        _ticks.TryStart(storeId, async () =>
        {
            try
            {
                await TickAsync(storeId, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(e, "Outgoing Satora Lightning swaps of store {StoreId} could not be processed", storeId);
            }
        });

    internal async Task TickAsync(string storeId, CancellationToken stoppingToken)
    {
        var open = await swaps.ListOpenAsync(storeId, stoppingToken);
        if (open.Count == 0)
            return;
        BulkStatus bulk;
        try
        {
            var wallet = await wallets.GetAsync(storeId);
            bulk = await wallet.GetBulkStatusAsync(open.Select(s => s.SwapId).ToList()).WaitAsync(StatusTimeout, stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            await StatusReadFailedAsync(storeId, open, e, stoppingToken);
            return;
        }
        _statusFailingSince.TryRemove(storeId, out _);
        await _alerts.ForgetAsync(storeId, Problems.OutgoingStatusUnavailable);
        var statuses = bulk.Statuses.ToDictionary(s => s.Id, s => SatoraSwapStatuses.From(s.Status));
        var notFound = bulk.NotFound.ToHashSet();
        foreach (var swap in open)
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
                    await UnknownToServerAsync(swap, stoppingToken);
                }
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Outgoing Satora Lightning swap {SwapId} of store {StoreId} could not be processed; retrying", swap.SwapId, storeId);
            }
        }
    }

    // Without statuses the worker can tell no payment paid or failed. Once reads have failed as long
    // as the incoming worker waits before alerting, the store hears about it while a payment may hold
    // the wallet's sats, and each one past its refund locktime is refunded without the server.
    private async Task StatusReadFailedAsync(string storeId, List<SatoraLightningSwap> open, Exception error, CancellationToken stoppingToken)
    {
        var now = UtcNow;
        if (_statusFailingSince.TryAdd(storeId, now))
            logger.LogWalletFailure(error, "Could not read the outgoing Satora Lightning swaps of store {StoreId}; retrying", storeId);
        var attempted = open.Where(s => s.FundingAttempted).ToList();
        if (attempted.Count == 0 || now - _statusFailingSince.GetValueOrDefault(storeId, now) < SatoraLightningWorker.StatusFailureAlertAfter)
            return;
        await AlertStoreOnce(storeId, Problems.OutgoingStatusUnavailable, attempted.Min(s => s.ClaimCutoff));
        foreach (var swap in attempted.Where(s => now >= s.ClaimCutoff))
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            try
            {
                await RefundWithoutServerAsync(swap, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Outgoing Satora Lightning swap {SwapId} of store {StoreId} could not be processed; retrying", swap.SwapId, storeId);
            }
        }
    }

    // Nothing funds a swap the server does not know whose funding was never attempted. An attempted
    // one waits for its refund locktime, from which the wallet can take the VHTLC back alone.
    private async Task UnknownToServerAsync(SatoraLightningSwap swap, CancellationToken stoppingToken)
    {
        if (!swap.FundingAttempted)
        {
            // Recorded expired, so nothing reads a swap the server forgot as still awaiting funding.
            await CloseUnattemptedAsync(swap, SatoraSwapStatus.Expired);
            return;
        }
        await AlertOnce(swap, Problems.UnknownToServer);
        if (UtcNow >= swap.ClaimCutoff)
            await RefundWithoutServerAsync(swap, stoppingToken);
    }

    private async Task ActAsync(SatoraLightningSwap swap, SatoraSwapStatus status, CancellationToken stoppingToken)
    {
        var action = SatoraOutgoingActions.Decide(swap, status);
        if (action == SatoraOutgoingAction.Alert)
        {
            logger.LogError("Outgoing Satora Lightning swap {SwapId} of store {StoreId} reached an unexpected status, {Status}",
                swap.SwapId, swap.StoreId, status);
            await AlertOnce(swap, Problems.UnexpectedStatus, status);
        }
        if (SatoraOutgoingActions.RefundsWithoutServer(swap, action, UtcNow))
        {
            await RefundWithoutServerAsync(swap, stoppingToken);
            return;
        }
        switch (action)
        {
            case SatoraOutgoingAction.Fund:
                await FundAsync(swap, stoppingToken);
                break;
            case SatoraOutgoingAction.Paid:
                if (await withdrawals.ClosePaidSwapAsync(swap.SwapId, swap.FundingTxId, UtcNow))
                    logger.LogInformation("Outgoing Satora Lightning swap {SwapId} of store {StoreId} paid its invoice", swap.SwapId, swap.StoreId);
                else
                    logger.LogWarning("Outgoing Satora Lightning swap {SwapId} of store {StoreId} paid its invoice after the store closed it; the store's close stands", swap.SwapId, swap.StoreId);
                await ForgetAsync(swap);
                break;
            case SatoraOutgoingAction.Refund:
                await RefundAsync(swap, stoppingToken);
                break;
            case SatoraOutgoingAction.Close:
                await CloseUnattemptedAsync(swap, status);
                break;
        }
    }

    // FundArkadeSwapAsync pays only when its journal proves no earlier attempt did, and otherwise
    // returns that attempt's txid once visible. Some of its refusals never change, such as for a VHTLC
    // locked to a key arkd no longer signs with, and a swap in a refund state refused before its
    // journal is read closes only once its VHTLC holds sats to refund, so a funding that keeps failing
    // is told to the store, and told again once the server gives the payment up. It is not closed or
    // failed for that: a VHTLC someone else funded is refused too, and the server may still pay its
    // invoice.
    private async Task<FundingOutcome> FundAsync(SatoraLightningSwap swap, CancellationToken stoppingToken)
    {
        try
        {
            var txid = await wallets.SpendAsync<string?>(swap.StoreId, async wallet =>
            {
                stoppingToken.ThrowIfCancellationRequested();
                // The refund locktime may have passed while this waited, and from then on the wallet
                // refunds the swap without the server instead.
                if (SatoraOutgoingActions.RefundsWithoutServer(swap, SatoraOutgoingActions.Decide(swap, swap.Status), UtcNow))
                    return null;
                // The store closes a payment under the same lock: one it closed while this waited is
                // not funded.
                if (!await swaps.SetFundingAttemptedAsync(swap.SwapId))
                    throw new SatoraSwapClosedException(swap.SwapId);
                return await wallet.FundArkadeSwapAsync(swap.SwapId);
            }, stoppingToken);
            if (txid is null)
            {
                logger.LogInformation("Outgoing Satora Lightning swap {SwapId} of store {StoreId} reached its refund locktime before it was funded again", swap.SwapId, swap.StoreId);
                return FundingOutcome.Failed;
            }
            logger.LogInformation("Funded outgoing Satora Lightning swap {SwapId} of store {StoreId} in {TxId}", swap.SwapId, swap.StoreId, txid);
            await swaps.SetFundedAsync(swap.SwapId, txid);
            swap.FundingTxId = txid;
            _fundingFailures.TryRemove(swap.SwapId, out _);
            return FundingOutcome.Funded;
        }
        catch (SatoraSwapClosedException)
        {
            logger.LogInformation("Outgoing Satora Lightning swap {SwapId} of store {StoreId} was closed before it was funded again", swap.SwapId, swap.StoreId);
            await ForgetAsync(swap);
            return FundingOutcome.Closed;
        }
        catch (Exception e) when (SatoraOutgoingActions.IsUnresolvedFunding(e))
        {
            logger.LogWarning(e, "The funding of outgoing Satora Lightning swap {SwapId} of store {StoreId} is unresolved; waiting", swap.SwapId, swap.StoreId);
            await AlertOnce(swap, Problems.FundingUnresolved);
            return FundingOutcome.Unresolved;
        }
        catch (Exception e) when (SatoraOutgoingActions.ProvesNothingFunded(e) && SatoraOutgoingActions.IsRefundState(swap.Status))
        {
            logger.LogInformation("Outgoing Satora Lightning swap {SwapId} of store {StoreId} ended unfunded", swap.SwapId, swap.StoreId);
            await CloseAsync(swap);
            return FundingOutcome.Closed;
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Funding outgoing Satora Lightning swap {SwapId} of store {StoreId} failed; retrying", swap.SwapId, swap.StoreId);
            if (!SatoraOutgoingActions.ProvesNothingFunded(e) &&
                _fundingFailures.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1) >= FailuresBeforeAlert)
                await AlertOnce(swap, Problems.FundingFailing, swap.Status, error: e.Message);
            return FundingOutcome.Failed;
        }
    }

    // A funding whose outcome was never read is resolved first: refunding an empty VHTLC only fails.
    // Some fundings the SDK never resolves on its own, and some it refuses on every attempt, so one
    // neither funded nor closed is refunded once the VHTLC holds sats. That cannot pay twice: a refund
    // returns the VHTLC to the wallet only, and the closed swap is never funded again.
    private async Task RefundAsync(SatoraLightningSwap swap, CancellationToken stoppingToken)
    {
        // Refunded without the server earlier; the server now confirms a refund state.
        if (swap.RefundTxId is { } refunded)
        {
            await RefundedAsync(swap, refunded);
            return;
        }
        if (swap.FundingTxId is null)
        {
            var funding = await FundAsync(swap, stoppingToken);
            if (funding == FundingOutcome.Closed || funding != FundingOutcome.Funded && await HeldAsync(swap, stoppingToken) is null or 0)
                return;
        }
        foreach (var operation in SatoraOutgoingActions.RefundOperations(swap, UtcNow))
        {
            string txid;
            try
            {
                txid = await wallets.UseAsync(swap.StoreId, wallet =>
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    return wallet.RefundAsync(swap.SwapId, operation);
                }, stoppingToken);
            }
            catch (Exception e) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "Refunding outgoing Satora Lightning swap {SwapId} of store {StoreId} ({Operation}) failed", swap.SwapId, swap.StoreId, operation);
                continue;
            }
            await RefundedAsync(swap, txid);
            return;
        }
        await RefundFailedAsync(swap, Problems.RefundFailing, stoppingToken);
    }

    // Counted for each problem apart, so failures of the cooperative refund do not count towards the
    // alerts of the refund without the server. Those say what the VHTLC holds, which tells the store
    // whether closing the payment by hand would be right.
    private async Task RefundFailedAsync(SatoraLightningSwap swap, Problems problem, CancellationToken stoppingToken)
    {
        if (_refundFailures.AddOrUpdate($"{swap.SwapId}/{problem}", 1, (_, count) => count + 1) < FailuresBeforeAlert ||
            _alerts.KnownSent(swap.StoreId, swap.SwapId, problem))
            return;
        var held = problem == Problems.RefundFailing ? null : await HeldAsync(swap, stoppingToken);
        await AlertOnce(swap, problem, heldSats: held);
    }

    // A refund that lands after the store closed the swap keeps the store's close, and only its txid
    // is recorded.
    private async Task RefundedAsync(SatoraLightningSwap swap, string txid)
    {
        var now = UtcNow;
        if (await withdrawals.CloseRefundedSwapAsync(swap.SwapId, txid,
                $"The Lightning payment failed ({swap.Status}); its funds were refunded to the wallet.", now))
        {
            logger.LogInformation("Refunded outgoing Satora Lightning swap {SwapId} of store {StoreId}", swap.SwapId, swap.StoreId);
        }
        else
        {
            await swaps.SetRefundTxIdAsync(swap.SwapId, txid);
            logger.LogWarning("Refunded outgoing Satora Lightning swap {SwapId} of store {StoreId} after the store closed it; the store's close stands", swap.SwapId, swap.StoreId);
        }
        swap.RefundTxId = txid;
        swap.CompletedAt = now;
        await ForgetAsync(swap);
    }

    // The server is gone or no longer knows the swap, and its refund locktime has passed: the wallet
    // takes the VHTLC back alone. The SDK refuses until median time past passes the locktime, which
    // takes as long as the chain worker allows for it before that refusal counts. Other failures, as
    // for a VHTLC never funded or one the server claimed, count at once. The server may still have
    // paid the invoice, so the payment stays pending and the store reconciles it by hand.
    private async Task RefundWithoutServerAsync(SatoraLightningSwap swap, CancellationToken stoppingToken)
    {
        if (swap.RefundTxId is not null)
            return;
        string txid;
        try
        {
            txid = await wallets.UseAsync(swap.StoreId, wallet =>
            {
                stoppingToken.ThrowIfCancellationRequested();
                return wallet.RefundAsync(swap.SwapId, SdkArkadeSpend.Refund);
            }, stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested && SatoraOutgoingActions.IsRefundLocktimeNotMature(e))
        {
            if (UtcNow < swap.ClaimCutoff + SatoraOutgoingActions.MedianTimeLag)
            {
                logger.LogDebug(e, "Outgoing Satora Lightning swap {SwapId} of store {StoreId} cannot be refunded without the swap server yet; retrying", swap.SwapId, swap.StoreId);
                return;
            }
            logger.LogWarning(e, "Median time past has still not passed the refund locktime of outgoing Satora Lightning swap {SwapId} of store {StoreId}; retrying", swap.SwapId, swap.StoreId);
            await RefundFailedAsync(swap, Problems.RefundLocktimeNotMature, stoppingToken);
            return;
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Refunding outgoing Satora Lightning swap {SwapId} of store {StoreId} without the swap server failed; retrying", swap.SwapId, swap.StoreId);
            await RefundFailedAsync(swap, Problems.RefundWithoutServerFailing, stoppingToken);
            return;
        }
        await swaps.SetRefundTxIdAsync(swap.SwapId, txid);
        swap.RefundTxId = txid;
        logger.LogWarning("Refunded outgoing Satora Lightning swap {SwapId} of store {StoreId} without the swap server; its payment stays pending", swap.SwapId, swap.StoreId);
        await AlertOnce(swap, Problems.RefundedWithoutServer);
    }

    // Spendable and recoverable sats at the swap's VHTLC, read without the swap server; null when
    // they cannot be read.
    private async Task<ulong?> HeldAsync(SatoraLightningSwap swap, CancellationToken stoppingToken)
    {
        try
        {
            var wallet = await wallets.GetAsync(swap.StoreId);
            var amounts = await wallet.AmountsForSwapAsync(swap.SwapId).WaitAsync(StatusTimeout, stoppingToken);
            return amounts.SpendableSats + amounts.RecoverableSats;
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not read what the VHTLC of outgoing Satora Lightning swap {SwapId} of store {StoreId} holds", swap.SwapId, swap.StoreId);
            return null;
        }
    }

    // Only while no payment has marked the swap attempted since it was read; otherwise the next tick
    // reads it again.
    private async Task CloseUnattemptedAsync(SatoraLightningSwap swap, SatoraSwapStatus status)
    {
        if (await withdrawals.CloseUnattemptedSwapAsync(swap.SwapId, status,
                $"The Lightning payment did not go through ({status}); nothing left the wallet.", UtcNow))
            await ForgetAsync(swap);
    }

    private async Task CloseAsync(SatoraLightningSwap swap)
    {
        var now = UtcNow;
        await withdrawals.CloseUnfundedSwapAsync(swap.SwapId,
            $"The Lightning payment did not go through ({swap.Status}); nothing left the wallet.", now);
        swap.CompletedAt = now;
        await ForgetAsync(swap);
    }

    private async Task ForgetAsync(SatoraLightningSwap swap)
    {
        _fundingFailures.TryRemove(swap.SwapId, out _);
        var prefix = swap.SwapId + "/";
        foreach (var key in _refundFailures.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
            _refundFailures.TryRemove(key, out _);
        await _alerts.ForgetSwapAsync(swap.StoreId, swap.SwapId);
    }

    private async Task AlertStoreOnce(string storeId, Problems problem, DateTimeOffset claimCutoff)
    {
        if (!await _alerts.TryStartAsync(storeId, null, problem))
            return;
        await SendAsync(new SatoraLightningNotification { StoreId = storeId, Problem = problem, ClaimCutoff = claimCutoff });
    }

    private async Task AlertOnce(SatoraLightningSwap swap, Problems problem, SatoraSwapStatus? status = null, ulong? heldSats = null,
        string? error = null)
    {
        if (!await _alerts.TryStartAsync(swap.StoreId, swap.SwapId, problem, status))
            return;
        await SendAsync(new SatoraLightningNotification
        {
            StoreId = swap.StoreId,
            SwapId = swap.SwapId,
            Problem = problem,
            Status = status?.ToString(),
            ClaimCutoff = problem is Problems.RefundFailing or Problems.RefundLocktimeNotMature or Problems.FundingFailing ? swap.ClaimCutoff : null,
            HeldSats = (long?)heldSats,
            Error = error
        });
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
