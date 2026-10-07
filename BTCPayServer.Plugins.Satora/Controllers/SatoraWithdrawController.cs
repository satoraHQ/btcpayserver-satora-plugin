using System.Text.Json;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Satora.Controllers;

// Withdrawing from an existing wallet needs the store's settings permission, not hot wallet rights:
// a server admin turning hot wallets off must not strand the funds.
[Route("~/plugins/{storeId}/Satora/withdraw")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
[AutoValidateAntiforgeryToken]
public class SatoraWithdrawController(SatoraWithdrawalService withdrawalService) : Controller
{
    private const string ReviewKey = "SatoraWithdrawReview";
    private const string AutoWithdrawFormKey = "SatoraAutoWithdrawForm";

    [HttpGet]
    public async Task<IActionResult> Index([FromRoute] string storeId)
    {
        var model = await withdrawalService.GetPageAsync(storeId);
        if (TempData[ReviewKey] is string review)
            model.Review = JsonSerializer.Deserialize<SatoraWithdrawReview>(review);
        // A refused form is shown again only for the rule it was loaded with: refilled over a rule
        // changed since, with that rule's version, one more save would undo the change unseen.
        if (TempData[AutoWithdrawFormKey] is string form &&
            JsonSerializer.Deserialize<SatoraAutoWithdrawRequest>(form) is { } entered && entered.Version == model.AutoWithdraw?.Version)
            model.AutoWithdrawForm = entered;
        return View(model);
    }

    [HttpPost("review")]
    public async Task<IActionResult> Review([FromRoute] string storeId, SatoraWithdrawRequest request)
    {
        var review = await withdrawalService.ReviewAsync(storeId, request);
        TempData[ReviewKey] = JsonSerializer.Serialize(review);
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("execute")]
    public async Task<IActionResult> Execute([FromRoute] string storeId, SatoraWithdrawRequest request)
    {
        var result = await withdrawalService.ExecuteAsync(storeId, request);
        var success = result.Kind is SatoraWithdrawResultKind.Sent or SatoraWithdrawResultKind.Pending;
        TempData[success ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = result.Message;
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("settle")]
    public async Task<IActionResult> Settle([FromRoute] string storeId)
    {
        var result = await withdrawalService.SettleAsync(storeId);
        var success = result.Outcome is SatoraSettleOutcome.Settled or SatoraSettleOutcome.NothingToSettle;
        TempData[success ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = result.Message;
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("auto/save")]
    public async Task<IActionResult> SaveAutoWithdraw([FromRoute] string storeId, SatoraAutoWithdrawRequest request)
    {
        var (saved, message) = await withdrawalService.SaveAutoWithdrawAsync(storeId, request);
        TempData[saved ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = message;
        if (!saved)
            TempData[AutoWithdrawFormKey] = JsonSerializer.Serialize(request);
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("auto/disable")]
    public async Task<IActionResult> DisableAutoWithdraw([FromRoute] string storeId)
    {
        TempData[WellKnownTempData.SuccessMessage] = await withdrawalService.DisableAutoWithdrawAsync(storeId)
            ? "Automatic withdrawal is off."
            : "Automatic withdrawal was off already.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("{withdrawalId}/resolve")]
    public async Task<IActionResult> Resolve([FromRoute] string storeId, [FromRoute] string withdrawalId, SatoraResolveWithdrawalViewModel vm)
    {
        if (await withdrawalService.ResolveAsync(storeId, withdrawalId, vm.Sent))
            TempData[WellKnownTempData.SuccessMessage] = vm.Sent ? "Withdrawal marked sent." : "Withdrawal marked failed.";
        else
            TempData[WellKnownTempData.ErrorMessage] = "That withdrawal cannot be marked. Its outcome is already known, or it may still be running.";
        return RedirectToAction(nameof(Index), new { storeId });
    }

    [HttpPost("payments/{swapId}/close")]
    public async Task<IActionResult> ClosePayment([FromRoute] string storeId, [FromRoute] string swapId, SatoraCloseSwapViewModel vm)
    {
        if (!string.Equals(vm.Confirmation?.Trim(), SatoraWithdrawalService.CloseConfirmationWord, StringComparison.Ordinal))
            TempData[WellKnownTempData.ErrorMessage] = $"Type {SatoraWithdrawalService.CloseConfirmationWord} to close the payment.";
        else if (vm.Paid is not { } paid)
            TempData[WellKnownTempData.ErrorMessage] = "Choose whether the invoice was paid or the funds are back in the wallet.";
        else
        {
            var (closed, message) = await withdrawalService.CloseSwapAsync(storeId, swapId, paid);
            TempData[closed ? WellKnownTempData.SuccessMessage : WellKnownTempData.ErrorMessage] = message;
        }
        return RedirectToAction(nameof(Index), new { storeId });
    }
}
