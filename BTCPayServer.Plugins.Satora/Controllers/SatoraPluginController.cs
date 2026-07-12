using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Satora.Controllers
{
    [Route("~/plugins/{storeId}/Satora")]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanModifyStoreSettings)]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanViewInvoices)]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanCreateNonApprovedPullPayments)]
    [Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie, Policy = Policies.CanManagePayouts)]
    [AutoValidateAntiforgeryToken]
    public class SatoraPluginController(
        SatoraPluginService pluginService,
        StoreRepository storeRepository) : Controller
    {

        [HttpGet]
        public async Task<IActionResult> Index([FromRoute] string storeId)
        {
            var model = await pluginService.GetStoreData(storeId);
            model.CanManageSeed = await IsStoreOwner(storeId);
            return View(model);
        }

        [HttpPost]
        public async Task<IActionResult> Index(SatoraSettings settings)
        {
            if (settings.Enabled && !await IsStoreOwner(settings.StoreId))
                return Forbid();

            if (ModelState.IsValid)
            {
                try
                {
                    await pluginService.UpdateSettings(settings);
                    TempData[WellKnownTempData.SuccessMessage] = "Settings successfuly saved";
                }
                catch (Exception ex)
                {
                    TempData[WellKnownTempData.ErrorMessage] = $"Error: {ex.Message}";
                    throw;
                }
            }
            return RedirectToAction("Index");
        }

        [HttpPost("generate-wallet")]
        public async Task<IActionResult> GenerateWallet([FromRoute] string storeId)
        {
            if (!await IsStoreOwner(storeId))
                return Forbid();

            try
            {
                var seed = await pluginService.GenerateWallet(storeId);
                var model = await pluginService.GetStoreData(storeId);
                model.CanManageSeed = true;
                model.OneTimeSeed = seed;
                TempData[WellKnownTempData.SuccessMessage] = "Satora wallet seed generated. Confirm the backup to enable the wallet.";
                return View("Index", model);
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Error: {ex.Message}";
                return RedirectToAction("Index", new { storeId });
            }
        }

        [HttpPost("restore-wallet")]
        public async Task<IActionResult> RestoreWallet([FromRoute] string storeId, SatoraRestoreWalletRequest request)
        {
            if (!await IsStoreOwner(storeId))
                return Forbid();

            request.StoreId = storeId;
            if (!ModelState.IsValid)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Seed and seed confirmation are required.";
                return RedirectToAction("Index", new { storeId });
            }

            try
            {
                await pluginService.RestoreWallet(request);
                TempData[WellKnownTempData.SuccessMessage] = "Satora wallet restored and enabled.";
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Error: {ex.Message}";
            }
            return RedirectToAction("Index", new { storeId });
        }

        [HttpPost("confirm-backup")]
        public async Task<IActionResult> ConfirmBackup([FromRoute] string storeId, SatoraConfirmSeedRequest request)
        {
            if (!await IsStoreOwner(storeId))
                return Forbid();

            request.StoreId = storeId;
            if (!ModelState.IsValid)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Seed confirmation is required.";
                return RedirectToAction("Index", new { storeId });
            }

            try
            {
                await pluginService.ConfirmWalletBackup(request);
                TempData[WellKnownTempData.SuccessMessage] = "Satora wallet backup confirmed and wallet enabled.";
            }
            catch (Exception ex)
            {
                TempData[WellKnownTempData.ErrorMessage] = $"Error: {ex.Message}";
            }
            return RedirectToAction("Index", new { storeId });
        }

        private async Task<bool> IsStoreOwner(string storeId)
        {
            if (User.IsInRole(Roles.ServerAdmin))
                return true;
            var userId = User.GetIdOrNull();
            if (userId is null)
                return false;
            var store = await storeRepository.FindStore(storeId, userId);
            return store?.UserStores?.Any(u =>
                u.ApplicationUserId == userId &&
                string.Equals(StoreRoleId.Parse(u.StoreRoleId).Role, StoreRoleId.Owner.Role, StringComparison.OrdinalIgnoreCase)) is true;
        }


        [HttpGet]
        [Route("GetSwapStatus")]
        public async Task<ActionResult> GetSwapStatus(string swapId)
        {
            try
            {
                var status = await pluginService.DoGetSwapStatus(swapId);
                return Ok(status);
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpGet("swap/{swapId}")]
        public async Task<IActionResult> SwapDetails([FromRoute] string storeId, [FromRoute] string swapId)
        {
            var model = await pluginService.GetSwapDetailsAsync(storeId, swapId);
            return View(model);
        }

    }
}
