using System.Numerics;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Satora.Sdk;

namespace BTCPayServer.Plugins.Satora.Controllers;

// Sending a customer's tokens back, as refunding bitcoin, needs the store's settings permission rather
// than hot wallet rights. The plugin never sends them without the owner's address.
[Route("~/plugins/{storeId}/Satora/stablecoin-refunds")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
[AutoValidateAntiforgeryToken]
public class SatoraStableRefundController(
    SatoraStableSwaps swaps,
    SatoraStableWallets wallets,
    SatoraStableCredit credit,
    ILogger<SatoraStableRefundController> logger) : Controller
{
    private static readonly TimeSpan AddressReadTimeout = TimeSpan.FromSeconds(5);
    // A check may first wait for a seed replace in progress.
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    // What a sweep sends is the address's whole balance, which for an underpayment is not the deposit.
    [HttpGet]
    public async Task<IActionResult> Index([FromRoute] string storeId)
    {
        var waiting = await swaps.ListNeedingAttentionAsync(storeId);
        var reads = await Task.WhenAll(waiting.Where(s => s.AttentionReason == SatoraStableAttention.Sweep)
            .Select(async s => (s.SwapId, Funds: await ReadAsync(s))));
        return View(new SatoraStableRefundPageModel
        {
            StoreId = storeId,
            Swaps = waiting,
            Funds = reads.ToDictionary(r => r.SwapId, r => r.Funds),
            Now = DateTimeOffset.UtcNow
        });
    }

    // A swap whose address cannot be read is still listed, so its sweep can be requested. The SDK's read
    // cannot be cancelled once it runs, so the page stops waiting for it instead.
    private async Task<SatoraDepositorFunds?> ReadAsync(SatoraStableSwap swap)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(AddressReadTimeout);
        try
        {
            return await wallets.GetFundsAsync(swap, swap.SweepToken, timeout.Token).WaitAsync(timeout.Token);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not read the deposit address of Satora stablecoin swap {SwapId}", swap.SwapId);
            return null;
        }
    }

    [HttpPost("{swapId}")]
    public async Task<IActionResult> RequestRefund([FromRoute] string storeId, [FromRoute] string swapId, SatoraStableRefundRequest vm)
    {
        var swap = await swaps.FindAsync(storeId, swapId);
        var destination = vm.Destination?.Trim();
        var token = swap is null ? null : SatoraStableRefunds.Token(swap, vm.Token);
        var refusal = SatoraStableRefunds.Check(swap, destination, token, vm.Confirmation);
        // The worker may have started the sweep between the check and this write.
        if (refusal == SatoraStableRefundRefusal.None && !await swaps.RequestSweepAsync(storeId, swapId, destination!, token))
            refusal = SatoraStableRefundRefusal.AlreadyStarted;
        TempData[refusal == SatoraStableRefundRefusal.None ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = refusal switch
        {
            SatoraStableRefundRefusal.None =>
                $"Refund to {destination} requested. It goes out within a minute of the deposit address holding gas. If it holds none, send it about {SatoraStableRefunds.GasToSend(swap!.ChainId)} from any wallet.",
            SatoraStableRefundRefusal.NotFound => "There is no such stablecoin swap in this store.",
            SatoraStableRefundRefusal.NotNeeded => "That swap does not need a refund.",
            SatoraStableRefundRefusal.HtlcRefund => "That swap's deposit is locked in the swap contract. To refund it, contact Satora support at support@satora.io with the swap id.",
            SatoraStableRefundRefusal.AlreadyStarted => "The refund of that swap has started; its address can no longer change.",
            SatoraStableRefundRefusal.InvalidAddress =>
                "Enter the customer's address on the swap's network exactly as given, including any capital letters. It cannot be the deposit address or a token contract.",
            SatoraStableRefundRefusal.InvalidToken => "Enter the contract of the token to refund, or leave it empty for the swap's own token.",
            _ => $"Type {SatoraStableRefunds.ConfirmationWord} to refund the customer."
        };
        return RedirectToAction(nameof(Index), new { storeId });
    }

    // For tokens the store will not send back: the customer was paid back another way, or what is left
    // is not worth the gas. The worker stops following the swap.
    [HttpPost("{swapId}/dismiss")]
    public async Task<IActionResult> Dismiss([FromRoute] string storeId, [FromRoute] string swapId, SatoraStableRefundRequest vm)
    {
        var (refusal, swap) = await DismissAsync(storeId, swapId, vm.Confirmation);
        if (refusal == SatoraStableRefundRefusal.None)
        {
            logger.LogInformation("Sending back the tokens of Satora stablecoin swap {SwapId} of store {StoreId}, of invoice {InvoiceId}, was dismissed; the plugin no longer follows the swap",
                swapId, storeId, swap!.InvoiceId);
            await credit.NoteAsync(swap, SatoraStableRefunds.DismissedNote(swap), InvoiceEventData.EventSeverity.Info);
        }
        TempData[refusal == SatoraStableRefundRefusal.None ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = refusal switch
        {
            SatoraStableRefundRefusal.None => "Refund dismissed. If more tokens arrive at its deposit address, check the address below.",
            SatoraStableRefundRefusal.NotFound => "There is no such stablecoin swap in this store.",
            SatoraStableRefundRefusal.NotNeeded => "That swap does not need a refund.",
            SatoraStableRefundRefusal.HtlcRefund => "That swap's deposit is locked in the swap contract and cannot be dismissed. Contact Satora support at support@satora.io with the swap id.",
            SatoraStableRefundRefusal.SweepRunning => "A refund attempt for that swap is running; try again in a minute.",
            _ => $"Type {SatoraStableRefunds.DismissWord} to dismiss the refund."
        };
        return RedirectToAction(nameof(Index), new { storeId });
    }

    // Decided between the worker's sweep calls, any of which may send, on the swap as it is then. Another
    // store's swap is refused as not found before its hold is tried, which would reveal a sweep in flight.
    private async Task<(SatoraStableRefundRefusal, SatoraStableSwap?)> DismissAsync(string storeId, string swapId, string? confirmation)
    {
        if (await swaps.FindAsync(storeId, swapId) is null)
            return (SatoraStableRefundRefusal.NotFound, null);
        using var hold = swaps.TryHoldForDismissal(swapId);
        if (hold is null)
            return (SatoraStableRefundRefusal.SweepRunning, null);
        var swap = await swaps.FindAsync(storeId, swapId);
        var refusal = SatoraStableRefunds.CheckDismiss(swap, confirmation);
        if (refusal == SatoraStableRefundRefusal.None && !await swaps.DismissAsync(storeId, swapId, DateTimeOffset.UtcNow))
            refusal = SatoraStableRefundRefusal.NotNeeded;
        return (refusal, swap);
    }

    // Nothing reads a closed swap's deposit address, so tokens that arrive after it closed, as from a slow
    // exchange withdrawal or a customer paying a saved address again, wait for the owner to look. A swap
    // whose address holds them is listed again to send them back.
    [HttpPost("check")]
    public async Task<IActionResult> CheckAddress([FromRoute] string storeId, SatoraStableCheckRequest vm)
    {
        var id = vm.Id?.Trim();
        var token = string.IsNullOrWhiteSpace(vm.Token) ? null : vm.Token.Trim();
        string? error = null;
        List<string> results = [];
        if (string.IsNullOrEmpty(id))
            error = "Enter the swap id or invoice id to check.";
        else if (token is not null && !SatoraEvmAddress.IsValid(token))
            error = "Enter the contract of the token to look for, or leave it empty for the swap's own.";
        else if (await swaps.FindByIdAsync(storeId, id) is not { Count: > 0 } found)
            error = "No stablecoin swap of this store has that swap or invoice id.";
        else
            foreach (var swap in found)
                results.Add(await CheckAsync(swap, token));
        TempData[error is null ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = error ?? string.Join("\n", results);
        return RedirectToAction(nameof(Index), new { storeId });
    }

    private async Task<string> CheckAsync(SatoraStableSwap swap, string? requested)
    {
        if (swap.CompletedAt is not { } closedAt)
            return swap.NeedsAttentionAt is not null
                ? $"Swap {swap.SwapId} is listed below."
                : $"Swap {swap.SwapId} is still open, and its deposit address is checked automatically.";
        var token = SatoraStableRefunds.Token(swap, requested);
        CheckRead read;
        try
        {
            read = await wallets.ReadThenAsync(swap, token, async funds =>
                {
                    var reopened = SatoraStableRefunds.Worth(swap, token, funds.TokenUnits) &&
                                   await swaps.ReopenForSweepAsync(swap.StoreId, swap.SwapId, closedAt, token, DateTimeOffset.UtcNow);
                    // Here rather than after the wait, which can stop before a slow read and its reopen finish.
                    if (reopened)
                        await NoteReopenedAsync(swap, SatoraStableRefunds.Amount(swap, token, funds.TokenUnits));
                    return new CheckRead(funds.TokenUnits, reopened);
                },
                HttpContext.RequestAborted).WaitAsync(CheckTimeout, HttpContext.RequestAborted);
        }
        // The SDK keeps each seed's swaps apart, so the current wallet has no record of a swap a
        // replaced seed made, nor of any once the store's SDK data is lost. The wallet's creation time
        // cannot tell a replaced seed: restoring a replaced phrase sets it.
        catch (SdkException e) when (SatoraWithdrawalService.IsSwapRecordMissing(e))
        {
            return $"Swap {swap.SwapId} cannot be checked. A wallet this store has since replaced made it, or the wallet's SDK data is missing.";
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not check the deposit address of Satora stablecoin swap {SwapId}", swap.SwapId);
            return $"The deposit address of swap {swap.SwapId} could not be read.";
        }
        var held = SatoraStableRefunds.Amount(swap, token, read.Units);
        if (read.Reopened)
            return $"The deposit address of swap {swap.SwapId} holds {held}. The swap is listed below for a refund.";
        return SatoraStableRefunds.Worth(swap, token, read.Units)
            ? $"The deposit address of swap {swap.SwapId} holds {held}, but the swap changed during the check. Check it again."
            : $"The deposit address of swap {swap.SwapId} holds {held}, too little to refund.";
    }

    // The swap is listed again whether or not the note is written.
    private async Task NoteReopenedAsync(SatoraStableSwap swap, string held)
    {
        logger.LogInformation("The deposit address of closed Satora stablecoin swap {SwapId} of store {StoreId} holds {Held}; it is listed for a sweep again",
            swap.SwapId, swap.StoreId, held);
        try
        {
            await credit.NoteAsync(swap,
                $"The deposit address {swap.DepositAddress} of Satora stablecoin swap {swap.SwapId} held {held} after the swap closed. It is listed again on Satora's Stablecoin refunds page.",
                InvoiceEventData.EventSeverity.Warning);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Could not note on its invoice that closed Satora stablecoin swap {SwapId} is listed for a sweep again", swap.SwapId);
        }
    }

    private sealed record CheckRead(BigInteger Units, bool Reopened);
}
