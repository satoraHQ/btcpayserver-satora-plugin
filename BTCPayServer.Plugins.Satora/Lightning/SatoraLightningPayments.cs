using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using NBitcoin;
using Satora.Sdk;
using SdkLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;

namespace BTCPayServer.Plugins.Satora.Lightning;

// Lightning payments out of a store's wallet: each is an Arkade to Lightning swap whose VHTLC the
// wallet funds. Once funding was attempted the wallet's sats may be in the VHTLC whatever the call
// reported, so from then on only SatoraOutgoingWorker decides the outcome: a payment is reported
// failed only once the swap server will not pay the invoice, and the worker refunds it.
public class SatoraLightningPayments(
    SatoraOutgoingSwaps swaps,
    SatoraWalletAccess wallets,
    ILogger<SatoraLightningPayments> logger)
{
    private const int ListLimit = 100;

    // Lets a test pay a real invoice that has since expired.
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.UtcNow;

    // ILightningClient.Pay for the store's wallet. Idempotent per payment hash: BTCPay retries a
    // payout after an error, and a fixed invoice brings it back here.
    public async Task<PayResponse> PayAsync(string storeId, Func<Task> ensureCurrent, string? bolt11, PayInvoiceParams? payParams,
        Network network, CancellationToken cancellation)
    {
        var fundingAttempted = false;
        try
        {
            var invoice = ParseInvoice(bolt11, network, out var refusal);
            if (invoice is null)
                return new PayResponse(PayResult.Error, refusal);
            var paymentHash = invoice.PaymentHash!.ToString();
            // Reported before the invoice's own refusals and the key and enabled checks: a payment
            // retried after its invoice expired, a disable or a key rotation must still hear that it
            // may have gone out.
            if (await swaps.FindByPaymentHashAsync(storeId, paymentHash) is { FundingAttempted: true } paid)
                return Report(paid);
            if (Refusal(invoice, payParams, Clock()) is { } refused)
                return new PayResponse(PayResult.Error, refused);
            await ensureCurrent();
            return await wallets.SpendAsync(storeId, async wallet =>
            {
                // A key rotation or a disable may have finished while this waited for the wallet.
                await ensureCurrent();
                var swap = await swaps.FindByPaymentHashAsync(storeId, paymentHash);
                if (swap is { FundingAttempted: true })
                    return Report(swap);
                if (swap is not null && (swap.Status != SatoraSwapStatus.Pending || swap.CompletedAt is not null))
                    return new PayResponse(PayResult.Error,
                        $"Satora swap {swap.SwapId} for this invoice is {(swap.CompletedAt is null ? swap.Status.ToString() : "closed")} and can no longer be paid.");
                swap ??= await CreateAsync(wallet, storeId, new SdkLightningDestination.Invoice(bolt11!), network, paymentHash, swaps.AddAsync);
                await CheckFundingAsync(wallet, swap, MaxFundingSats(invoice.MinimumAmount, payParams));
                // Set before the write: a write that fails may still have committed, and the worker
                // would then fund the swap while BTCPay retried the payout with a fresh invoice.
                fundingAttempted = true;
                await FundAsync(wallet, swap);
                return Report(swap);
            }, cancellation);
        }
        catch (Exception e) when (!fundingAttempted || e is SatoraSwapClosedException)
        {
            logger.LogWarning(e, "Paying a Lightning invoice from the Satora wallet of store {StoreId} failed before any funds moved", storeId);
            return new PayResponse(PayResult.Error, e.Message);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Paying a Lightning invoice from the Satora wallet of store {StoreId} has an unknown outcome; the worker resolves it", storeId);
            return new PayResponse(PayResult.Unknown, new PayDetails { Status = LightningPaymentStatus.Pending });
        }
    }

    public async Task<LightningPayment?> GetPaymentAsync(string storeId, string paymentHash) =>
        await swaps.FindByPaymentHashAsync(storeId, paymentHash) is { } swap ? SatoraOutgoingActions.ToPayment(swap) : null;

    public async Task<LightningPayment[]> ListPaymentsAsync(string storeId, ListPaymentsParams? request) =>
        (await swaps.ListAsync(storeId, request?.IncludePending is true, (int)Math.Max(0, request?.OffsetIndex ?? 0), ListLimit))
        .Select(SatoraOutgoingActions.ToPayment)
        .ToArray();

    // Inside a SpendAsync action, so the row a seed replace counts is written before the action returns.
    public async Task<SatoraLightningSwap> CreateAsync(ISatoraWallet wallet, string storeId, SdkLightningDestination destination,
        Network network, string expectedPaymentHash, Func<SatoraLightningSwap, Task> persist)
    {
        var created = await SatoraSwapLimits.CheckAsync(() => wallet.CreateArkadeToLightningSwapAsync(destination),
            limit => $"Satora pays Lightning amounts of {limit}.");
        var swap = ToRow(created, storeId, network, expectedPaymentHash, DateTimeOffset.UtcNow);
        await persist(swap);
        return swap;
    }

    // Refuses before anything is recorded as attempted: a swap the wallet cannot fund, or one whose
    // fees went past what the caller accepts.
    public static async Task CheckFundingAsync(ISatoraWallet wallet, SatoraLightningSwap swap, long? maxFundingSats)
    {
        var funding = swap.FundingSats!.Value;
        if (maxFundingSats is { } max && funding > max)
            throw new InvalidOperationException($"The swap takes {funding} sats from the wallet, more than the {max} sats accepted for this payment.");
        var balance = await wallet.GetBalanceAsync();
        var spendable = balance.confirmedSats + balance.preConfirmedSats;
        if ((ulong)funding > spendable)
            throw new InvalidOperationException($"The swap takes {funding} sats, but the wallet can spend {spendable} sats." + balance.recoverableSats switch
            {
                0 => "",
                < SatoraWithdrawalService.DustSats => $" Another {balance.recoverableSats} sats are recoverable but too small to settle yet.",
                _ => $" Another {balance.recoverableSats} sats are recoverable. Settle them on Satora's withdrawals page, then try again."
            });
    }

    // Inside a SpendAsync action. Records the attempt, then funds. A failed attempt is left to the
    // worker, which calls FundArkadeSwapAsync again: the SDK resolves it from its journal. A swap the
    // worker closed since it was read is refused before anything is attempted.
    public async Task FundAsync(ISatoraWallet wallet, SatoraLightningSwap swap)
    {
        if (!await swaps.SetFundingAttemptedAsync(swap.SwapId))
            throw new SatoraSwapClosedException(swap.SwapId);
        swap.FundingAttempted = true;
        try
        {
            swap.FundingTxId = await wallet.FundArkadeSwapAsync(swap.SwapId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Funding Satora Lightning swap {SwapId} of store {StoreId} has an unknown outcome; the worker resolves it", swap.SwapId, swap.StoreId);
            return;
        }
        logger.LogInformation("Funded Satora Lightning swap {SwapId} of store {StoreId} in {TxId}", swap.SwapId, swap.StoreId, swap.FundingTxId);
        try
        {
            await swaps.SetFundedAsync(swap.SwapId, swap.FundingTxId);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Satora Lightning swap {SwapId} of store {StoreId} was funded, but its txid was not saved; the worker reads it again", swap.SwapId, swap.StoreId);
        }
    }

    // What an existing payment reports, as GetPayment would.
    internal static PayResponse Report(SatoraLightningSwap swap)
    {
        var payment = SatoraOutgoingActions.ToPayment(swap);
        var details = new PayDetails
        {
            Status = payment.Status,
            PaymentHash = uint256.Parse(swap.PaymentHash),
            TotalAmount = payment.AmountSent,
            FeeAmount = payment.Fee
        };
        return payment.Status switch
        {
            LightningPaymentStatus.Complete => new PayResponse(PayResult.Ok, details),
            LightningPaymentStatus.Failed => new PayResponse(PayResult.Error, $"Satora swap {swap.SwapId} for this invoice failed ({swap.Status}); its funds return to the wallet."),
            _ => new PayResponse(PayResult.Unknown, details)
        };
    }

    // A BOLT11 invoice for this network, or null with the reason.
    internal static BOLT11PaymentRequest? ParseInvoice(string? bolt11, Network network, out string? refusal)
    {
        refusal = null;
        if (string.IsNullOrEmpty(bolt11))
            refusal = "The Satora wallet pays BOLT11 invoices only.";
        else if (!BOLT11PaymentRequest.TryParse(bolt11, out var invoice, network) || invoice?.PaymentHash is null)
            refusal = "The invoice is not a valid BOLT11 invoice for this network.";
        else
            return invoice;
        return null;
    }

    // Why a new payment cannot pay the invoice, or null. The swap server pays the invoice's own amount.
    internal static string? Refusal(BOLT11PaymentRequest invoice, PayInvoiceParams? payParams, DateTimeOffset now)
    {
        if (invoice.MinimumAmount == LightMoney.Zero)
            return "The Satora wallet cannot pay an invoice without an amount.";
        if (payParams?.Amount is { } amount && amount != LightMoney.Zero && amount != invoice.MinimumAmount)
            return $"The invoice is for {invoice.MinimumAmount.MilliSatoshi} msat, not {amount.MilliSatoshi} msat.";
        return invoice.ExpiryDate <= now ? "The invoice has expired." : null;
    }

    // BTCPay's payout processor caps the routing fee at MaxFeePercent; the swap's fees stand in for it.
    // A caller that sets no cap gets the one a Lightning receive has, so a swap whose amount the server
    // inflated is refused before anything is attempted.
    internal static long MaxFundingSats(LightMoney amount, PayInvoiceParams? payParams)
    {
        var sats = amount.MilliSatoshi / 1000;
        long? byPercent = payParams?.MaxFeePercent is { } percent ? (long)Math.Floor(sats * percent / 100) : null;
        long? flat = payParams?.MaxFeeFlat?.Satoshi;
        if (byPercent is null && flat is null)
            return sats + SatoraLightningClient.MaxFeeSats(sats);
        return sats + Math.Max(byPercent ?? 0, flat ?? 0);
    }

    // expectedPaymentHash: the invoice the plugin asked the server to pay.
    internal static SatoraLightningSwap ToRow(SwapDetails created, string storeId, Network network, string expectedPaymentHash, DateTimeOffset now)
    {
        if (created.Funding is not SdkSwapFunding.ArkadeAddress ||
            !BOLT11PaymentRequest.TryParse(created.ReceiveAddress, out var invoice, network) || invoice?.PaymentHash is not { } paymentHash ||
            !long.TryParse(created.DepositAmount, out var fundingSats) || fundingSats <= 0 ||
            created.VhtlcRefundLocktime is not { } refundLocktime)
            throw new InvalidOperationException($"The swap server returned swap {created.Id} without a valid invoice, amount or refund locktime.");
        if (paymentHash.ToString() != expectedPaymentHash)
            throw new InvalidOperationException($"The swap server returned swap {created.Id} for another invoice than the one to pay.");
        if (fundingSats * 1000 < invoice.MinimumAmount.MilliSatoshi)
            throw new InvalidOperationException($"The swap server returned swap {created.Id} taking less than the invoice amount.");
        return new SatoraLightningSwap
        {
            SwapId = created.Id,
            StoreId = storeId,
            Direction = SatoraLightningDirection.ArkadeToLightning,
            Status = SatoraSwapStatuses.From(created.Status),
            PaymentHash = paymentHash.ToString(),
            Bolt11 = created.ReceiveAddress,
            AmountMsat = invoice.MinimumAmount.MilliSatoshi,
            FundingSats = fundingSats,
            ClaimCutoff = DateTimeOffset.FromUnixTimeSeconds(refundLocktime),
            ExpiresAt = invoice.ExpiryDate,
            CreatedAt = now
        };
    }
}

public sealed class SatoraSwapClosedException(string swapId)
    : InvalidOperationException($"Satora swap {swapId} was closed before it was funded, so nothing was paid.");
