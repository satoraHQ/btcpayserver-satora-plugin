using System.Collections.Concurrent;
using System.Globalization;
using System.Numerics;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using Problems = BTCPayServer.Plugins.Satora.Stable.SatoraStableNotification.Problems;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;
using SdkRelayFundingState = uniffi.satora_sdk_ffi.RelayFundingState;

namespace BTCPayServer.Plugins.Satora.Stable;

// The only place incoming stablecoin swaps are funded, recorded, claimed and closed. Each store runs on
// its own task, one tick at a time, as SatoraChainWorker does. A deposit is funded through the swap
// server's relay with a signature of the store's key. The SDK journals every relay attempt and signs
// one Permit2 nonce per swap, so its journal, not this worker, decides whether an attempt may still
// land: the worker calls the relay whenever the deposit is there before the funding cutoff, and
// reconciles only when the SDK says an earlier request may have gone out. What a swap will not take
// waits for the owner, and leaves the deposit address only for the destination the owner entered.
// Once the worker is stopping no funding, claim or sweep starts.
public class SatoraStableWorker(
    SatoraStableSwaps swaps,
    SatoraStableWallets wallets,
    SatoraStableCredit credit,
    SatoraNotifier notifier,
    SatoraSentAlerts sentAlerts,
    ILogger<SatoraStableWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StatusFailureAlertAfter = TimeSpan.FromMinutes(30);
    // A sweep goes out within this of the owner's gas arriving at the deposit address.
    public static readonly TimeSpan SweepCheckInterval = TimeSpan.FromMinutes(1);
    // How often a relay funding whose transaction is not recorded is looked up once the swap server
    // has moved past Pending.
    public static readonly TimeSpan FundingLookupInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DepositReadTimeout = TimeSpan.FromSeconds(30);
    private const int FailuresBeforeAlert = 2;

    private readonly SatoraStoreTicks _ticks = new();
    private readonly ConcurrentDictionary<string, int> _fundingFailures = new();
    private readonly ConcurrentDictionary<string, int> _claimFailures = new();
    private readonly ConcurrentDictionary<string, bool> _lateClaimFailureLogged = new();
    // Swaps whose relay request may have gone out without a proven outcome: reconciled every tick,
    // and nothing new signed for them, until the SDK says what became of it.
    private readonly ConcurrentDictionary<string, bool> _relayUnresolved = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextLateRead = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextSweepCheck = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextFundingLookup = new();
    private readonly ConcurrentDictionary<string, int> _sweepFailures = new();
    private readonly SatoraOnceAlerts _alerts = new(sentAlerts, "stable", logger);
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
                logger.LogError(e, "Could not list the open Satora stablecoin swaps");
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
                logger.LogError(e, "Satora stablecoin swaps of store {StoreId} could not be processed", storeId);
            }
        });

    internal async Task TickAsync(string storeId, CancellationToken stoppingToken)
    {
        var open = await swaps.ListOpenAsync(storeId, stoppingToken);
        // Fundings and claims have deadlines, so they go before the sweeps.
        var active = open.Where(s => s.NeedsAttentionAt is null).ToList();
        if (active.Count > 0)
            await ActiveTickAsync(storeId, active, stoppingToken);
        // A sweep needs only the owner's destination, gas at the deposit address and the SDK. Any other
        // swap waiting for the owner is the owner's to act on.
        foreach (var swap in open.Where(s => s is { AttentionReason: SatoraStableAttention.Sweep, NeedsAttentionAt: not null, SweepDestination: not null }))
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            await Guarded(swap, () => SweepTickAsync(swap, stoppingToken), stoppingToken);
        }
    }

    private async Task ActiveTickAsync(string storeId, List<SatoraStableSwap> active, CancellationToken stoppingToken)
    {
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
        // After every claim, which has a deadline, as a lookup can take as long as the SDK's reconcile.
        foreach (var swap in active)
        {
            if (stoppingToken.IsCancellationRequested)
                return;
            if (statuses.TryGetValue(swap.SwapId, out var status))
                await Guarded(swap, () => RecordLandedFundingAsync(swap, status, stoppingToken), stoppingToken);
        }
    }

    private async Task Guarded(SatoraStableSwap swap, Func<Task> action, CancellationToken stoppingToken)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "Satora stablecoin swap {SwapId} of store {StoreId} could not be processed; retrying", swap.SwapId, swap.StoreId);
        }
    }

    private async Task StatusReadFailedAsync(string storeId, List<SatoraStableSwap> active, Exception error)
    {
        var now = UtcNow;
        if (_statusFailingSince.TryAdd(storeId, now))
            logger.LogWalletFailure(error, "Could not read the Satora stablecoin swaps of store {StoreId}; retrying", storeId);
        if (now - _statusFailingSince.GetValueOrDefault(storeId, now) < StatusFailureAlertAfter)
            return;
        var cutoff = active.Where(s => s.ClaimCutoff > now).Select(s => (DateTimeOffset?)s.ClaimCutoff).Min();
        if (cutoff is not null)
            await AlertOnce(storeId, null, Problems.StatusUnavailable, cutoff);
    }

    private async Task ActAsync(SatoraStableSwap swap, SatoraSwapStatus status, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        switch (SatoraStableActions.Decide(swap, status, now))
        {
            case SatoraStableAction.Wait:
                await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraStableAction.Fund:
                await FundAsync(swap, now, cancellationToken);
                break;
            case SatoraStableAction.Release:
                await ReleaseAsync(swap, serverStopped: status == SatoraSwapStatus.Expired, now, cancellationToken);
                break;
            case SatoraStableAction.RecordFunding:
                await RecordFundingAsync(swap, now);
                break;
            case SatoraStableAction.Claim:
                // A funding the worker never recorded (it was down, or the server was quicker) goes on the
                // invoice first, as a claim can take a while or keep failing.
                if (!swap.PaymentRecorded)
                    await TryRecordFundingAsync(swap, now);
                if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else
                    await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraStableAction.FinishClaim:
                if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else
                    await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraStableAction.FinishClaimOrHtlcRefund:
                // A claim that lands and a refund of the customer's deposit must never both happen, so the
                // deposit is left for its refund only once the SDK says no claim can complete, which is
                // final. A query that fails moves nothing.
                if (!await wallets.ClaimPossibleAsync(swap.StoreId, swap.SwapId))
                    await NeedsHtlcRefundAsync(swap, SatoraStableActions.UnclaimedReason, now);
                else if (await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                else
                    await AlertIfNearCutoffAsync(swap, status);
                break;
            case SatoraStableAction.FinishClaimOrSweep:
                // A claim that spends the VHTLC now would take both legs of the swap, so only one that
                // already spent it is finished, for its receipt. The SDK's word that none can complete
                // is final.
                if (!await wallets.ClaimPossibleAsync(swap.StoreId, swap.SwapId))
                    await NeedsSweepAsync(swap, SatoraStableActions.HtlcRefundedReason, now);
                else if (await wallets.AmountsAsync(swap.StoreId, swap.SwapId, cancellationToken) is { SpendableSats: 0, RecoverableSats: 0 } &&
                         await ClaimAsync(swap, cancellationToken))
                    await CreditAsync(swap);
                break;
            case SatoraStableAction.Credit:
                await CreditAsync(swap);
                break;
            case SatoraStableAction.Close:
                await CloseAsync(swap, now, cancellationToken);
                break;
            case SatoraStableAction.NeedsHtlcRefund:
                await NeedsHtlcRefundAsync(swap, status.ToString(), now);
                break;
            case SatoraStableAction.NeedsSweep:
                await NeedsSweepAsync(swap, SatoraStableActions.HtlcRefundedReason, now);
                break;
            case SatoraStableAction.ClaimUnverified:
                // Past the cutoff the SDK starts no new claim, but still returns the receipt of one it journaled.
                if (await ClaimAsync(swap, cancellationToken))
                {
                    await CreditAsync(swap);
                    break;
                }
                await AlertOnce(swap.StoreId, swap, Problems.ClaimUnverified, swap.ClaimCutoff, status);
                // A failed attempt says nothing about a claim the SDK may still finish: only its word that
                // none can complete, which is final, closes the swap once the server has taken the HTLC.
                if (status != SatoraSwapStatus.ServerRedeemed || await wallets.ClaimPossibleAsync(swap.StoreId, swap.SwapId))
                    break;
                logger.LogError("Satora stablecoin swap {SwapId} of store {StoreId} is {Status} on the swap server, but the wallet has no receipt for claiming it and no claim can complete; it was not credited",
                    swap.SwapId, swap.StoreId, status);
                await CloseAsync(swap, now, cancellationToken);
                break;
            case SatoraStableAction.Alert:
                logger.LogError("Satora stablecoin swap {SwapId} of store {StoreId} reached an unexpected status, {Status}; nothing was moved",
                    swap.SwapId, swap.StoreId, status);
                await AlertOnce(swap.StoreId, swap, Problems.UnexpectedStatus, null, status, perStatus: true);
                break;
        }
    }

    // The deposit address is read every tick until shortly after the invoice expires, and every
    // LatePoll after that until the funding cutoff. What it holds goes to the checkout, and once it
    // holds the deposit the relay funds the swap.
    private async Task FundAsync(SatoraStableSwap swap, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var unresolved = _relayUnresolved.ContainsKey(swap.SwapId);
        if (!ReadDue(swap, now, unresolved))
            return;
        if (unresolved && await ReconcileAsync(swap, cancellationToken) != SatoraRelayState.Expired)
            return;
        var deposited = await ReadDepositedAsync(swap, cancellationToken);
        await TrySetDepositProgressAsync(swap, deposited);
        var deposit = SatoraStableActions.Units(swap.SourceUnits) ??
                      throw new FormatException($"Satora stablecoin swap {swap.SwapId} has an invalid deposit, {swap.SourceUnits}.");
        if (deposited < deposit)
        {
            if (SatoraStableActions.MayBeAbandoned(swap, deposited, now) && await credit.SettledOtherwiseAsync(swap))
                await CloseUnfundedAsync(swap, SatoraStableActions.AbandonedReason, now);
            return;
        }
        if (deposited > deposit)
            await OverpaidAsync(swap, deposited - deposit);
        cancellationToken.ThrowIfCancellationRequested();
        // Read again after the deposit, so neither the cutoff nor the permit deadline is judged from
        // before a slow read.
        var signAt = UtcNow;
        // Before every call, as any call may sign: from then on the deposit may be in the HTLC, which
        // closing the swap has to know.
        if (!await swaps.MarkFundingAttemptedAsync(swap.SwapId, signAt))
        {
            logger.LogInformation("Satora stablecoin swap {SwapId} of store {StoreId} changed or passed its funding cutoff before it could be funded",
                swap.SwapId, swap.StoreId);
            return;
        }
        swap.FundingAttemptedAt ??= signAt;
        cancellationToken.ThrowIfCancellationRequested();
        RelayFunding funding;
        try
        {
            funding = await wallets.FundAsync(swap, SatoraStableActions.PermitDeadline(swap, signAt), cancellationToken);
        }
        catch (SdkException e) when (e.Kind == SdkErrorKind.UnresolvedFunding && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation("The SDK refused to fund Satora stablecoin swap {SwapId} of store {StoreId} again, as an earlier relay request may have gone out; reconciling it ({Reason})",
                swap.SwapId, swap.StoreId, e.Message);
            _relayUnresolved[swap.SwapId] = true;
            await ReconcileAsync(swap, cancellationToken);
            return;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // The next call tries again, or throws UnresolvedFunding if this one may have sent its request.
            var failures = _fundingFailures.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1);
            logger.LogWarning(e, "Funding Satora stablecoin swap {SwapId} of store {StoreId} through the relay failed ({Failures} so far)",
                swap.SwapId, swap.StoreId, failures);
            if (failures >= FailuresBeforeAlert)
                await AlertOnce(swap.StoreId, swap, Problems.FundingFailing, swap.FundingStartCutoff);
            return;
        }
        await RecordFundedAsync(swap, funding.TxHash);
    }

    // Only for the checkout to show, so a failed write does not hold back the funding.
    private async Task TrySetDepositProgressAsync(SatoraStableSwap swap, BigInteger deposited)
    {
        try
        {
            await credit.SetDepositProgressAsync(swap.InvoiceId, SatoraStableCredit.MethodOf(swap), swap.SwapId,
                deposited.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not show the checkout of Satora stablecoin swap {SwapId} of store {StoreId} what its deposit address holds",
                swap.SwapId, swap.StoreId);
        }
    }

    private bool ReadDue(SatoraStableSwap swap, DateTimeOffset now, bool relayUnresolved)
    {
        if (SatoraStableActions.FastPoll(swap, now, relayUnresolved))
            return true;
        if (_nextLateRead.TryGetValue(swap.SwapId, out var next) && now < next)
            return false;
        _nextLateRead[swap.SwapId] = now + SatoraStableActions.LatePoll;
        return true;
    }

    private async Task<BigInteger> ReadDepositedAsync(SatoraStableSwap swap, CancellationToken cancellationToken) =>
        await wallets.GetDepositedAsync(swap, cancellationToken).WaitAsync(DepositReadTimeout, cancellationToken);

    // Sends nothing. A swap the SDK has no journal for was never signed for by this wallet, or lost it.
    private async Task<SatoraRelayState> ReconcileAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
    {
        SdkRelayFundingState state;
        try
        {
            state = await wallets.ReconcileAsync(swap, cancellationToken);
        }
        catch (SdkException e) when (e.Kind == SdkErrorKind.InvalidSwap && !cancellationToken.IsCancellationRequested)
        {
            _relayUnresolved.TryRemove(swap.SwapId, out _);
            logger.LogDebug(e, "The SDK has no relay funding of Satora stablecoin swap {SwapId} of store {StoreId} to reconcile", swap.SwapId, swap.StoreId);
            return SatoraRelayState.Unknown;
        }
        switch (state)
        {
            case SdkRelayFundingState.Funded funded:
                await RecordFundedAsync(swap, funded.txHash);
                return SatoraRelayState.Funded;
            // Funded under a transaction the SDK does not know yet: reconciled again until it proves one.
            case SdkRelayFundingState.Pending { nonceConsumed: true }:
                _relayUnresolved[swap.SwapId] = true;
                logger.LogInformation("The relay funding of Satora stablecoin swap {SwapId} of store {StoreId} is pending: its signature was used by a transaction the SDK does not know yet; reconciling it again",
                    swap.SwapId, swap.StoreId);
                return SatoraRelayState.Funded;
            case SdkRelayFundingState.Pending pending:
                _relayUnresolved[swap.SwapId] = true;
                logger.LogInformation("The relay funding of Satora stablecoin swap {SwapId} of store {StoreId} is pending, and its signature can land until {Deadline}; reconciling it again",
                    swap.SwapId, swap.StoreId, DateTimeOffset.FromUnixTimeSeconds((long)pending.deadline).ToString("u", CultureInfo.InvariantCulture));
                return SatoraRelayState.Live;
            case SdkRelayFundingState.Expired:
                _relayUnresolved.TryRemove(swap.SwapId, out _);
                return SatoraRelayState.Expired;
            default:
                throw new InvalidOperationException($"The SDK reconciled the relay funding with an unexpected state, {state}.");
        }
    }

    // A relay funding that landed while the worker had no answer for it, as when a restart cut its
    // request off, is recorded once the SDK proves its transaction from its journal or the swap server.
    // Fund reconciles only while the swap is Pending, which the server may have moved past already.
    // A lookup sends nothing.
    private async Task RecordLandedFundingAsync(SatoraStableSwap swap, SatoraSwapStatus status, CancellationToken cancellationToken)
    {
        if (swap.FundingAttemptedAt is null || swap.FundTxHash is not null || status is SatoraSwapStatus.Pending or SatoraSwapStatus.Expired)
            return;
        var now = UtcNow;
        if (_nextFundingLookup.TryGetValue(swap.SwapId, out var next) && now < next)
            return;
        _nextFundingLookup[swap.SwapId] = now + FundingLookupInterval;
        SdkRelayFundingState state;
        try
        {
            state = await wallets.ReconcileAsync(swap, cancellationToken);
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(e, "The relay funding of Satora stablecoin swap {SwapId} of store {StoreId} could not be looked up; trying again later",
                swap.SwapId, swap.StoreId);
            return;
        }
        if (state is SdkRelayFundingState.Funded funded)
            await RecordFundedAsync(swap, funded.txHash);
    }

    private async Task RecordFundedAsync(SatoraStableSwap swap, string txHash)
    {
        _relayUnresolved.TryRemove(swap.SwapId, out _);
        _fundingFailures.TryRemove(swap.SwapId, out _);
        if (await swaps.SetFundedAsync(swap.SwapId, txHash))
            logger.LogInformation("Satora stablecoin swap {SwapId} of store {StoreId} funded through the relay in {TxHash}", swap.SwapId, swap.StoreId, txHash);
        swap.FundTxHash ??= txHash;
    }

    // The relay takes exactly the deposit and leaves the rest at the address.
    private async Task OverpaidAsync(SatoraStableSwap swap, BigInteger excess)
    {
        if (!await _alerts.TryStartAsync(swap.StoreId, swap.SwapId, Problems.Overpaid))
            return;
        var amount = $"{SatoraStableActions.FormatUnits(excess, swap.Decimals)} {swap.Token}";
        logger.LogWarning("Satora stablecoin swap {SwapId} of store {StoreId} was sent {Excess} more than its deposit", swap.SwapId, swap.StoreId, amount);
        await credit.NoteAsync(swap,
            $"The customer sent {amount} more than Satora stablecoin swap {swap.SwapId} asked for. The extra stays at the deposit address {swap.DepositAddress} and can be refunded from Satora's Stablecoin refunds page once the swap completes.",
            InvoiceEventData.EventSeverity.Warning);
        await SendAsync(new SatoraStableNotification
        {
            StoreId = swap.StoreId,
            SwapId = swap.SwapId,
            InvoiceId = swap.InvoiceId,
            Problem = Problems.Overpaid,
            Amount = amount
        });
    }

    // No funding starts any more, so once no relay signature can still pull the deposit, the address is
    // read: what it holds goes to the owner, and an empty one closes the swap. The journal is asked
    // first, as a signature that lands would empty the address.
    private async Task ReleaseAsync(SatoraStableSwap swap, bool serverStopped, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!ReadDue(swap, now, _relayUnresolved.ContainsKey(swap.SwapId)))
            return;
        var relay = swap.FundingAttemptedAt is null ? SatoraRelayState.None : await ReconcileAsync(swap, cancellationToken);
        var deposited = await ReadDepositedAsync(swap, cancellationToken);
        switch (SatoraStableActions.Release(relay, serverStopped, deposited, swap.Decimals))
        {
            case SatoraStableRelease.Sweep:
                await NeedsSweepAsync(swap, SatoraStableActions.SweepReason(swap, serverStopped, deposited), now);
                break;
            case SatoraStableRelease.Close:
                await CloseUnfundedAsync(swap, SatoraStableActions.UnfundedReason, now);
                break;
            case SatoraStableRelease.HtlcRefund:
                await NeedsHtlcRefundAsync(swap,
                    relay == SatoraRelayState.Funded ? SatoraStableActions.FundedLateReason : SatoraStableActions.FundingUnknownReason, now);
                break;
            case SatoraStableRelease.Wait:
                break;
        }
    }

    private async Task CloseUnfundedAsync(SatoraStableSwap swap, string reason, DateTimeOffset now)
    {
        await credit.DeactivatePromptAsync(swap.InvoiceId, SatoraStableCredit.MethodOf(swap), swap.SwapId);
        var closed = await swaps.SetClosedAsync(swap.SwapId, reason, now);
        await ForgetAsync(swap);
        if (closed)
            logger.LogInformation("Satora stablecoin swap {SwapId} of store {StoreId} closed with nothing at its deposit address ({Reason})",
                swap.SwapId, swap.StoreId, reason);
    }

    private async Task RecordFundingAsync(SatoraStableSwap swap, DateTimeOffset now)
    {
        var outcome = await credit.RecordAsync(swap, PaymentStatus.Processing, now);
        await swaps.SetPaymentRecordedAsync(swap.SwapId, true);
        swap.PaymentRecorded = true;
        logger.LogInformation("Satora stablecoin swap {SwapId} of store {StoreId} funded ({Outcome})", swap.SwapId, swap.StoreId, outcome);
    }

    private async Task TryRecordFundingAsync(SatoraStableSwap swap, DateTimeOffset now)
    {
        try
        {
            await RecordFundingAsync(swap, now);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not record the funding of Satora stablecoin swap {SwapId} of store {StoreId}; claiming anyway", swap.SwapId, swap.StoreId);
        }
    }

    // The SDK journals the claim before submitting it, so calling again after any error resumes it
    // rather than claiming twice, and a claim that already went through returns its receipt.
    private async Task<bool> ClaimAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
    {
        var claiming = false;
        try
        {
            var destination = await wallets.GetClaimAddressAsync(swap.StoreId, cancellationToken);
            var shortfallLimit = SatoraStableActions.ShortfallLimit(await credit.PaymentToleranceAsync(swap));
            cancellationToken.ThrowIfCancellationRequested();
            claiming = true;
            var settlement = await wallets.ClaimAsync(swap, destination, shortfallLimit, cancellationToken);
            claiming = false;
            var (txid, vhtlcSats) = settlement switch
            {
                SdkArkadeSettlement.Offchain offchain => (offchain.arkTxid, checked((long)offchain.claimAmountSats)),
                SdkArkadeSettlement.Delegated delegated => (delegated.commitmentTxid, await wallets.VhtlcSatsAsync(swap.StoreId, swap.SwapId, cancellationToken)),
                var other => throw new InvalidOperationException($"The SDK settled the claim with an unexpected outcome, {other}.")
            };
            await swaps.SetClaimedAsync(swap.SwapId, txid, vhtlcSats);
            swap.ClaimTxId = txid;
            swap.VhtlcSats = vhtlcSats;
            _claimFailures.TryRemove(swap.SwapId, out _);
            logger.LogInformation("Claimed Satora stablecoin swap {SwapId} of store {StoreId} ({VhtlcSats} sats)", swap.SwapId, swap.StoreId, vhtlcSats);
            return true;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            var failures = _claimFailures.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1);
            var claimable = UtcNow < swap.ClaimCutoff;
            // Past the cutoff the claim is retried every tick while the SDK may still finish it; one warning is enough.
            var level = claimable || _lateClaimFailureLogged.TryAdd(swap.SwapId, true) ? LogLevel.Warning : LogLevel.Debug;
            // The claim's refusal by the SDK's own rules, such as a shortfall beyond the limit, can repeat on
            // every tick until what the SDK reads changes, and its message usually says all the wrapper's
            // stack would, so it gets one line. An InvalidSwap from the reads around the claim keeps its stack.
            if (claiming && e is SdkException { Kind: SdkErrorKind.InvalidSwap } refusal)
                logger.Log(level, "Claiming Satora stablecoin swap {SwapId} of store {StoreId} was refused by the SDK ({Failures} so far): {Reason}",
                    swap.SwapId, swap.StoreId, failures, refusal.Message);
            else
                logger.Log(level, e, "Claiming Satora stablecoin swap {SwapId} of store {StoreId} failed ({Failures} so far)", swap.SwapId, swap.StoreId, failures);
            if (failures == FailuresBeforeAlert && claimable)
                await Notify(swap.StoreId, swap, Problems.ClaimFailing, swap.ClaimCutoff, null);
            return false;
        }
    }

    // A surplus or shortfall is noted only by the call that settled the payment, before the swap is
    // marked credited, so a tick that fails in between does not note it twice.
    private async Task CreditAsync(SatoraStableSwap swap)
    {
        var outcome = await credit.RecordAsync(swap, PaymentStatus.Settled, UtcNow);
        if (outcome == SatoraCreditOutcome.Recorded && SatoraStableCredit.SurplusSats(swap) is > 0 and var surplus)
            await credit.NoteAsync(swap,
                $"The exchange delivered {surplus} {(surplus == 1 ? "sat" : "sats")} more than due for Satora stablecoin swap {swap.SwapId}. The surplus stays in the Satora wallet.",
                InvoiceEventData.EventSeverity.Info);
        else if (outcome == SatoraCreditOutcome.Recorded && SatoraStableCredit.ShortfallSats(swap) is > 0 and var shortfall)
            await credit.NoteAsync(swap,
                $"The exchange delivered {shortfall} {(shortfall == 1 ? "sat" : "sats")} less than due for Satora stablecoin swap {swap.SwapId}. The payment is recorded at the amount that arrived.",
                InvoiceEventData.EventSeverity.Info);
        await swaps.SetCreditedAsync(swap.SwapId);
        swap.Credited = swap.PaymentRecorded = true;
        logger.LogInformation("Satora stablecoin swap {SwapId} of store {StoreId} credited ({Outcome})", swap.SwapId, swap.StoreId, outcome);
    }

    // The funding spent the swap's Permit2 nonce, so nothing else can pull from the address: whatever it
    // still holds, beyond dust, the customer sent on top of the deposit.
    private async Task CloseAsync(SatoraStableSwap swap, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (await ReadDepositedAsync(swap, cancellationToken) >= SatoraStableActions.DustUnits(swap.Decimals))
        {
            await NeedsSweepAsync(swap, SatoraStableActions.LeftoverReason, now);
            return;
        }
        if (swap.PaymentRecorded && !swap.Credited)
            await credit.UnaccountAsync(swap);
        await swaps.SetCompletedAsync(swap.SwapId, now);
        await ForgetAsync(swap);
    }

    // An HTLC's refund pays the deposit address the token the HTLC locked, not the one the customer sent.
    private Task NeedsSweepAsync(SatoraStableSwap swap, string reason, DateTimeOffset now) =>
        ToOwnerAsync(swap, SatoraStableAttention.Sweep, reason, Problems.NeedsSweep, null,
            $"Tokens at the deposit address {swap.DepositAddress} of Satora stablecoin swap {swap.SwapId} do not pay the invoice ({SatoraStableActions.Describe(reason)}). Refund them from Satora's Stablecoin refunds page.",
            now, reason == SatoraStableActions.HtlcRefundedReason ? SatoraStableRefunds.HtlcToken(swap.ChainId) : null);

    private Task NeedsHtlcRefundAsync(SatoraStableSwap swap, string reason, DateTimeOffset now) =>
        ToOwnerAsync(swap, SatoraStableAttention.HtlcRefund, reason, Problems.NeedsHtlcRefund, swap.EvmRefundLocktime,
            $"The deposit of Satora stablecoin swap {swap.SwapId} is locked in the swap contract and will not pay the invoice ({SatoraStableActions.Describe(reason)}). It can be refunded to the deposit address from {swap.EvmRefundLocktime.ToString("u", CultureInfo.InvariantCulture)}.",
            now);

    // A payment the swap will not settle comes off the invoice first, and the swap's address off the
    // invoice's prompt, so a failure in between leaves the row to try all again.
    private async Task ToOwnerAsync(SatoraStableSwap swap, SatoraStableAttention attention, string reason, Problems problem, DateTimeOffset? deadline,
        string note, DateTimeOffset now, string? sweepToken = null)
    {
        if (swap.PaymentRecorded && !swap.Credited)
            await credit.UnaccountAsync(swap);
        await credit.DeactivatePromptAsync(swap.InvoiceId, SatoraStableCredit.MethodOf(swap), swap.SwapId);
        if (!await swaps.SetNeedsAttentionAsync(swap.SwapId, swap.ClaimTxId, attention, now, sweepToken))
        {
            logger.LogWarning("Satora stablecoin swap {SwapId} of store {StoreId} changed since it was read; it is decided again on the next tick",
                swap.SwapId, swap.StoreId);
            return;
        }
        swap.NeedsAttentionAt = now;
        swap.AttentionReason = attention;
        swap.SweepToken = sweepToken;
        logger.LogWarning("Satora stablecoin swap {SwapId} of store {StoreId} needs the store ({Attention}: {Reason})", swap.SwapId, swap.StoreId, attention, reason);
        // Only once the swap is saved as needing the store, so a tick that fails before cannot note it twice.
        await credit.NoteAsync(swap, note, InvoiceEventData.EventSeverity.Warning);
        await AlertOnce(swap.StoreId, swap, problem, deadline, reason);
        await ForgetAsync(swap);
    }

    // Nothing can be claimed for a swap the server does not know. Once the server could no longer fund
    // its VHTLC, what reached its deposit address goes to the owner.
    private async Task UnknownToServerAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
    {
        await AlertOnce(swap.StoreId, swap, Problems.UnknownToServer, swap.VhtlcRefundLocktime);
        var now = UtcNow;
        if (now >= swap.VhtlcRefundLocktime)
            await ReleaseAsync(swap, serverStopped: true, now, cancellationToken);
    }

    // The owner's destination gets the deposit address's whole balance of the token the owner chose,
    // paid for with gas the owner sends to the address. The destination can change until the address
    // holds any gas, enough for the transfer or not, as the first attempt then fixes it; from then on the
    // SDK's record of the transfer decides whether a call sends one or verifies the last. The address is
    // read every SweepCheckInterval.
    private async Task SweepTickAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
    {
        var now = UtcNow;
        if (_nextSweepCheck.TryGetValue(swap.SwapId, out var next) && now < next)
            return;
        _nextSweepCheck[swap.SwapId] = now + SweepCheckInterval;
        using var hold = await swaps.HoldForSweepAsync(swap.SwapId, cancellationToken);
        // The owner may have dismissed the swap, or changed where its tokens go, since this tick read it.
        if (await swaps.FindAsync(swap.StoreId, swap.SwapId) is not
            { CompletedAt: null, AttentionReason: SatoraStableAttention.Sweep, SweepDestination: { } destination } current)
        {
            await ForgetAsync(swap);
            return;
        }
        swap = current;
        var token = swap.SweepToken;
        var funds = await wallets.GetFundsAsync(swap, token, cancellationToken).WaitAsync(DepositReadTimeout, cancellationToken);
        var worth = SatoraStableRefunds.Worth(swap, token, funds.TokenUnits);
        if (!swap.SweepAttempted)
        {
            // The page shows the owner what the address holds of the token chosen.
            if (!worth)
                return;
            if (funds.NativeWei.IsZero)
            {
                await GasNeededAsync(swap);
                return;
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!await swaps.TryStartSweepAsync(swap.SwapId, destination, token))
                return;
            swap.SweepAttempted = true;
        }
        cancellationToken.ThrowIfCancellationRequested();
        EvmReceipt receipt;
        try
        {
            receipt = await wallets.SweepAsync(swap, destination, token, cancellationToken);
        }
        catch (SdkException e) when (e.Kind == SdkErrorKind.InsufficientGas && !cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug(e, "The deposit address of Satora stablecoin swap {SwapId} of store {StoreId} holds too little gas to send its tokens back",
                swap.SwapId, swap.StoreId);
            await GasNeededAsync(swap);
            return;
        }
        catch (SdkException e) when (e.Kind == SdkErrorKind.UnresolvedFunding && !cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(e, "Satora stablecoin swap {SwapId} of store {StoreId} is not swept while a relay funding signature can still pull its deposit",
                swap.SwapId, swap.StoreId);
            return;
        }
        // Only a transfer this wallet sent empties the address once no relay signature can pull from
        // it: the SDK refuses a sweep with nothing to move after its transfer's hash went unrecorded.
        catch (SdkException e) when (e.Kind == SdkErrorKind.InvalidSwap && !worth && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(e, "The deposit address of Satora stablecoin swap {SwapId} of store {StoreId} holds nothing more to send to {Destination}, and its sweep was not recorded",
                swap.SwapId, swap.StoreId, destination);
            if (await swaps.SetClosedAsync(swap.SwapId, SatoraStableRefunds.SweepUnrecordedReason, now))
                await credit.NoteAsync(swap,
                    $"The deposit address {swap.DepositAddress} of Satora stablecoin swap {swap.SwapId} has nothing left to refund, so the swap is closed. A refund to {destination} started, but its transaction was not recorded. Check the history of {destination} to confirm the tokens arrived.",
                    InvoiceEventData.EventSeverity.Warning);
            await ForgetAsync(swap);
            return;
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            var failures = _sweepFailures.AddOrUpdate(swap.SwapId, 1, (_, count) => count + 1);
            logger.LogWarning(e, "Sending back the tokens at the deposit address of Satora stablecoin swap {SwapId} of store {StoreId} failed ({Failures} so far)",
                swap.SwapId, swap.StoreId, failures);
            if (failures >= FailuresBeforeAlert)
                await AlertOnce(swap.StoreId, swap, Problems.SweepFailing, null);
            return;
        }
        _sweepFailures.TryRemove(swap.SwapId, out _);
        // A sweep repeated after it settled returns the same receipt. Noted before it is recorded, so a
        // failure in between notes it twice rather than never.
        if (receipt.TxHash != swap.SweepTxHash)
        {
            logger.LogInformation("Sent the tokens at the deposit address of Satora stablecoin swap {SwapId} of store {StoreId} to {Destination} in {TxHash}",
                swap.SwapId, swap.StoreId, destination, receipt.TxHash);
            await credit.NoteAsync(swap,
                $"The tokens at the deposit address {swap.DepositAddress} of Satora stablecoin swap {swap.SwapId} were sent to {destination} in {receipt.TxHash}.",
                InvoiceEventData.EventSeverity.Success);
            await swaps.SetSweepTxHashAsync(swap.SwapId, receipt.TxHash);
        }
        // The sweep sent the whole balance, so what the address holds now arrived since, and the next
        // check sends it to the same destination.
        var left = await wallets.GetFundsAsync(swap, token, cancellationToken).WaitAsync(DepositReadTimeout, cancellationToken);
        if (SatoraStableRefunds.Worth(swap, token, left.TokenUnits))
            return;
        if (token is not null)
        {
            var own = await wallets.GetFundsAsync(swap, null, cancellationToken).WaitAsync(DepositReadTimeout, cancellationToken);
            if (SatoraStableRefunds.Worth(swap, null, own.TokenUnits))
            {
                await OwnTokenLeftAsync(swap, token, own.TokenUnits);
                return;
            }
        }
        await swaps.SetCompletedAsync(swap.SwapId, UtcNow);
        await ForgetAsync(swap);
    }

    // As an overpayment's excess beside a token sent by mistake. Where the other token went need not be
    // where this one goes, so the owner enters a destination again.
    private async Task OwnTokenLeftAsync(SatoraStableSwap swap, string sweptToken, BigInteger units)
    {
        if (await swaps.ResetSweepToOwnTokenAsync(swap.SwapId, sweptToken))
        {
            var held = SatoraStableRefunds.Amount(swap, null, units);
            logger.LogWarning("The deposit address of Satora stablecoin swap {SwapId} of store {StoreId} still holds {Held} after the token {Token} went back; it stays listed to send that back",
                swap.SwapId, swap.StoreId, held, sweptToken);
            await credit.NoteAsync(swap,
                $"The deposit address {swap.DepositAddress} of Satora stablecoin swap {swap.SwapId} still holds {held}. Enter a refund address for it on Satora's Stablecoin refunds page.",
                InvoiceEventData.EventSeverity.Warning);
        }
        await ForgetAsync(swap);
    }

    private async Task GasNeededAsync(SatoraStableSwap swap)
    {
        if (!await _alerts.TryStartAsync(swap.StoreId, swap.SwapId, Problems.GasNeeded))
            return;
        logger.LogInformation("Satora stablecoin swap {SwapId} of store {StoreId} waits for gas at its deposit address {DepositAddress} to send its tokens back",
            swap.SwapId, swap.StoreId, swap.DepositAddress);
        await SendAsync(new SatoraStableNotification
        {
            StoreId = swap.StoreId,
            SwapId = swap.SwapId,
            InvoiceId = swap.InvoiceId,
            Problem = Problems.GasNeeded,
            Amount = SatoraStableRefunds.GasToSend(swap.ChainId)
        });
    }

    private Task AlertIfNearCutoffAsync(SatoraStableSwap swap, SatoraSwapStatus status) =>
        SatoraStableActions.NearCutoff(swap, status, UtcNow)
            ? AlertOnce(swap.StoreId, swap, Problems.NearClaimCutoff, swap.ClaimCutoff)
            : Task.CompletedTask;

    private async Task ForgetAsync(SatoraStableSwap swap)
    {
        var swapId = swap.SwapId;
        _fundingFailures.TryRemove(swapId, out _);
        _claimFailures.TryRemove(swapId, out _);
        _lateClaimFailureLogged.TryRemove(swapId, out _);
        _relayUnresolved.TryRemove(swapId, out _);
        _nextLateRead.TryRemove(swapId, out _);
        _nextSweepCheck.TryRemove(swapId, out _);
        _nextFundingLookup.TryRemove(swapId, out _);
        _sweepFailures.TryRemove(swapId, out _);
        await _alerts.ForgetSwapAsync(swap.StoreId, swapId);
    }

    // swap is null for a problem with the store's swaps as a whole. perStatus: once for each status the
    // swap reaches, rather than once for the swap.
    private async Task AlertOnce(string storeId, SatoraStableSwap? swap, Problems problem, DateTimeOffset? deadline, object? status = null, bool perStatus = false)
    {
        if (await _alerts.TryStartAsync(storeId, swap?.SwapId, problem, perStatus ? status : null))
            await Notify(storeId, swap, problem, deadline, status?.ToString());
    }

    // Names the invoice, which the store looks a payment up by, as well as the swap.
    private Task Notify(string storeId, SatoraStableSwap? swap, Problems problem, DateTimeOffset? deadline, string? status) =>
        SendAsync(new SatoraStableNotification
        {
            StoreId = storeId,
            SwapId = swap?.SwapId,
            InvoiceId = swap?.InvoiceId,
            Problem = problem,
            Status = status,
            Deadline = deadline
        });

    protected virtual async Task SendAsync(SatoraStableNotification notification)
    {
        try
        {
            await notifier.SendAsync(notification.StoreId, notification);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not send the Satora stablecoin notification for swap {SwapId} of store {StoreId}", notification.SwapId, notification.StoreId);
        }
    }
}
