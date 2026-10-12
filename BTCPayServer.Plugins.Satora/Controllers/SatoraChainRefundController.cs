using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Satora.Controllers;

// Sending a customer's bitcoin back, as withdrawing, needs the store's settings permission rather
// than hot wallet rights. The plugin never refunds without the owner's address.
[Route("~/plugins/{storeId}/Satora/onchain-refunds")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
[AutoValidateAntiforgeryToken]
public class SatoraChainRefundController(
    SatoraChainSwaps swaps,
    SatoraChainWallets wallets,
    SatoraChainCredit credit,
    BTCPayNetworkProvider networkProvider,
    ILogger<SatoraChainRefundController> logger) : Controller
{
    private static readonly TimeSpan AddressReadTimeout = TimeSpan.FromSeconds(5);

    // What a refund sends is whatever sits at the address, which for an underpayment or a payment after
    // the swap ended is not the deposit. A swap whose refund was broadcast shows the refund instead.
    [HttpGet]
    public async Task<IActionResult> Index([FromRoute] string storeId)
    {
        var waiting = await swaps.ListNeedingRefundAsync(storeId);
        var reads = await Task.WhenAll(waiting.Where(s => s.RefundTxId is null).Select(async s => (s.SwapId, Outputs: await ReadAsync(s))));
        return View(new SatoraChainRefundPageModel
        {
            StoreId = storeId,
            Swaps = waiting,
            Outputs = reads.ToDictionary(r => r.SwapId, r => r.Outputs),
            Now = DateTimeOffset.UtcNow
        });
    }

    // A swap whose address cannot be read is still listed, so its refund can be requested.
    private async Task<IReadOnlyList<SatoraHtlcOutput>?> ReadAsync(SatoraChainSwap swap)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        timeout.CancelAfter(AddressReadTimeout);
        try
        {
            return await wallets.ListUnspentAsync(swap.HtlcAddress, timeout.Token);
        }
        catch (Exception e)
        {
            logger.LogDebug(e, "Could not read the address of Satora on-chain swap {SwapId}", swap.SwapId);
            return null;
        }
    }

    [HttpPost("{swapId}")]
    public async Task<IActionResult> RequestRefund([FromRoute] string storeId, [FromRoute] string swapId, SatoraChainRefundRequest vm)
    {
        var swap = await swaps.FindAsync(storeId, swapId);
        var address = vm.Address?.Trim();
        var refusal = SatoraChainRefunds.Check(swap, address, networkProvider.GetNetwork<BTCPayNetwork>(SatoraChainPaymentMethod.CryptoCode)!.NBitcoinNetwork, vm.Confirmation);
        // The worker may have started the refund between the check and this write.
        if (refusal == SatoraChainRefundRefusal.None && !await swaps.RequestRefundAsync(storeId, swapId, address!))
            refusal = SatoraChainRefundRefusal.AlreadyStarted;
        TempData[refusal == SatoraChainRefundRefusal.None ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = refusal switch
        {
            SatoraChainRefundRefusal.None =>
                $"Refund to {address} requested. It goes out from about {swap!.BtcRefundLocktime:u}. The address can be changed until then.",
            SatoraChainRefundRefusal.NotFound => "There is no such on-chain swap in this store.",
            SatoraChainRefundRefusal.NotNeeded => "That swap does not need a refund.",
            SatoraChainRefundRefusal.AlreadyStarted => "The refund of that swap has started; its address can no longer change.",
            SatoraChainRefundRefusal.InvalidAddress => "Enter a Bitcoin address on this store's network.",
            _ => $"Type {SatoraChainRefunds.ConfirmationWord} to refund the customer."
        };
        return RedirectToAction(nameof(Index), new { storeId });
    }

    // For a refund the store will not make: the customer was paid back another way, or what is left is
    // not worth it. The worker stops following the swap.
    [HttpPost("{swapId}/dismiss")]
    public async Task<IActionResult> Dismiss([FromRoute] string storeId, [FromRoute] string swapId, SatoraChainRefundRequest vm)
    {
        var (refusal, swap) = await DismissAsync(storeId, swapId, vm.Confirmation);
        if (refusal == SatoraChainRefundRefusal.None)
        {
            logger.LogInformation("The refund of Satora on-chain swap {SwapId} of store {StoreId}, of invoice {InvoiceId}, was dismissed; the plugin no longer follows the swap",
                swapId, storeId, swap!.InvoiceId);
            await credit.NoteAsync(swap, SatoraChainRefunds.DismissedNote(swap), InvoiceEventData.EventSeverity.Info);
        }
        TempData[refusal == SatoraChainRefundRefusal.None ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = refusal switch
        {
            SatoraChainRefundRefusal.None => "Refund dismissed. Nothing more is sent from that swap's address.",
            SatoraChainRefundRefusal.NotFound => "There is no such on-chain swap in this store.",
            SatoraChainRefundRefusal.NotNeeded => "That swap does not need a refund.",
            SatoraChainRefundRefusal.RefundRunning => "A refund attempt for that swap is running; try again in a minute.",
            _ => $"Type {SatoraChainRefunds.DismissWord} to dismiss the refund."
        };
        return RedirectToAction(nameof(Index), new { storeId });
    }

    // Decided between the worker's refund calls, any of which may broadcast, on the swap as it is then.
    // Another store's swap is refused as not found before its hold is tried, which would reveal a
    // refund in flight.
    private async Task<(SatoraChainRefundRefusal, SatoraChainSwap?)> DismissAsync(string storeId, string swapId, string? confirmation)
    {
        if (await swaps.FindAsync(storeId, swapId) is null)
            return (SatoraChainRefundRefusal.NotFound, null);
        using var hold = swaps.TryHoldForDismissal(swapId);
        if (hold is null)
            return (SatoraChainRefundRefusal.RefundRunning, null);
        var swap = await swaps.FindAsync(storeId, swapId);
        var refusal = SatoraChainRefunds.CheckDismiss(swap, confirmation);
        if (refusal == SatoraChainRefundRefusal.None && !await swaps.DismissRefundAsync(storeId, swapId, DateTimeOffset.UtcNow))
            refusal = SatoraChainRefundRefusal.NotNeeded;
        return (refusal, swap);
    }
}
