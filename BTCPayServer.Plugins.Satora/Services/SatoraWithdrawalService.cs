using System.Collections.Concurrent;
using BTCPayServer.Data.Payouts.LightningLike;
using BTCPayServer.Lightning;
using BTCPayServer.Payouts;
using BTCPayServer.PayoutProcessors;
using BTCPayServer.PayoutProcessors.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using LNURL;
using NBitcoin;
using Satora.Sdk;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using SdkLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;

namespace BTCPayServer.Plugins.Satora.Services;

public enum SatoraWithdrawResultKind
{
    // Refused before anything was recorded.
    Refused,
    // The review was submitted before; nothing new was done.
    Replayed,
    Sent,
    // A Lightning payment on its way; SatoraOutgoingWorker records how it ends.
    Pending,
    Unknown,
    Failed
}

public sealed record SatoraWithdrawResult(SatoraWithdrawResultKind Kind, string Message);

// A Lightning address's or LNURL's endpoint that did not answer: the payee's to fix, so a review tells
// the store without logging it as a failure of the plugin.
public sealed class SatoraPayeeException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);

// Manual withdrawals from a store's wallet. Each attempt is recorded before any SDK call that could
// move funds, under its review's idempotency key, and runs with the store's spend lock held, so no
// two withdrawals of a store, nor a withdrawal and a seed replace, interleave.
public class SatoraWithdrawalService(
    SatoraWalletAccess wallets,
    SatoraWithdrawals withdrawals,
    SatoraLightningPayments payments,
    SatoraWalletRenewals renewals,
    SatoraOutgoingSwaps outgoing,
    SatoraWalletRegistry registry,
    BTCPayNetworkProvider networkProvider,
    PayoutProcessorService payoutProcessors,
    SatoraNotifier notifier,
    IHttpClientFactory httpClientFactory,
    ILogger<SatoraWithdrawalService> logger)
{
    private const int HistoryLimit = 50;
    // For each request to a Lightning address's or LNURL's endpoint, as BTCPay's Lightning payout
    // handler allows an LNURL lookup; the store's spend lock is held meanwhile.
    protected virtual TimeSpan LnurlTimeout => TimeSpan.FromSeconds(30);
    public const string UnresolvedMessage =
        "A withdrawal's outcome is unknown. Check the wallet's balance and mark it sent or failed before withdrawing again.";
    public const string ExpiringFundsMessage = "Some of the wallet's funds are about to expire. They are being renewed. Try again in a minute.";
    public const string CloseConfirmationWord = "CLOSE";
    // arkd's dust (330 sats today): the least recoverable total ark-client settles.
    public const long DustSats = 330;

    // Withdrawals whose request is running in this process, with their store. BTCPay runs as one
    // process, so after a restart none is, whatever its row says.
    private readonly ConcurrentDictionary<string, string> _running = new();

    public virtual string? Unavailable => registry.NetworkError;

    protected virtual Network? Network =>
        Unavailable is null ? networkProvider.GetNetwork<BTCPayNetwork>(SatoraLightningCredit.CryptoCode)?.NBitcoinNetwork : null;

    public async Task<SatoraWithdrawPageModel> GetPageAsync(string storeId)
    {
        var now = DateTimeOffset.UtcNow;
        var unfinished = (await outgoing.ListOpenAsync(storeId, CancellationToken.None)).Where(s => SatoraOutgoingActions.OwnerCanClose(s, now)).ToList();
        var held = await Task.WhenAll(unfinished.Select(async swap => (swap.SwapId, Amounts: await TryReadHeldAsync(storeId, swap.SwapId))));
        return new SatoraWithdrawPageModel
        {
            StoreId = storeId,
            Unavailable = Unavailable,
            Withdrawals = await withdrawals.ListAsync(storeId, HistoryLimit),
            Unresolved = await withdrawals.ListUnresolvedAsync(storeId),
            Running = _running.Keys.ToHashSet(),
            Unfinished = unfinished,
            UnfinishedHeld = held.ToDictionary(h => h.SwapId, h => h.Amounts),
            AutomatedLightningPayouts = await HasAutomatedLightningPayoutsAsync(storeId),
            Now = now
        };
    }

    // Unfinished payments pile up while arkd or its indexer is unreachable, which is when the store
    // needs the page to close them, and each read may then wait for the SDK's network timeout.
    private async Task<VhtlcAmounts?> TryReadHeldAsync(string storeId, string swapId)
    {
        try
        {
            return await ReadAsync().WaitAsync(HeldReadTimeout);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read what the VHTLC of Lightning payment {SwapId} of store {StoreId} holds", swapId, storeId);
            return null;
        }

        async Task<VhtlcAmounts> ReadAsync() => await (await wallets.GetAsync(storeId)).AmountsForSwapAsync(swapId);
    }

    protected virtual TimeSpan HeldReadTimeout => TimeSpan.FromSeconds(10);

    // Once enabled, BTCPay's Lightning payout processor pays approved payouts from the wallet
    // without anyone opening this plugin.
    public virtual async Task<bool> HasAutomatedLightningPayoutsAsync(string storeId) =>
        (await payoutProcessors.GetProcessors(new PayoutProcessorService.PayoutProcessorQuery
        {
            Stores = [storeId],
            Processors = [LightningAutomatedPayoutSenderFactory.ProcessorName],
            PayoutMethods = [PayoutTypes.LN.GetPayoutMethodId(SatoraLightningCredit.CryptoCode)]
        })).Count > 0;

    public async Task<SatoraWithdrawReview> ReviewAsync(string storeId, SatoraWithdrawRequest request)
    {
        var review = new SatoraWithdrawReview { Request = request };
        if (Network is not { } network)
            return Refuse(review, Unavailable ?? "Satora is not available on this server's network.");
        if ((await withdrawals.ListUnresolvedAsync(storeId)).Count > 0)
            return Refuse(review, UnresolvedMessage);
        var target = SatoraWithdrawDestinations.Detect(request.Destination, network, out var error);
        if (target is null)
            return Refuse(review, error!);
        review.Type = target.Type;
        review.Destination = target.Destination;
        error = SatoraWithdrawDestinations.Amount(target, request.AmountBtc, request.SendAll, out var amountSats);
        if (error is not null)
            return Refuse(review, error);
        try
        {
            var wallet = await wallets.GetAsync(storeId);
            var balance = await wallet.GetBalanceAsync();
            review.RecoverableSats = (long)balance.recoverableSats;
            review.AvailableSats = Spendable(balance);
            if (amountSats > review.AvailableSats)
                return Refuse(review, CannotCover(amountSats.Value, review));
            switch (target.Type)
            {
                case SatoraWithdrawalType.Arkade:
                    review.AmountSats = amountSats ?? review.AvailableSats;
                    review.TotalSats = review.AmountSats.Value;
                    break;
                case SatoraWithdrawalType.Bitcoin:
                    var offboard = await wallet.GetOffboardQuoteAsync(target.Destination, (ulong?)amountSats);
                    review.AmountSats = (long)offboard.AmountSats;
                    review.FeeSats = (long)offboard.FeeSats;
                    review.TotalSats = review.AmountSats.Value + review.FeeSats;
                    break;
                default:
                    var sats = amountSats;
                    if (sats is null)
                    {
                        var most = await SendMaxAsync(wallet, review.AvailableSats);
                        if (most.Sats < most.MinSats || most.Sats > most.MaxSats)
                            return Refuse(review, LightningRange(most.MinSats, most.MaxSats, most.Sats));
                        sats = most.Sats;
                    }
                    var plan = await PlanLightningAsync(wallet, target, sats.Value, amountSats is null ? review.AvailableSats : null, network);
                    var quote = plan.Quote;
                    if (quote.TargetAmountSats < quote.MinAmountSats || quote.TargetAmountSats > quote.MaxAmountSats)
                        return Refuse(review, LightningRange((long)quote.MinAmountSats, (long)quote.MaxAmountSats, (long)quote.TargetAmountSats));
                    review.AmountSats = plan.AmountSats;
                    review.FeeSats = plan.FeeSats;
                    review.TotalSats = plan.TotalSats;
                    break;
            }
        }
        catch (Exception e) when (e is SatoraAmountRefusedException or SatoraPayeeException)
        {
            return Refuse(review, e.Message);
        }
        catch (Exception e) when (SatoraWalletRenewals.IsExpiringRefusal(e))
        {
            return Refuse(review, StartRenewal(storeId, e));
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Reviewing a withdrawal from the Satora wallet of store {StoreId} failed", storeId);
            return Refuse(review, e.Message);
        }
        if (review.AmountSats <= 0)
            return Refuse(review, "There is nothing to send.");
        if (review.TotalSats > review.AvailableSats)
            return Refuse(review, CannotCover(review.TotalSats, review));
        review.Key = Guid.NewGuid().ToString("N");
        return review;
    }

    public async Task<SatoraWithdrawResult> ExecuteAsync(string storeId, SatoraWithdrawRequest request)
    {
        if (Network is not { } network)
            return Result(SatoraWithdrawResultKind.Refused, Unavailable ?? "Satora is not available on this server's network.");
        if (request.Key is not { Length: > 0 and <= 64 } key || !key.All(char.IsAsciiLetterOrDigit))
            return Result(SatoraWithdrawResultKind.Refused, "Review the withdrawal before confirming it.");
        var target = SatoraWithdrawDestinations.Detect(request.Destination, network, out var error);
        if (target is null)
            return Result(SatoraWithdrawResultKind.Refused, error!);
        error = SatoraWithdrawDestinations.Amount(target, request.AmountBtc, request.SendAll, out var amount);
        if (error is not null)
            return Result(SatoraWithdrawResultKind.Refused, error);
        // Send everything takes what the wallet can spend when it runs, which may be more than reviewed.
        var maxTotal = amount is null ? null : request.MaxTotalSats;
        // An exit's or a Lightning payment's fees are quoted again when it runs, and are held to the review's.
        if (target.Type != SatoraWithdrawalType.Arkade && (amount is null ? request.MaxFeeSats : maxTotal) is null)
            return Result(SatoraWithdrawResultKind.Refused, "Review the withdrawal before confirming it.");
        try
        {
            return await wallets.SpendAsync(storeId, wallet =>
                ExecuteGatedAsync(wallet, storeId, key, target, amount, maxTotal, request.MaxFeeSats, network));
        }
        catch (Exception e)
        {
            // Opening the wallet, or recording an outcome after the SDK call, failed: what was sent is
            // only known from the withdrawal's row, if one was written.
            logger.LogError(e, "Withdrawal {WithdrawalId} from the Satora wallet of store {StoreId} stopped with an error", key, storeId);
            return Result(SatoraWithdrawResultKind.Unknown,
                $"The withdrawal stopped with an error ({e.Message}). Check the withdrawals below and the wallet's balance before trying again.");
        }
    }

    public Task<bool> ResolveAsync(string storeId, string id, bool sent) =>
        withdrawals.ResolveAsync(storeId, id, sent, _running.ContainsKey(id), DateTimeOffset.UtcNow);

    // The store closes a Lightning payment the worker cannot finish, once the wallet's balance and the
    // payee have shown whether its invoice was paid. A withdrawal still running it decides first. The
    // funds are not back while the swap's VHTLC holds sats the worker can still refund, so the store's
    // word that they are is refused then, and while what the VHTLC holds cannot be read. The close is
    // decided with the store's spend lock held, as every funding is: none lands between the checks and
    // the close, and none starts on a swap the store has closed.
    public async Task<(bool Closed, string Message)> CloseSwapAsync(string storeId, string swapId, bool paid)
    {
        const string cannotClose = "That payment cannot be closed. It is already finished or still in progress.";
        // Refused at once rather than after waiting for the spend lock, which the withdrawal holds.
        if (await withdrawals.FindBySwapIdAsync(storeId, swapId) is { } withdrawal && _running.ContainsKey(withdrawal.Id))
            return (false, cannotClose);
        using var wait = new CancellationTokenSource(CloseWait);
        try
        {
            return await wallets.SpendAsync<(bool, string)>(storeId, async wallet =>
            {
                var now = DateTimeOffset.UtcNow;
                var swap = (await outgoing.ListOpenAsync(storeId, CancellationToken.None)).FirstOrDefault(s => s.SwapId == swapId);
                if (swap is null || !SatoraOutgoingActions.OwnerCanClose(swap, now))
                    return (false, cannotClose);
                if (!paid && swap.RefundTxId is null && await StillHeldAsync(wallet, storeId, swap) is { } held)
                    return (false, held);
                if (!await withdrawals.CloseSwapAsync(storeId, swapId, paid, now))
                    return (false, cannotClose);
                return (true, paid ? "Payment closed as paid." : "Payment closed as failed.");
            }, wait.Token);
        }
        catch (OperationCanceledException) when (wait.IsCancellationRequested)
        {
            return (false, "The wallet is busy with a withdrawal or a payment. Try again once it has finished.");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Closing Lightning payment {SwapId} of store {StoreId} failed", swapId, storeId);
            return (false, $"The payment could not be closed ({e.Message}). Try again.");
        }
    }

    // How long the store's close waits for the spend lock, which an exit can hold for many minutes.
    protected virtual TimeSpan CloseWait => TimeSpan.FromSeconds(30);

    // Why the funds are not back yet, or null. A swap missing from the SDK's storage can never be
    // refunded either, so it does not hold the close up. Other InvalidSwap refusals, such as for an
    // Arkade server or VTXO that does not match the swap, come from arkd or its indexer and can clear.
    private async Task<string?> StillHeldAsync(ISatoraWallet wallet, string storeId, SatoraLightningSwap swap)
    {
        VhtlcAmounts amounts;
        try
        {
            amounts = await wallet.AmountsForSwapAsync(swap.SwapId).WaitAsync(HeldReadTimeout);
        }
        catch (SdkException e) when (IsSwapRecordMissing(e))
        {
            logger.LogWarning(e, "The SDK has no record of Lightning payment {SwapId} of store {StoreId}; letting the store close it", swap.SwapId, storeId);
            return null;
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not read what the VHTLC of Lightning payment {SwapId} of store {StoreId} holds", swap.SwapId, storeId);
            return $"The amount still held in the swap could not be read ({e.Message}). The payment was not closed. Try again once this page shows the amount.";
        }
        var held = amounts.SpendableSats + amounts.RecoverableSats;
        return held == 0
            ? null
            : $"The swap still holds {held} sats, so the funds are not back in the wallet. Leave the payment open. They return to the wallet automatically after the refund locktime, unless the swap server pays the invoice first.";
    }

    // Settles the wallet's recoverable funds, which no withdrawal spends, under the gates a withdrawal
    // takes.
    public async Task<(bool Success, string Message)> SettleAsync(string storeId)
    {
        if (Network is null)
            return (false, Unavailable ?? "Satora is not available on this server's network.");
        if (_running.Values.Contains(storeId))
            return (false, "A withdrawal is still running. Settle once it has finished.");
        try
        {
            return await wallets.SpendAsync<(bool, string)>(storeId, async wallet =>
            {
                if ((await withdrawals.ListUnresolvedAsync(storeId)).Count > 0)
                    return (false, UnresolvedMessage);
                if (await wallet.SettleArkadeAsync() is not { } batch)
                    return (true, "The wallet had no recoverable funds to settle.");
                logger.LogInformation("Settled the recoverable funds of the Satora wallet of store {StoreId} in Arkade batch {TxId}", storeId, batch);
                return (true, $"The wallet's recoverable funds were settled in Arkade batch {batch}.");
            });
        }
        catch (SdkException e) when (TooSmallToSettle(e))
        {
            logger.LogInformation("The recoverable funds of the Satora wallet of store {StoreId} are below dust, and are settled once more funds become recoverable ({Reason})",
                storeId, e.Message);
            return (false, "The recoverable funds are too small to settle on their own. They can be settled once more funds become recoverable.");
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Settling the recoverable funds of the Satora wallet of store {StoreId} failed", storeId);
            return (false, $"Settling the wallet's recoverable funds failed ({e.Message}). The balance shows whether the batch went through.");
        }
    }

    private async Task<SatoraWithdrawResult> ExecuteGatedAsync(ISatoraWallet wallet, string storeId, string key, SatoraWithdrawTarget target,
        long? amountSats, long? maxTotalSats, long? maxFeeSats, Network network)
    {
        if (await withdrawals.FindAsync(storeId, key) is not null)
            return Result(SatoraWithdrawResultKind.Replayed, "This withdrawal was already submitted; see its status below.");
        if ((await withdrawals.ListUnresolvedAsync(storeId)).Count > 0)
            return Result(SatoraWithdrawResultKind.Refused, UnresolvedMessage);
        var now = DateTimeOffset.UtcNow;
        var withdrawal = new SatoraWithdrawal
        {
            Id = key,
            StoreId = storeId,
            Type = target.Type,
            Destination = target.Destination,
            AmountSats = amountSats,
            Status = SatoraWithdrawalStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (!await withdrawals.TryAddAsync(withdrawal))
            return Result(SatoraWithdrawResultKind.Replayed, "This withdrawal was already submitted.");
        _running.TryAdd(key, storeId);
        try
        {
            if (target.Type == SatoraWithdrawalType.Bitcoin)
                return await OffboardAsync(wallet, withdrawal, amountSats, maxTotalSats, maxFeeSats);
            long spendable;
            try
            {
                spendable = Spendable(await wallet.GetBalanceAsync());
            }
            catch (Exception e)
            {
                return await FailAsync(withdrawal, e.Message, e);
            }
            return target.Type == SatoraWithdrawalType.Arkade
                ? await SendArkadeAsync(wallet, withdrawal, amountSats ?? spendable, spendable)
                : await PayLightningAsync(wallet, withdrawal, target, amountSats, spendable, maxTotalSats, maxFeeSats, network);
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    // What sends, swap fundings and exits spend. Recoverable sats move only when the owner settles
    // them: ark-client counts VTXOs below dust as recoverable and refuses to settle a total below
    // dust, so a withdrawal that settled first could be blocked for good by leftover change.
    private static long Spendable(ArkadeBalance balance) => (long)(balance.confirmedSats + balance.preConfirmedSats);

    private async Task<SatoraWithdrawResult> SendArkadeAsync(ISatoraWallet wallet, SatoraWithdrawal withdrawal, long sats, long spendable)
    {
        if (sats <= 0 || sats > spendable)
            return await FailAsync(withdrawal, $"The wallet can spend {spendable} sats, so {sats} sats were not sent.", null);
        try
        {
            await SetAmountsAsync(withdrawal, sats, 0);
        }
        catch (Exception e)
        {
            return await FailAsync(withdrawal, e.Message, e);
        }
        string txid;
        try
        {
            txid = await wallet.SendArkadeAsync(withdrawal.Destination, (ulong)sats);
        }
        catch (Exception e)
        {
            return await SendFailedAsync(withdrawal, e);
        }
        return await SentAsync(withdrawal, txid);
    }

    private async Task<SatoraWithdrawResult> OffboardAsync(ISatoraWallet wallet, SatoraWithdrawal withdrawal, long? amountSats,
        long? maxTotalSats, long? maxFeeSats)
    {
        OffboardQuote quote;
        try
        {
            quote = await wallet.GetOffboardQuoteAsync(withdrawal.Destination, (ulong?)amountSats);
            await SetAmountsAsync(withdrawal, (long)quote.AmountSats, (long)quote.FeeSats);
        }
        catch (Exception e)
        {
            return await FailAsync(withdrawal, e.Message, e);
        }
        var total = (long)(quote.AmountSats + quote.FeeSats);
        if (amountSats is not null && total > maxTotalSats)
            return await FailAsync(withdrawal, $"The on-chain withdrawal now takes {total} sats from the wallet, more than the {maxTotalSats} sats reviewed.", null);
        if (amountSats is null && (long)quote.FeeSats > maxFeeSats)
            return await FailAsync(withdrawal, $"The on-chain fee is now {quote.FeeSats} sats, more than the {maxFeeSats} sats reviewed.", null);
        string txid;
        try
        {
            txid = await wallet.OffboardAsync(withdrawal.Destination, (ulong?)amountSats);
        }
        catch (Exception e)
        {
            return await SendFailedAsync(withdrawal, e);
        }
        return await SentAsync(withdrawal, txid);
    }

    // The swap is recorded with the withdrawal; from its funding attempt on, SatoraOutgoingWorker
    // finishes the withdrawal as it finishes any outgoing payment.
    private async Task<SatoraWithdrawResult> PayLightningAsync(ISatoraWallet wallet, SatoraWithdrawal withdrawal, SatoraWithdrawTarget target,
        long? amountSats, long spendable, long? maxTotalSats, long? maxFeeSats, Network network)
    {
        SatoraLightningSwap swap;
        long receive, fees;
        try
        {
            var (bolt11, paymentHash) = await InvoiceAsync(wallet, target, amountSats, spendable, network);
            swap = await UnfundedSwapAsync(withdrawal, target, paymentHash)
                   ?? await payments.CreateAsync(wallet, withdrawal.StoreId, new SdkLightningDestination.Invoice(bolt11), network, paymentHash,
                       created => withdrawals.AttachSwapAsync(withdrawal.Id, created));
            receive = swap.AmountMsat / 1000;
            fees = swap.FundingSats!.Value - receive;
            await SetAmountsAsync(withdrawal, receive, fees);
            await SatoraLightningPayments.CheckFundingAsync(wallet, swap, maxTotalSats);
        }
        catch (Exception e)
        {
            return await FailAsync(withdrawal, e.Message, e);
        }
        if (amountSats is null && fees > maxFeeSats)
            return await FailAsync(withdrawal, $"The Lightning payment's fees are now {fees} sats, more than the {maxFeeSats} sats reviewed.", null);
        if (target.Type == SatoraWithdrawalType.LightningInvoice)
            logger.LogInformation("{Withdrawal} {WithdrawalId} from the Satora wallet of store {StoreId} pays {AmountSats} sats with fees of {FeeSats} sats to the invoice with payment hash {PaymentHash} through Satora swap {SwapId}",
                Describe(withdrawal), withdrawal.Id, withdrawal.StoreId, receive, fees, swap.PaymentHash, swap.SwapId);
        else
            // The only record of the invoice the payee's endpoint issued, besides the swap's row.
            logger.LogInformation("{Withdrawal} {WithdrawalId} from the Satora wallet of store {StoreId} pays {AmountSats} sats with fees of {FeeSats} sats to the invoice with payment hash {PaymentHash}, which {Destination} issued, through Satora swap {SwapId}",
                Describe(withdrawal), withdrawal.Id, withdrawal.StoreId, receive, fees, swap.PaymentHash, withdrawal.Destination, swap.SwapId);
        try
        {
            await payments.FundAsync(wallet, swap);
            if (swap.FundingTxId is { } txid)
                await withdrawals.SetOutcomeAsync(withdrawal.Id, SatoraWithdrawalStatus.Pending, txid, null);
        }
        catch (SatoraSwapClosedException e)
        {
            return await FailAsync(withdrawal, e.Message, e);
        }
        catch (SatoraFundsExpiringException e)
        {
            return await FailAsync(withdrawal, e.Message, e.InnerException);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Lightning withdrawal {WithdrawalId} of store {StoreId} is left to the worker", withdrawal.Id, withdrawal.StoreId);
        }
        return Result(SatoraWithdrawResultKind.Pending,
            "Lightning payment started. Its status below changes to sent once the invoice is paid, or to failed if the funds return to the wallet.");
    }

    // The swap server and the swaps table take one swap per invoice, so a Lightning invoice paid again
    // funds the swap an earlier payment recorded, as BTCPay's retry of a payout does: one whose first
    // funding the SDK refused for funds about to expire, for example. Only a swap not marked attempted,
    // which nothing can have paid, that still awaits funding for an invoice not yet expired, is taken
    // over, and only from a withdrawal that failed. Any other is refused. Null when the invoice has no
    // swap yet.
    private async Task<SatoraLightningSwap?> UnfundedSwapAsync(SatoraWithdrawal withdrawal, SatoraWithdrawTarget target, string paymentHash)
    {
        if (target.Type != SatoraWithdrawalType.LightningInvoice ||
            await outgoing.FindByPaymentHashAsync(withdrawal.StoreId, paymentHash) is not { } swap)
            return null;
        var used = $"An earlier payment from this wallet already used this invoice (Satora swap {swap.SwapId}). Ask the payee for a new invoice if it was not paid.";
        if (swap is not { FundingAttempted: false, Status: SatoraSwapStatus.Pending, CompletedAt: null })
            throw new InvalidOperationException(used);
        if (swap.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new InvalidOperationException("The invoice has expired.");
        if (!await withdrawals.MoveSwapAsync(withdrawal.Id, swap.SwapId))
            throw new InvalidOperationException(used);
        logger.LogInformation("Lightning invoice withdrawal {WithdrawalId} of store {StoreId} takes over Satora swap {SwapId}, which an earlier payment of the same invoice recorded without funding it",
            withdrawal.Id, withdrawal.StoreId, swap.SwapId);
        return swap;
    }

    // The invoice a manual withdrawal's swap pays.
    private async Task<(string Bolt11, string PaymentHash)> InvoiceAsync(ISatoraWallet wallet, SatoraWithdrawTarget target, long? amountSats,
        long spendable, Network network)
    {
        if (target.Type == SatoraWithdrawalType.LightningInvoice)
            return (target.Destination, target.PaymentHash!);
        if (amountSats is { } sats)
        {
            using var lookup = new CancellationTokenSource(LnurlTimeout);
            var invoice = await InvoiceFromPayeeAsync(target, await PayRequestAsync(target, lookup.Token), sats, network, lookup.Token);
            return (invoice.Bolt11, invoice.PaymentHash);
        }
        var most = await SendMaxAsync(wallet, spendable);
        if (most.Sats < most.MinSats || most.Sats > most.MaxSats)
            throw new InvalidOperationException(LightningRange(most.MinSats, most.MaxSats, most.Sats));
        var plan = await PlanLightningAsync(wallet, target, most.Sats, spendable, network);
        return (plan.Bolt11, plan.PaymentHash);
    }

    // An invoice to pay, with the swap server's quote for paying exactly it.
    private sealed record LightningPlan(string Bolt11, string PaymentHash, long AmountSats, LightningSendQuote Quote)
    {
        public long FeeSats => (long)(Quote.NetworkFeeSats + Quote.ProtocolFeeSats);

        public long TotalSats => long.TryParse(Quote.SourceAmount, out var source) ? source : (long)Quote.SourceAmountSats;
    }

    // What sending everything up to budgetSats pays over Lightning, from the swap server's amount-only
    // quote: it is not told the destination, so the Lightning fee in it is the server's estimate.
    private static async Task<(long Sats, long MinSats, long MaxSats)> SendMaxAsync(ISatoraWallet wallet, long budgetSats)
    {
        var quote = await wallet.GetLightningSendMaxQuoteAsync((ulong)budgetSats);
        return (long.TryParse(quote.NetTargetAmount, out var sats) ? sats : 0, (long)quote.MinAmount, (long)quote.MaxAmount);
    }

    // The invoice paying sats and the swap server's quote for it. For a Lightning address or LNURL the
    // swap server sees only the invoice its endpoint issued. With budgetSats, an invoice whose fees take
    // the total past it, as when an amount-only quote underestimated the Lightning fee, is asked for
    // once more, lower by the difference. One LnurlTimeout covers every request to the payee.
    private async Task<LightningPlan> PlanLightningAsync(ISatoraWallet wallet, SatoraWithdrawTarget target, long sats, long? budgetSats,
        Network network)
    {
        if (target.Type == SatoraWithdrawalType.LightningInvoice)
            return await QuoteAsync(target.Destination, target.PaymentHash!, sats);
        using var lookup = new CancellationTokenSource(LnurlTimeout);
        var payRequest = await PayRequestAsync(target, lookup.Token);
        var plan = await ResolveAsync(sats);
        if (budgetSats is not { } budget || plan.TotalSats <= budget)
            return plan;
        var lower = plan.AmountSats - (plan.TotalSats - budget);
        if (lower > 0)
            plan = await ResolveAsync(lower);
        return plan.TotalSats <= budget
            ? plan
            : throw new InvalidOperationException(
                $"Paying the {SatoraWithdrawDestinations.Display(target.Type)} takes {plan.TotalSats} sats with fees, more than the {budget} sats the wallet can send.");

        async Task<LightningPlan> ResolveAsync(long amount)
        {
            var invoice = await InvoiceFromPayeeAsync(target, payRequest, amount, network, lookup.Token);
            return await QuoteAsync(invoice.Bolt11, invoice.PaymentHash, invoice.Sats);
        }

        async Task<LightningPlan> QuoteAsync(string bolt11, string paymentHash, long amount) =>
            new(bolt11, paymentHash, amount, await SatoraSwapLimits.CheckAsync(
                () => wallet.GetLightningSendQuoteAsync(new SdkLightningDestination.Invoice(bolt11)),
                limit => $"Satora pays Lightning amounts of {limit}; this one is {amount} sats."));
    }

    // A Lightning address or LNURL is resolved here, as BTCPay's own Lightning payouts resolve one,
    // never by the swap server: the swap is then bound to the invoice the payee's endpoint issued, so a
    // swap server cannot put an invoice of its own in its place, and the payee is not disclosed to it.
    private async Task<LNURLPayRequest> PayRequestAsync(SatoraWithdrawTarget target, CancellationToken lookup)
    {
        var endpoint = target.Type == SatoraWithdrawalType.LightningAddress
            ? LNURL.LNURL.ExtractUriFromInternetIdentifier(target.Destination)
            : LNURL.LNURL.Parse(target.Destination, out _);
        return await AskPayeeAsync(target, lookup, cancellationToken => FetchPayRequestAsync(endpoint, cancellationToken));
    }

    // An invoice for sats from the payee's endpoint.
    private async Task<(string Bolt11, string PaymentHash, long Sats)> InvoiceFromPayeeAsync(SatoraWithdrawTarget target, LNURLPayRequest payRequest,
        long sats, Network network, CancellationToken lookup)
    {
        var amount = LightMoney.Satoshis(sats);
        if (amount < payRequest.MinSendable || amount > payRequest.MaxSendable)
            throw new InvalidOperationException(
                $"The {SatoraWithdrawDestinations.Display(target.Type)} takes from {Sats(payRequest.MinSendable, up: true)} to {Sats(payRequest.MaxSendable, up: false)} sats, not {sats} sats.");
        var bolt11 = await AskPayeeAsync(target, lookup, cancellationToken => RequestInvoiceAsync(payRequest, amount, network, cancellationToken));
        if (!BOLT11PaymentRequest.TryParse(bolt11, out var invoice, network) || invoice?.PaymentHash is not { } paymentHash || invoice.MinimumAmount != amount)
            throw new InvalidOperationException($"The {SatoraWithdrawDestinations.Display(target.Type)} returned an invoice that is not for {sats} sats on this network.");
        return (bolt11, paymentHash.ToString(), sats);
    }

    // lookup is cancelled LnurlTimeout after the lookup this request is part of began.
    private async Task<T> AskPayeeAsync<T>(SatoraWithdrawTarget target, CancellationToken lookup, Func<CancellationToken, Task<T>> ask)
    {
        try
        {
            return await ask(lookup);
        }
        catch (OperationCanceledException) when (lookup.IsCancellationRequested)
        {
            throw new SatoraPayeeException(
                $"The {SatoraWithdrawDestinations.Display(target.Type)} did not answer in time ({LnurlTimeout.TotalSeconds:0.#} seconds).");
        }
        // No status code: the connection itself failed, as for a refused connection, a name that does
        // not resolve, or a failed TLS or Tor connection.
        catch (HttpRequestException e) when (e.StatusCode is null)
        {
            throw new SatoraPayeeException(
                $"The {SatoraWithdrawDestinations.Display(target.Type)}'s server could not be reached: {e.Message.TrimEnd('.')}.", e);
        }
    }

    private static string LightningRange(long minSats, long maxSats, long sats) =>
        $"Satora pays Lightning amounts from {minSats} to {maxSats} sats; this one would be {sats} sats.";

    private static long Sats(LightMoney amount, bool up) => (amount.MilliSatoshi + (up ? 999 : 0)) / 1000;

    protected virtual async Task<LNURLPayRequest> FetchPayRequestAsync(Uri endpoint, CancellationToken cancellationToken) =>
        await LNURL.LNURL.FetchInformation(endpoint, "payRequest", HttpClient(endpoint), cancellationToken) as LNURLPayRequest
        ?? throw new InvalidOperationException("The destination is not an LNURL-pay endpoint.");

    // SendRequest refuses an invoice whose amount or description hash is not the one requested.
    protected virtual async Task<string> RequestInvoiceAsync(LNURLPayRequest payRequest, LightMoney amount, Network network,
        CancellationToken cancellationToken) =>
        (await payRequest.SendRequest(amount, network, HttpClient(payRequest.Callback), cancellationToken: cancellationToken)).Pr;

    // The clients BTCPay's own LNURL payouts use: an onion endpoint goes through its Tor proxy.
    private HttpClient HttpClient(Uri endpoint) =>
        httpClientFactory.CreateClient(endpoint.IsOnion()
            ? LightningLikePayoutHandler.LightningLikePayoutHandlerOnionNamedClient
            : LightningLikePayoutHandler.LightningLikePayoutHandlerClearnetNamedClient);

    private async Task<SatoraWithdrawResult> SentAsync(SatoraWithdrawal withdrawal, string txid)
    {
        logger.LogInformation("{Withdrawal} {WithdrawalId} from the Satora wallet of store {StoreId} sent {AmountSats} sats with a fee of {FeeSats} sats to {Destination} in {TxId}",
            Describe(withdrawal), withdrawal.Id, withdrawal.StoreId, withdrawal.AmountSats, withdrawal.FeeSats, withdrawal.Destination, txid);
        await withdrawals.SetOutcomeAsync(withdrawal.Id, SatoraWithdrawalStatus.Sent, txid, null);
        return Result(SatoraWithdrawResultKind.Sent, $"{SatoraWithdrawDestinations.Display(withdrawal.Type)} withdrawal sent.");
    }

    // The row and the copy whose amounts SentAsync logs.
    private async Task SetAmountsAsync(SatoraWithdrawal withdrawal, long amountSats, long feeSats)
    {
        await withdrawals.SetAmountsAsync(withdrawal.Id, amountSats, feeSats);
        (withdrawal.AmountSats, withdrawal.FeeSats) = (amountSats, feeSats);
    }

    private static string Describe(SatoraWithdrawal withdrawal) =>
        $"{SatoraWithdrawDestinations.Display(withdrawal.Type)} withdrawal";

    private async Task<SatoraWithdrawResult> FailAsync(SatoraWithdrawal withdrawal, string message, Exception? error)
    {
        if (SatoraWalletRenewals.IsExpiringRefusal(error))
            message = StartRenewal(withdrawal.StoreId, error!);
        else if (error is not null)
            logger.LogWarning(error, "Withdrawal {WithdrawalId} from the Satora wallet of store {StoreId} failed; nothing was sent", withdrawal.Id, withdrawal.StoreId);
        await withdrawals.SetOutcomeAsync(withdrawal.Id, SatoraWithdrawalStatus.Failed, null, message);
        return Result(SatoraWithdrawResultKind.Failed, $"Nothing was sent: {message}");
    }

    // The SDK refused a withdrawal the wallet cannot cover without its funds about to expire: they are
    // renewed, and the store is told to try again rather than shown the refusal.
    private string StartRenewal(string storeId, Exception refusal)
    {
        logger.LogInformation("A withdrawal from the Satora wallet of store {StoreId} was refused, as funds it needs are about to expire; renewing them ({Reason})",
            storeId, refusal.Message);
        renewals.Start(storeId);
        return ExpiringFundsMessage;
    }

    private async Task<SatoraWithdrawResult> SendFailedAsync(SatoraWithdrawal withdrawal, Exception error)
    {
        if (SentNothing(error))
            return await FailAsync(withdrawal, error.Message, error);
        logger.LogError(error, "Withdrawal {WithdrawalId} from the Satora wallet of store {StoreId} has an unknown outcome", withdrawal.Id, withdrawal.StoreId);
        await withdrawals.SetOutcomeAsync(withdrawal.Id, SatoraWithdrawalStatus.Unknown, null, error.Message);
        await NotifyUnknownAsync(withdrawal);
        return Result(SatoraWithdrawResultKind.Unknown,
            "The withdrawal's outcome is unknown. Check the wallet's balance and mark it sent or failed below. No new withdrawal can start until then.");
    }

    // Errors the SDK raises before a send or an offboard leaves the wallet. Any other error,
    // Transport, Timeout and Storage first, may come after it did.
    public static bool SentNothing(Exception error) => error is SdkException
    {
        Kind: SdkErrorKind.Api or SdkErrorKind.InvalidSwap or SdkErrorKind.InvalidSigner or SdkErrorKind.Decode or
        SdkErrorKind.UnsupportedQuotePath or SdkErrorKind.InvalidBaseUrl
    };

    protected virtual async Task NotifyUnknownAsync(SatoraWithdrawal withdrawal)
    {
        var notification = new SatoraWithdrawalNotification { StoreId = withdrawal.StoreId, WithdrawalId = withdrawal.Id, Destination = withdrawal.Destination };
        try
        {
            await notifier.SendAsync(withdrawal.StoreId, notification);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Could not send the Satora withdrawal notification for store {StoreId}", withdrawal.StoreId);
        }
    }

    private static string CannotCover(long sats, SatoraWithdrawReview review) =>
        $"The withdrawal takes {sats} sats, but the wallet can spend {review.AvailableSats} sats." + review.RecoverableSats switch
        {
            <= 0 => "",
            < DustSats => $" Another {review.RecoverableSats} sats are recoverable but too small to settle yet.",
            _ => $" Another {review.RecoverableSats} sats are recoverable. Settle them, then review the withdrawal again."
        };

    // ark-client refuses, before joining a batch, to settle recoverable funds whose total is below
    // dust, and the SDK passes that on as a Transport error.
    private static bool TooSmallToSettle(SdkException error) =>
        error.Kind == SdkErrorKind.Transport && error.Message.Contains("cannot settle into sub-dust VTXO", StringComparison.Ordinal);

    // The SDK reads a VHTLC only for a swap in its storage (arkade_htlc), and refuses any other with
    // an InvalidSwap that only its text tells apart.
    private static bool IsSwapRecordMissing(SdkException error) =>
        error.Kind == SdkErrorKind.InvalidSwap && error.Message.Contains("swap record missing", StringComparison.Ordinal);

    private static SatoraWithdrawReview Refuse(SatoraWithdrawReview review, string error)
    {
        review.Error = error;
        review.Key = null;
        return review;
    }

    private static SatoraWithdrawResult Result(SatoraWithdrawResultKind kind, string message) => new(kind, message);
}
