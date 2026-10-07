using System.Globalization;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Models.StoreViewModels;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Plugins.Satora.Stable;
using BTCPayServer.Services;
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
        SatoraWalletService walletService,
        IAuthorizationService authorizationService,
        PoliciesSettings policiesSettings,
        SatoraStableQuotes stableQuotes,
        ILogger<SatoraPluginController> logger) : Controller
    {
        private static readonly TimeSpan QuoteTimeout = TimeSpan.FromSeconds(15);

        [HttpGet]
        public async Task<IActionResult> Index([FromRoute] string storeId)
        {
            var model = await pluginService.GetStoreData(storeId);
            model.CanUseHotWallet = await CanUseHotWallet();
            model.PaymentTolerance = HttpContext.GetStoreData().GetStoreBlob().PaymentTolerance;
            // From the last quote read, or the minimum before any: the page asks the swap server nothing.
            model.StablecoinToleranceShortfalls = model.Stablecoins
                .Select(coin => (Coin: coin, Floor: stableQuotes.LastRead(coin) is { } quote
                    ? SatoraStableActions.ToleranceFloorPercent(quote)
                    : SatoraStableActions.MinToleranceFloorPercent))
                .Where(c => (decimal)model.PaymentTolerance < c.Floor)
                .ToList();
            return View(model);
        }

        // Anyone who may change the store's settings may stop it taking Satora payments.
        [HttpPost]
        public async Task<IActionResult> Index([FromRoute] string storeId, SatoraSettingsViewModel vm)
        {
            if (vm.Enabled && !await CanUseHotWallet())
                return NotFound();
            if (await walletService.SetEnabledAsync(storeId, vm.Enabled))
                TempData[WellKnownTempData.SuccessMessage] = vm.Enabled ? "Satora wallet enabled." : "Satora wallet disabled.";
            else
                TempData[WellKnownTempData.ErrorMessage] = "Confirm the wallet backup before enabling it.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        [HttpPost("generate-wallet")]
        public async Task<IActionResult> GenerateWallet([FromRoute] string storeId, SatoraGenerateWalletViewModel vm)
        {
            if (!await CanUseHotWallet())
                return NotFound();
            var (refusal, mnemonic) = await walletService.GenerateAsync(storeId, vm.ReplaceConfirmation);
            if (mnemonic is null)
                return await RefusedAsync(storeId, refusal);
            return ShowSeed(storeId, mnemonic, requireConfirm: true);
        }

        [HttpPost("restore-wallet")]
        public async Task<IActionResult> RestoreWallet([FromRoute] string storeId, SatoraRestoreWalletViewModel vm)
        {
            if (!await CanUseHotWallet())
                return NotFound();
            // Fixed text only: model errors and exception messages could carry words of the phrase.
            if (!SatoraMnemonic.TryParse(vm.Seed, out var mnemonic) ||
                !SatoraMnemonic.TryParse(vm.SeedConfirmation, out var confirmation) ||
                mnemonic != confirmation)
            {
                TempData[WellKnownTempData.ErrorMessage] = "Enter the same valid BIP39 recovery phrase in both fields.";
                return RedirectToAction(nameof(Index), new { storeId });
            }
            var refusal = await walletService.RestoreAsync(storeId, mnemonic, vm.ReplaceConfirmation);
            if (refusal != SeedWriteRefusal.None)
                return await RefusedAsync(storeId, refusal);
            TempData[WellKnownTempData.SuccessMessage] = "Satora wallet restored.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        [HttpPost("confirm-backup")]
        public async Task<IActionResult> ConfirmBackup([FromRoute] string storeId, SatoraConfirmBackupViewModel vm)
        {
            if (!await CanUseHotWallet())
                return NotFound();
            if (await walletService.ConfirmBackupAsync(storeId, vm.SeedConfirmation))
                TempData[WellKnownTempData.SuccessMessage] = "Backup confirmed. The wallet can now be enabled.";
            else
                TempData[WellKnownTempData.ErrorMessage] = "That is not the recovery phrase of this wallet.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        [HttpPost("view-seed")]
        public async Task<IActionResult> ViewSeed([FromRoute] string storeId)
        {
            if (!await CanUseHotWallet())
                return NotFound();
            var mnemonic = (await walletService.GetStatusAsync(storeId)).Mnemonic;
            if (mnemonic is null)
            {
                TempData[WellKnownTempData.ErrorMessage] = "The wallet seed is not available.";
                return RedirectToAction(nameof(Index), new { storeId });
            }
            return ShowSeed(storeId, mnemonic, requireConfirm: false);
        }

        [HttpPost("rotate-lightning-key")]
        public async Task<IActionResult> RotateLightningKey([FromRoute] string storeId)
        {
            if (!await CanUseHotWallet())
                return NotFound();
            if (await walletService.RotateLightningKeyAsync(storeId))
                TempData[WellKnownTempData.SuccessMessage] = "Lightning connection key rotated. Copies of the old connection string no longer work.";
            else
                TempData[WellKnownTempData.ErrorMessage] = "The wallet has no Lightning connection key yet.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        [HttpPost("use-as-lightning-node")]
        public async Task<IActionResult> UseAsLightningNode([FromRoute] string storeId, SatoraUseAsLightningNodeViewModel vm)
        {
            if (!await CanUseHotWallet())
                return NotFound();
            if (!string.Equals(vm.ReplaceConfirmation?.Trim(), SeedWriteGuard.ConfirmationWord, StringComparison.Ordinal))
                TempData[WellKnownTempData.ErrorMessage] = $"Type {SeedWriteGuard.ConfirmationWord} to replace the store's Lightning node.";
            else if (await walletService.UseAsLightningNodeAsync(storeId))
                TempData[WellKnownTempData.SuccessMessage] = "The Satora wallet is now the store's Lightning node. LNURL is turned off.";
            else
                TempData[WellKnownTempData.ErrorMessage] = "Enable the Satora wallet first.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        // Turning a stablecoin on needs hot wallet rights, as enabling the wallet does, and turning one off
        // only the store's settings permission. The DEX swap that funds a payment can pay the wallet up
        // to the swap's fees less than quoted, so one is turned on only while the store's payment
        // tolerance covers that share of a typical payment; below it such an invoice would not settle.
        [HttpPost("stablecoins")]
        public async Task<IActionResult> Stablecoins([FromRoute] string storeId, SatoraStablecoinsViewModel vm)
        {
            var requested = SatoraStablePaymentMethod.All.Where(c => vm.Methods?.Contains(c.Id.ToString()) is true).ToList();
            var added = requested.Except(await walletService.GetStablecoinsAsync(storeId)).ToList();
            if (added.Count > 0 && !await CanUseHotWallet())
                return NotFound();
            var tolerance = HttpContext.GetStoreData().GetStoreBlob().PaymentTolerance;
            foreach (var coin in added)
            {
                decimal floor;
                try
                {
                    floor = SatoraStableActions.ToleranceFloorPercent(await stableQuotes.GetAsync(storeId, coin, HttpContext.RequestAborted).WaitAsync(QuoteTimeout));
                }
                catch (Exception e)
                {
                    logger.LogWarning(e, "Could not read the Satora quote for {PaymentMethodId} of store {StoreId}", coin.Id, storeId);
                    TempData[WellKnownTempData.ErrorMessage] = $"The swap server's fees for {coin.PrettyName} could not be read, so nothing was changed. Try again in a minute.";
                    return RedirectToAction(nameof(Index), new { storeId });
                }
                if ((decimal)tolerance < floor)
                {
                    TempData[WellKnownTempData.ErrorMessage] = string.Create(CultureInfo.InvariantCulture,
                        $"{coin.PrettyName} needs a payment tolerance of at least {floor:0.#}%, and the store's is {tolerance:0.##}%. " +
                        $"Raise it a little above that, as the minimum rises with the swap's fees. Nothing was changed.");
                    return RedirectToAction(nameof(Index), new { storeId });
                }
            }
            if (await walletService.SetStablecoinsAsync(storeId, requested))
                TempData[WellKnownTempData.SuccessMessage] = "Stablecoin payment methods saved.";
            else
                TempData[WellKnownTempData.ErrorMessage] = "Set up the wallet first.";
            return RedirectToAction(nameof(Index), new { storeId });
        }

        [HttpGet("wallet-balance")]
        public async Task<IActionResult> WalletBalance([FromRoute] string storeId)
        {
            var balance = await walletService.TryGetBalanceAsync(storeId);
            if (balance is null)
                return StatusCode(StatusCodes.Status503ServiceUnavailable);
            return Json(new
            {
                confirmedSats = balance.confirmedSats,
                preConfirmedSats = balance.preConfirmedSats,
                recoverableSats = balance.recoverableSats,
                canSettle = balance.recoverableSats >= SatoraWithdrawalService.DustSats
            });
        }

        // As UIStoreOnChainWalletsController: a hot wallet is for server admins unless the server
        // allows hot wallets for every user (PoliciesSettings.AllowHotWalletForAll).
        private async Task<bool> CanUseHotWallet() =>
            (await authorizationService.CanUseHotWallet(policiesSettings, User)).CanCreateHotWallet;

        // BTCPay's recovery phrase page, as UIStoreOnChainWalletsController.GenerateWallet and WalletSeed use it.
        private IActionResult ShowSeed(string storeId, string mnemonic, bool requireConfirm) =>
            this.RedirectToRecoverySeedBackup(new RecoverySeedBackupViewModel
            {
                CryptoCode = "BTC",
                Mnemonic = mnemonic,
                IsStored = true,
                RequireConfirm = requireConfirm,
                ReturnUrl = Url.Action(nameof(Index), new { storeId })
            });

        private async Task<IActionResult> RefusedAsync(string storeId, SeedWriteRefusal refusal)
        {
            TempData[WellKnownTempData.ErrorMessage] = refusal switch
            {
                SeedWriteRefusal.NetworkUnsupported => walletService.NetworkError,
                SeedWriteRefusal.Locked => "The wallet is locked and cannot be replaced. Restore its own recovery phrase to unlock it.",
                SeedWriteRefusal.RestoreOnly => "Restore the Satora wallet from its recovery phrase before anything else.",
                SeedWriteRefusal.OpenOperations => OpenOperations(await walletService.DescribeOpenOperationsAsync(storeId)),
                SeedWriteRefusal.BalanceUnknown => "The wallet balance could not be read, so the wallet was not replaced. Try again once the Arkade server answers.",
                SeedWriteRefusal.ConfirmationRequired => $"The wallet holds funds. Type {SeedWriteGuard.ConfirmationWord} to replace it anyway.",
                _ => "The wallet was not changed."
            };
            return RedirectToAction(nameof(Index), new { storeId });
        }

        private static string OpenOperations(string named) =>
            "The wallet cannot be replaced while swaps, refunds or withdrawals that depend on it are open" + (named.Length > 0 ? $" ({named})" : "") +
            ". Disable the wallet so no new swaps start, and try again once these are finished, refunded or marked.";
    }
}
