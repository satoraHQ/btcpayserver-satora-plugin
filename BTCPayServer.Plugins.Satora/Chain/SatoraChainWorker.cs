using System.Collections.Concurrent;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using Problems = BTCPayServer.Plugins.Satora.Chain.SatoraChainNotification.Problems;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;

namespace BTCPayServer.Plugins.Satora.Chain;

// The only place incoming on-chain swaps are recorded, claimed, closed and refunded. Each store runs on
// its own task, one tick at a time, apart from the Lightning workers, so an esplora or refund call
// never holds up a Lightning claim. Refunds go out only to an address the owner entered. Once the
// worker is stopping no claim or refund starts, and a failure, such as a call on a client BTCPay has
// disposed, counts against no swap.
public class SatoraChainWorker(
    SatoraChainSwaps swaps,
    SatoraChainWallets wallets,
    SatoraChainCredit credit,
    SatoraNotifier notifier,
    SatoraSentAlerts sentAlerts,
    ILogger<SatoraChainWorker> logger) : BackgroundService
{
    // A confirmation takes ten minutes on average, and a funded swap can be claimed for 12 hours.
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StatusFailureAlertAfter = TimeSpan.FromMinutes(30);
    // How often a broadcast refund, or the address of a swap whose refund cannot go out, is read
    // again: about a block, so esplora is not asked every tick.
    public static readonly TimeSpan RefundCheckInterval = TimeSpan.FromMinutes(10);
    // How long an address must hold only outputs worth less than a refund's fee before its swap closes
    // and leaves them, so a fee spike does not cost the customer a refund worth sending once fees fall.
    // Counted in memory: a restart only puts the close off.
    public static readonly TimeSpan UneconomicCloseAfter = TimeSpan.FromHours(24);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private const int FailuresBeforeAlert = 2;
    // Reads in a row without the funding output before its payment comes off: one esplora answer
    // lagging behind another should not flip the invoice.
    private const int MissesBeforeUnaccount = 2;

    private readonly SatoraStoreTicks _ticks = new();
    private readonly ConcurrentDictionary<string, int> _claimFailures = new();
    private readonly ConcurrentDictionary<string, bool> _lateClaimFailureLogged = new();
    private readonly ConcurrentDictionary<string, int> _refundFailures = new();
    private readonly ConcurrentDictionary<string, int> _fundingMisses = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextRefundCheck = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nothingSince = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _uneconomicSince = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _leftoverRecheck = new();
    private readonly SatoraOnceAlerts _alerts = new(sentAlerts, "chain", logger);
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
                logger.LogError(e, "Could not list the open Satora on-chain swaps");
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
                logger.LogError(e, "Satora on-chain swaps of store {StoreId} could not be processed", storeId);
            }
        });

    internal async Task TickAsync(string storeId, CancellationToken stoppingToken)
    {
        var open = await swaps.ListOpenAsync(storeId, stoppingToken);
        // A swap waiting for its refund needs only the owner's address, the HTLC's locktime and its address.
        foreach (var swap in open.Where(s => s.NeedsRefundAt is not null))
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            await Guarded(swap, () => RefundTickAsync(swap, stoppingToken), stoppingToken);
        }
        var active = open.Where(s => s.NeedsRefundAt is null).ToList();
        if (active.Count == 0)
            return;
        BulkStatus bulk;
        try
        {
            bulk = await wallets.GetBulkStatusAsync(storeId, active.Select(s => s.SwapId), stoppingToken).WaitAsync(StatusTimeout, stoppingToken);
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            await StatusReadFailedAsync(storeId, active, e);
            return;
        }
        _statusFailingSince.TryRemove(storeId, out _);
        await _alerts.ForgetAsync(storeId, Problems.StatusUnavailable);

        var statuses = bulk.Statuses.ToDictionary(s => s.Id, s => SatoraSwapStatuses.From(s.Status));
        var notFound = bulk.NotFound.ToHashSet();
        foreach (var swap in active)
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            if (statuses.TryGetValue(swap.SwapId, out var status))
            {
                await Guarded(swap, async () =>
                {
                    if (status != swap.Status)
                    {
                        await swaps.SetStatusAsync(swap.SwapId, status);
                        swap.Status = status;
                    }
                    await ActAsync(swap, status, stoppingToken);
                }, stoppingToken);
            }
            else if (notFound.Contains(swap.SwapId))
            {
                await Guarded(swap, () => UnknownToServerAsync(swap, stoppingToken), stoppingToken);
            }
        }
    }

    private async Task Guarded(SatoraChainSwap swap, Func<Task> action, CancellationToken stoppingToken)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Satora on-chain swap {SwapId} of store {StoreId} could not be processed; retrying", swap.SwapId, swap.StoreId);
        }
    }

    private async Task StatusReadFailedAsync(string storeId, List<SatoraChainSwap> active, Exception error)
    {
        var now = UtcNow;
        if (_statusFailingSince.TryAdd(storeId, now))
            logger.LogWalletFailure(error, "Could not read the Satora on-chain swaps of store {StoreId}; retrying", storeId);
        if (now - _statusFailingSince.GetValueOrDefault(storeId, now) < StatusFailureAlertAfter)
            return;
        var cutoff = active.Where(s => s.ClaimCutoff > now).Select(s => (DateTimeOffset?)s.ClaimCutoff).Min();
        if (cutoff is not null)
            await AlertStoreOnce(storeId, Problems.StatusUnavailable, cutoff);
    }

    private async Task ActAsync(SatoraChainSwap swap, SatoraSwapStatus status, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        switch (SatoraChainActions.Decide(swap, status, now))
        {
            case SatoraChainAction.Wait:
                await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraChainAction.CheckFunding:
                await CheckFundingAsync(swap, now, cancellationToken);
                break;
            case SatoraChainAction.Claim:
                // A funding the worker never recorded (it was down, or the server was quicker) goes on
                // the invoice first, as a claim can take a while or keep failing. Once the server has
                // funded the VHTLC, a recorded funding has confirmed and stays at the address until the
                // claim reveals the preimage, so it is read again for the excess the claim takes, in case
                // a replacement was mined before the worker saw it. Not at a later status, when the
                // server may have swept it and left only another payment there.
                if (!swap.PaymentRecorded)
                    await TryCheckFundingAsync(swap, now, cancellationToken);
                else if (status == SatoraSwapStatus.ServerFunded)
                    await TryReadOverpaidAsync(swap, cancellationToken);
                if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else
                    await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraChainAction.FinishClaim:
                // Past the cutoff the SDK starts no new claim, but finishes one it journaled, also after a
                // transport error or timeout. Without one, the customer's bitcoin goes back from the
                // VHTLC locktime.
                if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else
                    await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraChainAction.FinishClaimOrRefund:
                // A claim that lands and a refund of the customer's bitcoin must never both go out, so the
                // swap goes back only once the SDK says no claim can complete, which is final. A query
                // that fails moves nothing.
                if (!await wallets.ClaimPossibleAsync(swap.StoreId, swap.SwapId))
                    await NeedsRefundAsync(swap, SatoraChainActions.UnclaimedReason, Problems.NeedsRefund, now);
                else if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else
                    await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraChainAction.Credit:
                await CreditAsync(swap);
                break;
            case SatoraChainAction.ClaimUnverified:
                // Past the cutoff the SDK starts no new claim, but still returns the receipt of one it journaled.
                if (await ClaimAsync(swap, cancellationToken))
                {
                    await CreditAsync(swap);
                    break;
                }
                await AlertOnce(swap, Problems.ClaimUnverified, swap.ClaimCutoff, status);
                // Only once the server has swept the customer's output can what is left be told apart. The
                // SDK reaches arkd before it reads its journal, so a failed attempt says nothing about a
                // claim it may still finish: only its word that none can complete, which is final, closes
                // the swap. A query that fails moves nothing.
                if (status != SatoraSwapStatus.ServerRedeemed || await wallets.ClaimPossibleAsync(swap.StoreId, swap.SwapId))
                    break;
                // Already logged on the tick that found outputs still at the swept address.
                if (!_leftoverRecheck.ContainsKey(swap.SwapId))
                    logger.LogError("Satora on-chain swap {SwapId} of store {StoreId} is {Status} on the swap server, but the wallet has no receipt for claiming it and no claim can complete; it was not credited",
                        swap.SwapId, swap.StoreId, status);
                await CloseAsync(swap, now, cancellationToken, swept: true);
                break;
            case SatoraChainAction.Close:
                await CloseAsync(swap, now, cancellationToken,
                    expired: status == SatoraSwapStatus.Expired, swept: status == SatoraSwapStatus.ServerRedeemed);
                break;
            case SatoraChainAction.NeedsRefund:
                await NeedsRefundAsync(swap, status.ToString(), Problems.NeedsRefund, now);
                break;
            case SatoraChainAction.FundingTooLate:
                // A funding that never confirmed may have left the mempool since; then nothing goes back.
                if ((await wallets.ListUnspentAsync(swap.HtlcAddress, cancellationToken)).Count > 0)
                    await NeedsRefundAsync(swap, status.ToString(), Problems.NeedsRefund, now);
                else if (NothingForAWhile(swap.SwapId, now))
                {
                    logger.LogInformation("The funding of Satora on-chain swap {SwapId} of store {StoreId} never confirmed and is gone; closing it", swap.SwapId, swap.StoreId);
                    await CompleteAsync(swap, now);
                }
                break;
            case SatoraChainAction.Alert:
                logger.LogError("Satora on-chain swap {SwapId} of store {StoreId} reached an unexpected status, {Status}; nothing was moved",
                    swap.SwapId, swap.StoreId, status);
                await AlertOnce(swap, Problems.UnexpectedStatus, null, status, perStatus: true);
                break;
        }
    }

    // Only an output paying the whole deposit counts: the server marks an underpayment it has seen in
    // the mempool ClientFundingSeen too, and its status says nothing of the amount. Until it confirms,
    // the output can be replaced or dropped, and its payment then comes off the invoice, as
    // NBXplorerListener takes off a replaced transaction.
    private async Task CheckFundingAsync(SatoraChainSwap swap, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var funding = SatoraChainActions.Funding(await wallets.ListUnspentAsync(swap.HtlcAddress, cancellationToken), swap.DepositSats);
        if (funding is null)
        {
            if (!swap.PaymentRecorded || _fundingMisses.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1) < MissesBeforeUnaccount)
                return;
            await credit.UnaccountAsync(swap);
            // Cleared first, so a later funding that cannot be read before the claim is not alerted
            // with this output's excess.
            await SetOverpaidAsync(swap, 0);
            await swaps.SetPaymentRecordedAsync(swap.SwapId, false);
            swap.PaymentRecorded = false;
            _fundingMisses.TryRemove(swap.SwapId, out _);
            logger.LogWarning("The funding of Satora on-chain swap {SwapId} of store {StoreId} is no longer at its address; its payment came off the invoice",
                swap.SwapId, swap.StoreId);
            return;
        }
        _fundingMisses.TryRemove(swap.SwapId, out _);
        // Saved before the payment, so a restart in between cannot lose it.
        await SetOverpaidAsync(swap, funding.ValueSats - swap.DepositSats);
        if (swap.PaymentRecorded)
            return;
        var outcome = await credit.RecordAsync(swap, PaymentStatus.Processing, now);
        await swaps.SetPaymentRecordedAsync(swap.SwapId, true);
        swap.PaymentRecorded = true;
        logger.LogInformation("Satora on-chain swap {SwapId} of store {StoreId} funded by {TxId}:{Vout} ({Outcome})",
            swap.SwapId, swap.StoreId, funding.TxId, funding.Vout, outcome);
    }

    private async Task TryCheckFundingAsync(SatoraChainSwap swap, DateTimeOffset now, CancellationToken cancellationToken)
    {
        try
        {
            await CheckFundingAsync(swap, now, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not read the funding of Satora on-chain swap {SwapId} of store {StoreId}; claiming anyway", swap.SwapId, swap.StoreId);
        }
    }

    private async Task TryReadOverpaidAsync(SatoraChainSwap swap, CancellationToken cancellationToken)
    {
        try
        {
            var outputs = await wallets.ListUnspentAsync(swap.HtlcAddress, cancellationToken);
            if (SatoraChainActions.Funding(outputs.Where(o => o.Confirmed), swap.DepositSats) is { } funding)
                await SetOverpaidAsync(swap, funding.ValueSats - swap.DepositSats);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Could not read the funding of Satora on-chain swap {SwapId} of store {StoreId}; claiming anyway", swap.SwapId, swap.StoreId);
        }
    }

    private async Task SetOverpaidAsync(SatoraChainSwap swap, long overpaidSats)
    {
        if (overpaidSats != swap.OverpaidSats && await swaps.SetOverpaidAsync(swap.SwapId, overpaidSats))
            swap.OverpaidSats = overpaidSats;
    }

    // The SDK journals the claim before submitting it, so calling again after any error resumes it
    // rather than claiming twice, and a claim that already went through returns its receipt.
    private async Task<bool> ClaimAsync(SatoraChainSwap swap, CancellationToken cancellationToken)
    {
        try
        {
            var destination = await wallets.GetClaimAddressAsync(swap.StoreId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var txid = await wallets.ClaimAsync(swap.StoreId, swap.SwapId, destination, cancellationToken) switch
            {
                SdkArkadeSettlement.Offchain offchain => offchain.arkTxid,
                SdkArkadeSettlement.Delegated delegated => delegated.commitmentTxid,
                var other => throw new InvalidOperationException($"The SDK settled the claim with an unexpected outcome, {other}.")
            };
            await swaps.SetClaimedAsync(swap.SwapId, txid);
            swap.ClaimTxId = txid;
            _claimFailures.TryRemove(swap.SwapId, out _);
            logger.LogInformation("Claimed Satora on-chain swap {SwapId} of store {StoreId}", swap.SwapId, swap.StoreId);
            return true;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            var failures = _claimFailures.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1);
            var claimable = UtcNow < swap.ClaimCutoff;
            // Past the cutoff the claim is retried every tick while the SDK may still finish it; one warning is enough.
            var level = claimable || _lateClaimFailureLogged.TryAdd(swap.SwapId, true) ? LogLevel.Warning : LogLevel.Debug;
            logger.Log(level, e, "Claiming Satora on-chain swap {SwapId} of store {StoreId} failed ({Failures} so far)", swap.SwapId, swap.StoreId, failures);
            if (failures == FailuresBeforeAlert && claimable)
                await Notify(swap, Problems.ClaimFailing, swap.ClaimCutoff, null);
            return false;
        }
    }

    // Each alert here has the store settle bitcoin with the customer, so none goes out before the claim:
    // until then the funding can be replaced, or the swap refunded with all its output paid. The swap is
    // marked credited right after and never decided again, so an alert that cannot be recorded fails the
    // credit, which the next tick repeats.
    private async Task CreditAsync(SatoraChainSwap swap)
    {
        var outcome = await credit.RecordAsync(swap, PaymentStatus.Settled, UtcNow);
        if (swap.OverpaidSats > 0)
            await OverpaidAsync(swap);
        if (await credit.SettledAfterPaidInFullAsync(swap))
            await PaidAfterPaidInFullAsync(swap);
        await swaps.SetCreditedAsync(swap.SwapId);
        swap.Credited = swap.PaymentRecorded = true;
        logger.LogInformation("Satora on-chain swap {SwapId} of store {StoreId} credited ({Outcome})", swap.SwapId, swap.StoreId, outcome);
    }

    // The server funds the VHTLC with the amount due whatever the output pays, and keeps the rest.
    private async Task OverpaidAsync(SatoraChainSwap swap)
    {
        if (!await _alerts.StartAsync(swap.StoreId, swap.SwapId, Problems.Overpaid))
            return;
        logger.LogWarning("Satora on-chain swap {SwapId} of store {StoreId} was paid {ExcessSats} sats more than its deposit; the swap server keeps them",
            swap.SwapId, swap.StoreId, swap.OverpaidSats);
        await credit.NoteAsync(swap,
            $"The customer paid {swap.OverpaidSats} sats more than Satora on-chain swap {swap.SwapId} asked for at {swap.HtlcAddress}. Only the amount due reached the Satora wallet, and the swap server kept the rest. Settle the difference with the customer.",
            InvoiceEventData.EventSeverity.Warning);
        await Notify(swap, Problems.Overpaid, null, null, swap.OverpaidSats);
    }

    // Bitcoin claimed for an invoice its other payments had paid in full, as at the address of a swap a
    // renewal replaced, is credited, and BTCPay does not mark a settled invoice paid over, so the store
    // hears of it, once, to settle it with the customer.
    private async Task PaidAfterPaidInFullAsync(SatoraChainSwap swap)
    {
        if (!await _alerts.StartAsync(swap.StoreId, swap.SwapId, Problems.PaidAfterPaidInFull))
            return;
        logger.LogWarning("Satora on-chain swap {SwapId} of store {StoreId} was claimed after its invoice {InvoiceId} was paid in full; it is credited, and {ReceiveSats} sats overpay the invoice",
            swap.SwapId, swap.StoreId, swap.InvoiceId, swap.ReceiveSats);
        await credit.NoteAsync(swap,
            $"Satora on-chain swap {swap.SwapId}, paid at {swap.HtlcAddress}, was claimed after the invoice's other payments had paid it in full. Its {swap.ReceiveSats} sats are credited and in the Satora wallet, so the invoice is overpaid by that amount. Settle it with the customer.",
            InvoiceEventData.EventSeverity.Warning);
        await Notify(swap, Problems.PaidAfterPaidInFull, null, null, swap.ReceiveSats);
    }

    // A swap ends only with an empty HTLC address: anything still there is the customer's to get back.
    // The swap server reports ServerRedeemed once it has broadcast its sweep, which this esplora may not
    // list as spent yet, so an output that could be the swept funding (one paying the deposit) is read
    // again RefundCheckInterval later, and only that read decides. An expired one is read until a
    // payment made by the invoice's expiry would have shown, and its address meanwhile comes off the
    // invoice's prompt.
    private async Task CloseAsync(SatoraChainSwap swap, DateTimeOffset now, CancellationToken cancellationToken,
        bool expired = false, bool swept = false)
    {
        if (_leftoverRecheck.TryGetValue(swap.SwapId, out var recheckAt) && now < recheckAt)
            return;
        var outputs = await wallets.ListUnspentAsync(swap.HtlcAddress, cancellationToken);
        if (outputs.Count > 0)
        {
            if (swept && SatoraChainActions.Funding(outputs, swap.DepositSats) is not null &&
                _leftoverRecheck.TryAdd(swap.SwapId, now + RefundCheckInterval))
                return;
            await NeedsRefundAsync(swap, SatoraChainActions.LeftoverReason, Problems.LeftoverOutputs, now);
            return;
        }
        if (expired && SatoraChainActions.LatePaymentPossible(swap, now))
        {
            await credit.DeactivatePromptAsync(swap.InvoiceId, swap.SwapId);
            return;
        }
        await CompleteAsync(swap, now);
    }

    private async Task CompleteAsync(SatoraChainSwap swap, DateTimeOffset now)
    {
        if (swap.PaymentRecorded && !swap.Credited)
            await credit.UnaccountAsync(swap);
        await swaps.SetCompletedAsync(swap.SwapId, now);
        await ForgetAsync(swap);
    }

    // A payment the swap will not settle comes off the invoice first, and the swap's address off the
    // invoice's prompt, so a failure in between leaves the row to try all again.
    private async Task NeedsRefundAsync(SatoraChainSwap swap, string reason, Problems problem, DateTimeOffset now)
    {
        if (swap.PaymentRecorded && !swap.Credited)
            await credit.UnaccountAsync(swap);
        await credit.DeactivatePromptAsync(swap.InvoiceId, swap.SwapId);
        if (!await swaps.SetNeedsRefundAsync(swap.SwapId, swap.ClaimTxId, reason, now))
        {
            logger.LogWarning("Satora on-chain swap {SwapId} of store {StoreId} changed since it was read; it is decided again on the next tick",
                swap.SwapId, swap.StoreId);
            return;
        }
        swap.NeedsRefundAt = now;
        swap.NeedsRefundReason = reason;
        // The refund path counts empty reads from its own first one.
        _nothingSince.TryRemove(swap.SwapId, out _);
        _leftoverRecheck.TryRemove(swap.SwapId, out _);
        logger.LogWarning("Satora on-chain swap {SwapId} of store {StoreId} needs a refund ({Reason})", swap.SwapId, swap.StoreId, reason);
        // Only once the swap is saved as needing a refund, so a tick that fails before cannot note it twice.
        await credit.NoteAsync(swap,
            $"Satora on-chain swap {swap.SwapId} needs a refund ({SatoraChainRefunds.Describe(reason)}). Refund the bitcoin at {swap.HtlcAddress} to the customer from Satora's On-chain refunds page.",
            InvoiceEventData.EventSeverity.Warning);
        await AlertOnce(swap, problem, swap.BtcRefundLocktime, reason);
    }

    // A refund closes its swap once it confirmed and nothing worth refunding is left at the address. A
    // swap whose address holds nothing worth a refund closes without one, once the refund could go out.
    // A refund that errored after the node took it spends the output in the mempool, which esplora's
    // utxo list leaves out, and only a later attempt returns it from the SDK's journal. So the address
    // is read before this tick's attempt, not after it, and a swap with an attempt behind it stays open
    // while the mempool holds a spend of its address.
    private async Task RefundTickAsync(SatoraChainSwap swap, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        if (swap.RefundTxId is not null)
        {
            if (RefundCheckDue(swap.SwapId, now))
                await FollowRefundAsync(swap, now, cancellationToken);
            return;
        }
        var read = now >= SatoraChainActions.RefundPossibleFrom(swap) && RefundCheckDue(swap.SwapId, now);
        if (read && await CloseIfNothingToRefundAsync(swap, now, cancellationToken))
            return;
        // The estimate weighs each output alone, while the SDK also spends several together whose sum
        // covers their fee, so while the last read found nothing worth a refund the SDK is still asked,
        // once per read.
        if (SatoraChainActions.RefundDue(swap, now) && (read || !_uneconomicSince.ContainsKey(swap.SwapId)))
            await TryRefundAsync(swap, now, cancellationToken);
    }

    private bool RefundCheckDue(string swapId, DateTimeOffset now)
    {
        if (_nextRefundCheck.TryGetValue(swapId, out var next) && now < next)
            return false;
        _nextRefundCheck[swapId] = now + RefundCheckInterval;
        return true;
    }

    private async Task TryRefundAsync(SatoraChainSwap swap, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await RefundHeldAsync(swap))
            return;
        using var hold = await swaps.HoldForRefundAsync(swap.SwapId, cancellationToken);
        // TryStartRefundAsync fixes the refund address, which the owner can change until then.
        cancellationToken.ThrowIfCancellationRequested();
        if (!await swaps.TryStartRefundAsync(swap.SwapId, swap.RefundAddress!))
            return;
        cancellationToken.ThrowIfCancellationRequested();
        BitcoinRefundReceipt receipt;
        try
        {
            receipt = await wallets.RefundAsync(swap.StoreId, swap.SwapId, swap.RefundAddress!, cancellationToken);
        }
        // Expected whenever the SDK agrees with the estimate, so not counted toward the alert.
        catch (Exception e) when (!cancellationToken.IsCancellationRequested && _uneconomicSince.ContainsKey(swap.SwapId))
        {
            logger.LogDebug(e, "Satora on-chain swap {SwapId} of store {StoreId}, whose outputs the last read found worth less than a refund's fee, was not refunded",
                swap.SwapId, swap.StoreId);
            return;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            await RefundFailedAsync(swap, now, e);
            return;
        }
        await RecordRefundAsync(swap, receipt, now);
    }

    // Called again, RefundBitcoinAsync returns a pending refund as it is, rebuilds one that left the
    // mempool, and once it confirmed sweeps outputs it left behind or that arrived since.
    private async Task FollowRefundAsync(SatoraChainSwap swap, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await RefundHeldAsync(swap))
            return;
        BitcoinRefundReceipt receipt;
        using (await swaps.HoldForRefundAsync(swap.SwapId, cancellationToken))
        {
            // The owner may have dismissed the swap since this tick read it.
            if (await swaps.FindAsync(swap.StoreId, swap.SwapId) is not { CompletedAt: null })
            {
                await ForgetAsync(swap);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                receipt = await wallets.RefundAsync(swap.StoreId, swap.SwapId, swap.RefundAddress!, cancellationToken);
            }
            catch (Exception e) when (!cancellationToken.IsCancellationRequested)
            {
                await RefundFailedAsync(swap, now, e);
                return;
            }
            if (receipt.Txid != swap.RefundTxId)
            {
                await RecordRefundAsync(swap, receipt, now);
                return;
            }
        }
        if (!await wallets.IsConfirmedAsync(receipt.Txid, cancellationToken))
            return;
        var leftovers = await wallets.ListUnspentAsync(swap.HtlcAddress, cancellationToken);
        if (leftovers.Count > 0)
        {
            // Against one input's fee, as the receipt's covers every input the refund spent.
            if (!SatoraChainActions.NothingToRefund(leftovers, await wallets.EstimateRefundFeeSatsAsync(cancellationToken)))
            {
                _uneconomicSince.TryRemove(swap.SwapId, out _);
                return;
            }
            // Each follow until then asks the SDK, which sweeps them once fees allow.
            if (now - _uneconomicSince.GetOrAdd(swap.SwapId, now) < UneconomicCloseAfter)
                return;
        }
        var completed = await swaps.SetCompletedAsync(swap.SwapId, now);
        await ForgetAsync(swap);
        if (completed)
            logger.LogInformation("The refund of Satora on-chain swap {SwapId} of store {StoreId} confirmed in {TxId}", swap.SwapId, swap.StoreId, receipt.Txid);
        else
            logger.LogInformation("The refund of Satora on-chain swap {SwapId} of store {StoreId} confirmed in {TxId}, after the store closed the swap",
                swap.SwapId, swap.StoreId, receipt.Txid);
        // A refund broadcast before the store dismissed its swap still confirms, which the store needs on record.
        await credit.NoteAsync(swap, $"The refund of Satora on-chain swap {swap.SwapId} confirmed in {receipt.Txid}. " +
            (!completed ? "The swap had already been dismissed."
                : leftovers.Count > 0 ? "The swap is closed, and its address holds less than the network fee to refund it."
                : "The swap is closed."), InvoiceEventData.EventSeverity.Success);
        if (completed && leftovers.Count > 0)
            await Notify(swap, Problems.NothingToRefund, null, SatoraChainRefunds.LeftBehindStatus);
    }

    private async Task RecordRefundAsync(SatoraChainSwap swap, BitcoinRefundReceipt receipt, DateTimeOffset now)
    {
        await swaps.SetRefundTxIdAsync(swap.SwapId, receipt.Txid);
        swap.RefundTxId = receipt.Txid;
        _refundFailures.TryRemove(swap.SwapId, out _);
        _nothingSince.TryRemove(swap.SwapId, out _);
        _uneconomicSince.TryRemove(swap.SwapId, out _);
        _nextRefundCheck[swap.SwapId] = now + RefundCheckInterval;
        logger.LogInformation("Refunding Satora on-chain swap {SwapId} of store {StoreId} in {TxId}", swap.SwapId, swap.StoreId, receipt.Txid);
        await credit.NoteAsync(swap, $"The refund of Satora on-chain swap {swap.SwapId} to {swap.RefundAddress} was broadcast in {receipt.Txid}.",
            InvoiceEventData.EventSeverity.Info);
    }

    // A swap offered for a refund because no claim could complete is asked about again right before
    // each refund call, any of which may broadcast. The SDK's false is final, so a claim possible again
    // is a fault, but the customer's bitcoin still does not go back while the store's claim may land.
    private async Task<bool> RefundHeldAsync(SatoraChainSwap swap)
    {
        if (swap.NeedsRefundReason != SatoraChainActions.UnclaimedReason || !await wallets.ClaimPossibleAsync(swap.StoreId, swap.SwapId))
            return false;
        if (await _alerts.TryStartAsync(swap.StoreId, swap.SwapId, Problems.RefundHeld))
        {
            logger.LogError("Satora on-chain swap {SwapId} of store {StoreId} was offered for a refund because no claim could complete, but the SDK now says one can; its refund is held",
                swap.SwapId, swap.StoreId);
            await Notify(swap, Problems.RefundHeld, null, null);
        }
        return true;
    }

    // Until median time past passes the locktime every attempt is refused, which is expected, so only
    // failures from when the refund should be possible count. The SDK also refuses an address with
    // nothing worth refunding, which closes on its own, so that is not reported as failing.
    private async Task RefundFailedAsync(SatoraChainSwap swap, DateTimeOffset now, Exception error)
    {
        if (now < SatoraChainActions.RefundPossibleFrom(swap))
        {
            logger.LogDebug(error, "Satora on-chain swap {SwapId} of store {StoreId} cannot be refunded yet", swap.SwapId, swap.StoreId);
            return;
        }
        var failures = _refundFailures.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1);
        logger.LogWarning(error, "Refunding Satora on-chain swap {SwapId} of store {StoreId} failed ({Failures} so far)", swap.SwapId, swap.StoreId, failures);
        if (failures >= FailuresBeforeAlert && !_nothingSince.ContainsKey(swap.SwapId))
            await AlertOnce(swap, Problems.RefundFailing, null);
    }

    // Nothing was broadcast, and the address holds nothing, or for a day nothing a refund would not eat
    // in fees: a dropped underpayment, or dust sent to an old address.
    private async Task<bool> CloseIfNothingToRefundAsync(SatoraChainSwap swap, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var outputs = await wallets.ListUnspentAsync(swap.HtlcAddress, cancellationToken);
        if (outputs.Count > 0 && !SatoraChainActions.NothingToRefund(outputs, await wallets.EstimateRefundFeeSatsAsync(cancellationToken)))
        {
            _nothingSince.TryRemove(swap.SwapId, out _);
            _uneconomicSince.TryRemove(swap.SwapId, out _);
            return false;
        }
        DateTimeOffset? uneconomicSince = null;
        if (outputs.Count > 0)
            uneconomicSince = _uneconomicSince.GetOrAdd(swap.SwapId, now);
        else
            _uneconomicSince.TryRemove(swap.SwapId, out _);
        if (!NothingForAWhile(swap.SwapId, now))
            return false;
        if (swap.RefundAttempted && await wallets.HasPossibleRefundAsync(swap.HtlcAddress, swap.RefundAddress, cancellationToken))
        {
            _nothingSince.TryRemove(swap.SwapId, out _);
            _uneconomicSince.TryRemove(swap.SwapId, out _);
            logger.LogWarning("A refund of Satora on-chain swap {SwapId} of store {StoreId} may have gone out but is not recorded; keeping the swap open", swap.SwapId, swap.StoreId);
            return false;
        }
        if (uneconomicSince is { } since && now - since < UneconomicCloseAfter)
            return false;
        var reason = outputs.Count == 0 ? SatoraChainRefunds.EmptyReason : SatoraChainRefunds.UneconomicReason;
        var closed = await swaps.SetClosedAsync(swap.SwapId, reason, now);
        await ForgetAsync(swap);
        if (!closed)
        {
            logger.LogInformation("Satora on-chain swap {SwapId} of store {StoreId} holds nothing to refund, but the store closed it first",
                swap.SwapId, swap.StoreId);
            return true;
        }
        logger.LogWarning("Satora on-chain swap {SwapId} of store {StoreId} closed without a refund ({Reason})", swap.SwapId, swap.StoreId, reason);
        await credit.NoteAsync(swap,
            $"Satora on-chain swap {swap.SwapId} closed without a refund because its address {(outputs.Count == 0 ? "holds nothing" : "holds less than the network fee to refund it")}.",
            InvoiceEventData.EventSeverity.Info);
        await Notify(swap, Problems.NothingToRefund, null, reason);
        return true;
    }

    // An address closes its swap as holding nothing only when a read at least RefundCheckInterval after
    // the first such read agrees: a funding the swap server's node saw may not have reached this esplora
    // yet, and a refund spent in the mempool is missing from its utxo list until the SDK returns it.
    private bool NothingForAWhile(string swapId, DateTimeOffset now) =>
        now - _nothingSince.GetOrAdd(swapId, now) >= RefundCheckInterval;

    // Nothing can be claimed for a swap the server does not know. Once it could no longer be funded,
    // whatever reached its address is offered for a refund.
    private async Task UnknownToServerAsync(SatoraChainSwap swap, CancellationToken cancellationToken)
    {
        await AlertOnce(swap, Problems.UnknownToServer, swap.VhtlcRefundLocktime);
        var now = UtcNow;
        if (now >= swap.VhtlcRefundLocktime)
            await CloseAsync(swap, now, cancellationToken);
    }

    private Task AlertIfNearCutoffAsync(SatoraChainSwap swap, SatoraSwapStatus status) =>
        SatoraChainActions.NearCutoff(swap, status, UtcNow)
            ? AlertOnce(swap, Problems.NearClaimCutoff, swap.ClaimCutoff)
            : Task.CompletedTask;

    private async Task ForgetAsync(SatoraChainSwap swap)
    {
        var swapId = swap.SwapId;
        _claimFailures.TryRemove(swapId, out _);
        _lateClaimFailureLogged.TryRemove(swapId, out _);
        _refundFailures.TryRemove(swapId, out _);
        _fundingMisses.TryRemove(swapId, out _);
        _nextRefundCheck.TryRemove(swapId, out _);
        _nothingSince.TryRemove(swapId, out _);
        _uneconomicSince.TryRemove(swapId, out _);
        _leftoverRecheck.TryRemove(swapId, out _);
        await _alerts.ForgetSwapAsync(swap.StoreId, swapId);
    }

    // perStatus: once for each status the swap reaches, rather than once for the swap.
    private async Task AlertOnce(SatoraChainSwap swap, Problems problem, DateTimeOffset? deadline, object? status = null, bool perStatus = false)
    {
        if (await _alerts.TryStartAsync(swap.StoreId, swap.SwapId, problem, perStatus ? status : null))
            await Notify(swap, problem, deadline, status?.ToString());
    }

    private async Task AlertStoreOnce(string storeId, Problems problem, DateTimeOffset? deadline)
    {
        if (await _alerts.TryStartAsync(storeId, null, problem))
            await SendAsync(new SatoraChainNotification { StoreId = storeId, Problem = problem, Deadline = deadline });
    }

    // Names the invoice, which the store looks a payment up by, as well as the swap.
    private Task Notify(SatoraChainSwap swap, Problems problem, DateTimeOffset? deadline, string? status, long? excessSats = null) =>
        SendAsync(new SatoraChainNotification
        {
            StoreId = swap.StoreId,
            SwapId = swap.SwapId,
            InvoiceId = swap.InvoiceId,
            Problem = problem,
            Status = status,
            Deadline = deadline,
            ExcessSats = excessSats
        });

    protected virtual async Task SendAsync(SatoraChainNotification notification)
    {
        try
        {
            await notifier.SendAsync(notification.StoreId, notification);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not send the Satora on-chain notification for swap {SwapId} of store {StoreId}", notification.SwapId, notification.StoreId);
        }
    }
}
