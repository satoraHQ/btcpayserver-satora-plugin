using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Payments;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Stores;
using BTCPayServer.Data;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using uniffi.satora_sdk_ffi;

namespace BTCPayServer.Plugins.Satora.Services
{
    public class SatoraPluginService(
        SatoraPluginDbContextFactory pluginDbContextFactory,
        ILogger<SatoraPluginService> logger,
        SatoraService satoraService,
        SatoraSettlementService settlementService,
        StoreRepository storeRepository)
    {

        public async Task<SatoraSettings> GetStoreSettings(string storeId)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var settings = await _context.SatoraSettings.FirstOrDefaultAsync(a => a.StoreId == storeId);
                if (settings == null)
                {
                    settings = new SatoraSettings
                    {
                        StoreId = storeId,
                        Enabled = false
                    };
                }
                settings.Enabled = settings.Enabled && IsWalletUsable(settings);
                return settings;
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:GetStoreSettings()");
                throw;
            }
        }

        public async Task<SatoraModel> GetStoreData(string storeId)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var settings = await _context.SatoraSettings.FirstOrDefaultAsync(a => a.StoreId == storeId);
                if (settings == null)
                {
                    settings = new SatoraSettings
                    {
                        StoreId = storeId,
                        Enabled = false
                    };
                }
                settings.Enabled = settings.Enabled && IsWalletUsable(settings);

                var txs = await _context.SatoraTransactions
                    .Where(a => a.StoreId == storeId)
                    .OrderByDescending(a => a.DateT)
                    .ToListAsync();

                ArkadeBalance walletBalance = null;
                if (!string.IsNullOrEmpty(settings.Seed))
                    walletBalance = await satoraService.GetArkadeBalanceAsync(settings.Seed);

                return new SatoraModel { Settings = settings, Transactions = txs, WalletBalance = walletBalance };
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:GetStoreData()");
                throw;
            }
        }

        public async Task UpdateSettings(SatoraSettings settings)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var dbSettings = await _context.SatoraSettings.FirstOrDefaultAsync(a => a.StoreId == settings.StoreId);
                if (dbSettings == null)
                {
                    settings.Enabled = settings.Enabled && IsWalletUsable(settings);
                    _context.SatoraSettings.Add(settings);
                }
                else
                {
                    dbSettings.Enabled = settings.Enabled && IsWalletUsable(dbSettings);
                    _context.SatoraSettings.Update(dbSettings);
                }
                await _context.SaveChangesAsync();
                await SyncStorePaymentMethodConfig(dbSettings ?? settings);
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:UpdateSettings()");
                throw;
            }
        }

        public async Task<string> GenerateWallet(string storeId)
        {
            var seed = new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();
            await SaveWalletSeed(storeId, seed, backupConfirmed: false, enabled: false);
            return seed;
        }

        public async Task RestoreWallet(SatoraRestoreWalletRequest request)
        {
            var seed = NormalizeMnemonic(request.Seed);
            var confirmation = NormalizeMnemonic(request.SeedConfirmation);
            if (!string.Equals(seed, confirmation, StringComparison.Ordinal))
                throw new InvalidOperationException("The seed confirmation did not match.");

            await SaveWalletSeed(request.StoreId, seed, backupConfirmed: true, enabled: true);
        }

        public async Task ConfirmWalletBackup(SatoraConfirmSeedRequest request)
        {
            await using var _context = pluginDbContextFactory.CreateContext();
            var settings = await _context.SatoraSettings.FirstOrDefaultAsync(a => a.StoreId == request.StoreId);
            if (settings is null || string.IsNullOrEmpty(settings.Seed))
                throw new InvalidOperationException("No Satora wallet seed exists for this store.");

            var confirmation = NormalizeMnemonic(request.SeedConfirmation);
            if (!string.Equals(settings.Seed, confirmation, StringComparison.Ordinal))
                throw new InvalidOperationException("The seed confirmation did not match.");

            settings.BackupConfirmed = true;
            settings.BackupConfirmedAt = DateTimeOffset.UtcNow;
            settings.Enabled = true;
            await _context.SaveChangesAsync();
            await SyncStorePaymentMethodConfig(settings);
        }

        private async Task SaveWalletSeed(string storeId, string seed, bool backupConfirmed, bool enabled)
        {
            await using var _context = pluginDbContextFactory.CreateContext();
            var settings = await _context.SatoraSettings.FirstOrDefaultAsync(a => a.StoreId == storeId);
            if (settings is null)
            {
                settings = new SatoraSettings { StoreId = storeId };
                _context.SatoraSettings.Add(settings);
            }

            settings.Seed = seed;
            settings.BackupConfirmed = backupConfirmed;
            settings.BackupConfirmedAt = backupConfirmed ? DateTimeOffset.UtcNow : null;
            settings.WalletCreated ??= DateTimeOffset.UtcNow;
            settings.Enabled = enabled && backupConfirmed;
            await _context.SaveChangesAsync();
            await SyncStorePaymentMethodConfig(settings);
        }

        private static string NormalizeMnemonic(string seed)
        {
            return new Mnemonic(seed.Trim(), Wordlist.English).ToString();
        }

        private async Task SyncStorePaymentMethodConfig(SatoraSettings settings)
        {
            var store = await storeRepository.FindStore(settings.StoreId);
            if (store is null)
            {
                logger.LogWarning("SatoraPlugin:SyncStorePaymentMethodConfig(): store {StoreId} not found", settings.StoreId);
                return;
            }

            store.SetPaymentMethodConfig(
                SatoraPaymentMethod.PaymentMethodId,
                IsWalletUsable(settings) && settings.Enabled
                    ? JObject.FromObject(new SatoraPaymentMethodConfig { Enabled = true })
                    : null);

            store.SetPaymentMethodConfig(
                PaymentTypes.LN.GetPaymentMethodId("BTC"),
                IsWalletUsable(settings) && settings.Enabled
                    ? JObject.FromObject(new LightningPaymentMethodConfig
                    {
                        ConnectionString = SatoraLightningConnectionStringHandler.ToConnectionString(settings.StoreId)
                    })
                    : null);
            await storeRepository.UpdateStore(store);
        }

        private static bool IsWalletUsable(SatoraSettings settings)
        {
            return settings.BackupConfirmed && !string.IsNullOrEmpty(settings.CryptedSeed);
        }

        public async Task SyncStorePaymentMethodConfigs(CancellationToken cancellationToken = default)
        {
            await using var _context = pluginDbContextFactory.CreateContext();
            var settings = await _context.SatoraSettings.ToListAsync(cancellationToken);
            foreach (var setting in settings)
                await SyncStorePaymentMethodConfig(setting);
        }

        public async Task AddStoreTransaction(SatoraTx tx)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                await _context.SatoraTransactions.AddAsync(tx);
                await _context.SaveChangesAsync();
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:AddStoreTransaction()");
                throw;
            }
        }

        public async Task<string?> GetSeedPhraseBySwapId(string swapId)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var dbSwap    = await _context.SatoraTransactions.FirstOrDefaultAsync(s => s.TxID == swapId);
                var settings  = await _context.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == dbSwap.StoreId);
                return settings?.Seed;
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:GetSeedPhraseBySwapId()");
                throw;
            }
        }

        public async Task<string> DoGetSwapStatus(string swapId)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var dbSwap      = await _context.SatoraTransactions.FirstOrDefaultAsync(s => s.TxID == swapId);
                var storeSettings = await _context.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == dbSwap.StoreId);
                var status      = await satoraService.GetSwapInfoAsync(swapId, storeSettings.Seed);
                if (dbSwap.Status != status)
                {
                    dbSwap.Status = status;
                    _context.SatoraTransactions.Update(dbSwap);
                    await _context.SaveChangesAsync();
                }
                return status;
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:DoGetSwapStatus()");
                throw;
            }
        }

        public async Task<SwapDetailsModel> GetSwapDetailsAsync(string storeId, string swapId)
        {
            var model = new SwapDetailsModel { StoreId = storeId };

            await using var _context = pluginDbContextFactory.CreateContext();
            var storeSettings = await _context.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == storeId);
            model.LocalTx = await _context.SatoraTransactions
                .FirstOrDefaultAsync(s => s.TxID == swapId && s.StoreId == storeId);

            try
            {
                var swap = await satoraService.GetSwapAsync(swapId, storeSettings.Seed);
                model.BackendStatus  = swap.Status.GetType().Name;
                model.DepositAddress = (swap.Funding as SwapFunding.Gasless)?.@depositAddress;
                model.DepositAmount  = swap.DepositAmount;
                model.DepositToken   = swap.DepositToken.GetType().Name;
                model.ReceiveAddress = swap.ReceiveAddress;
                model.ReceiveAmount  = swap.ReceiveAmount;
                model.ReceiveToken   = swap.ReceiveToken.GetType().Name;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "SatoraPlugin:GetSwapDetailsAsync({SwapId}): backend lookup failed", swapId);
                model.BackendError = e.Message;
            }

            try
            {
                model.DerivedArkadeAddress = await satoraService.GetArkadeAddressAsync(storeSettings.Seed);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "SatoraPlugin:GetSwapDetailsAsync({SwapId}): derived address lookup failed", swapId);
            }

            return model;
        }


        public async Task<(string action, string status)> ContinueSwapAsync(string swapId, string? destinationOverride = null)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var dbSwap        = await _context.SatoraTransactions.FirstOrDefaultAsync(s => s.TxID == swapId);
                var storeSettings = await _context.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == dbSwap.StoreId);

                var swap            = await satoraService.GetSwapAsync(swapId, storeSettings.Seed);
                var statusName      = swap.Status.GetType().Name;
                var originalClaimTxId = dbSwap?.ClaimTxId;
                var originalSweepTxId = dbSwap?.SweepTxId;

                string action;
                switch (swap.Status)
                {
                    case SwapStatus.Pending:
                        var deposit = await satoraService.CheckDepositAsync(swapId, storeSettings.Seed);
                        if (!deposit.HasSufficientSourceToken)
                        {
                            action = $"waiting_for_deposit ({deposit.SourceTokenBalance}/{deposit.SourceTokenRequired})";
                            break;
                        }
                        var fundReceipt = await satoraService.FundSwapAsync(swapId, storeSettings.Seed);
                        logger.LogInformation("SatoraPlugin:ContinueSwap({SwapId}): funded, userOpHash={Hash}", swapId, fundReceipt.UserOpHash);
                        action     = $"funded:{fundReceipt.UserOpHash}";
                        swap       = await satoraService.GetSwapAsync(swapId, storeSettings.Seed);
                        statusName = swap.Status.GetType().Name;
                        break;

                    case SwapStatus.ClientFundingSeen:
                    case SwapStatus.ClientFunded:
                        action = "waiting_for_server";
                        break;

                    case SwapStatus.ServerFunded:
                      /*  var destination = destinationOverride
                            ?? await satoraService.GetArkadeAddressAsync(storeSettings.Seed);
                        var claimReceipt = await satoraService.ClaimAsync(swapId, destination, storeSettings.Seed);
                        logger.LogInformation("SatoraPlugin:ContinueSwap({SwapId}): claimed {Sats} sats → wallet Satora, ark_txid={Txid}",
                            swapId, claimReceipt.ClaimAmountSats, claimReceipt.ArkTxid);
                        if (dbSwap != null)
                            dbSwap.ClaimTxId = claimReceipt.ArkTxid;

                        var sweepTxId = await SweepToMerchantAsync(
                            dbSwap?.BTCPayInvoiceId,
                            claimReceipt.ClaimAmountSats,
                            storeSettings.Seed,
                            swapId,
                            dbSwap?.InvoiceArkadeAddress);

                        if (sweepTxId != null && dbSwap != null)
                            dbSwap.SweepTxId = sweepTxId;

                        action     = sweepTxId != null ? $"swept:{sweepTxId}" : $"claimed:{claimReceipt.ArkTxid}";*/
                        action = "claimed";

                        if (dbSwap != null && !string.IsNullOrEmpty(dbSwap.ClaimTxId))
                        {
                            ulong.TryParse(swap.ReceiveAmount, out var redeemedSats);
                            try
                            {
                                await settlementService.SettleAsync(dbSwap.BTCPayInvoiceId, dbSwap.ClaimTxId, redeemedSats, swapId);
                            }
                            catch (Exception se)
                            {
                                logger.LogWarning(se, "SatoraPlugin:ContinueSwap({SwapId}): settle retry failed", swapId);
                            }
                        }

                        swap = await satoraService.GetSwapAsync(swapId, storeSettings.Seed);
                        statusName = swap.Status.GetType().Name;
                        break;

                    case SwapStatus.ClientRedeeming:
                        action = "redeeming";
                        break;

                    case SwapStatus.ClientRedeemed:
                    case SwapStatus.ServerRedeemed:
                        /* if (dbSwap != null && !string.IsNullOrEmpty(dbSwap.ClaimTxId) && string.IsNullOrEmpty(dbSwap.SweepTxId))
                         {
                             ulong.TryParse(swap.ReceiveAmount, out var redeemedSats);
                             var retrySweepTxId = await SweepToMerchantAsync(
                                 dbSwap.BTCPayInvoiceId,
                                 redeemedSats,
                                 storeSettings.Seed,
                                 swapId,
                                 dbSwap.InvoiceArkadeAddress);

                             if (retrySweepTxId != null)
                                 dbSwap.SweepTxId = retrySweepTxId;
                         }*/
                        if (dbSwap != null && !string.IsNullOrEmpty(dbSwap.ClaimTxId))
                        {
                            ulong.TryParse(swap.ReceiveAmount, out var redeemedSats);
                            try
                            {
                                await settlementService.SettleAsync(dbSwap.BTCPayInvoiceId, dbSwap.ClaimTxId, redeemedSats, swapId);
                            }
                            catch (Exception se)
                            {
                                logger.LogWarning(se, "SatoraPlugin:ContinueSwap({SwapId}): settle retry failed", swapId);
                            }
                        }

                        action = "complete";
                        break;

                    default:
                        action = "terminal";
                        break;
                }

                if (dbSwap != null && (
                    dbSwap.Status     != statusName      ||
                    dbSwap.ClaimTxId  != originalClaimTxId ||
                    dbSwap.SweepTxId  != originalSweepTxId))
                {
                    dbSwap.Status = statusName;
                    _context.SatoraTransactions.Update(dbSwap);
                    await _context.SaveChangesAsync();
                }

                return (action, statusName);
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:ContinueSwapAsync({SwapId})", swapId);
                throw;
            }
        }

    /*    private async Task<string?> SweepToMerchantAsync(
            string? invoiceId,
            ulong amountSats,
            string satoraSeed,
            string swapId,
            string? invoiceAddress)
        {
            try
            {

                var sweepTxId = await satoraService.SweepToAddressAsync(invoiceAddress ?? "", amountSats, satoraSeed);
                logger.LogInformation(
                    "SatoraPlugin:SweepToMerchant({SwapId}): {Sats} sats swept to {Dest}, sweep_txid={Txid}",
                    swapId, amountSats, invoiceAddress, sweepTxId);
                return sweepTxId;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "SatoraPlugin:SweepToMerchant({SwapId}): sweep failed — " +
                    "{Sats} sats remain in the Satora wallet. " +
                    "The claim is preserved, the sweep will be retried on the next tick.",
                    swapId, amountSats);
                return null;
            }
        }*/
    }
}
