using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Payments;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Stores;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.Payment;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using uniffi.satora_sdk_ffi;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;

namespace BTCPayServer.Plugins.Satora.Services
{
    public class SatoraPluginService(
        SatoraPluginDbContextFactory pluginDbContextFactory,
        ILogger<SatoraPluginService> logger,
        SatoraService satoraService,
        SatoraSettlementService settlementService,
        StoreRepository storeRepository,
        RateFetcher rateFetcher,
        DefaultRulesCollection defaultRules,
        BTCPayNetworkProvider networkProvider)
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

                var walletBalance = await GetWalletBalance(settings);

                return new SatoraModel { Settings = settings, Transactions = txs, WalletBalance = walletBalance };
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:GetStoreData()");
                throw;
            }
        }

        public async Task<SatoraWalletBalance?> GetWalletBalance(string storeId, CancellationToken cancellationToken = default)
        {
            var settings = await GetStoreSettings(storeId);
            return await GetWalletBalance(settings, cancellationToken);
        }

        private async Task<SatoraWalletBalance?> GetWalletBalance(SatoraSettings settings, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(settings.Seed))
                return null;

            var arkadeBalance = await satoraService.GetArkadeBalanceAsync(settings.Seed);
            var balance = new SatoraWalletBalance
            {
                ConfirmedSats = arkadeBalance.confirmedSats,
                PreConfirmedSats = arkadeBalance.preConfirmedSats,
                RecoverableSats = arkadeBalance.recoverableSats,
                ConfirmedBtc = SatsToBtc(arkadeBalance.confirmedSats),
                PreConfirmedBtc = SatsToBtc(arkadeBalance.preConfirmedSats),
                RecoverableBtc = SatsToBtc(arkadeBalance.recoverableSats),
                RefreshedAt = DateTimeOffset.UtcNow
            };

            await AddFiatValue(settings.StoreId, balance, cancellationToken);
            return balance;
        }

        private async Task AddFiatValue(string storeId, SatoraWalletBalance balance, CancellationToken cancellationToken)
        {
            var store = await storeRepository.FindStore(storeId);
            if (store is null)
            {
                balance.FiatError = "Store not found.";
                return;
            }

            var storeBlob = store.GetStoreBlob();
            var fiatCurrency = storeBlob.DefaultCurrency;
            balance.FiatCurrency = fiatCurrency;

            if (string.Equals(fiatCurrency, "BTC", StringComparison.OrdinalIgnoreCase))
            {
                balance.BtcFiatRate = 1m;
                balance.ConfirmedFiat = balance.ConfirmedBtc;
                balance.PreConfirmedFiat = balance.PreConfirmedBtc;
                balance.RecoverableFiat = balance.RecoverableBtc;
                return;
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var rateResult = await rateFetcher.FetchRate(
                    new CurrencyPair("BTC", fiatCurrency),
                    storeBlob.GetRateRules(defaultRules),
                    new StoreIdRateContext(storeId),
                    cts.Token);

                if (rateResult.BidAsk is null)
                {
                    var errors = rateResult.Errors is null
                        ? string.Empty
                        : string.Join(", ", rateResult.Errors.OfType<object>());
                    balance.FiatError = string.IsNullOrEmpty(errors)
                        ? rateResult.EvaluatedRule
                        : $"{rateResult.EvaluatedRule} ({errors})";
                    return;
                }

                var rate = rateResult.BidAsk.Center;
                balance.BtcFiatRate = rate;
                balance.ConfirmedFiat = balance.ConfirmedBtc * rate;
                balance.PreConfirmedFiat = balance.PreConfirmedBtc * rate;
                balance.RecoverableFiat = balance.RecoverableBtc * rate;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SatoraPlugin:AddFiatValue(): failed to fetch BTC/{FiatCurrency} rate for store {StoreId}", fiatCurrency, storeId);
                balance.FiatError = ex.Message;
            }
        }

        private static decimal SatsToBtc(ulong sats)
        {
            return sats / 100_000_000m;
        }

        public async Task<SatoraWithdrawReview> ReviewWithdrawAsync(SatoraWithdrawRequest request, CancellationToken cancellationToken = default)
        {
            request.Destination = request.Destination?.Trim() ?? string.Empty;
            var review = new SatoraWithdrawReview
            {
                Request = request,
                NormalizedDestination = request.Destination
            };

            var settings = await GetStoreSettings(request.StoreId);
            if (!settings.Enabled || string.IsNullOrEmpty(settings.Seed))
                return WithdrawError(review, "Satora wallet is not enabled for this store.");

            var network = GetBitcoinNetwork();
            DetectWithdrawDestination(review, network);
            if (review.DestinationType == SatoraWithdrawDestinationType.Unknown)
                return WithdrawError(review, "Destination must be an Arkade address, BOLT11 invoice, LNURL, Lightning address, Bitcoin address, or BIP21 URI.");

            var requestedAmount = request.AmountBtc;
            if (review.DestinationType == SatoraWithdrawDestinationType.BitcoinOnChain)
                requestedAmount ??= TryGetBip21Amount(request.Destination, network);

            if (review.DestinationType == SatoraWithdrawDestinationType.LightningInvoice)
            {
                var invoice = BOLT11PaymentRequest.Parse(review.NormalizedDestination, network.NBitcoinNetwork);
                if (invoice.ExpiryDate <= DateTimeOffset.UtcNow)
                    return WithdrawError(review, "The BOLT11 invoice has expired.");
                if (invoice.MinimumAmount > LightMoney.Zero)
                    requestedAmount ??= invoice.MinimumAmount.ToUnit(LightMoneyUnit.BTC);
            }

            if (requestedAmount is null || requestedAmount <= 0)
                return WithdrawError(review, "Enter an amount greater than zero. Fixed-amount BOLT11 invoices and BIP21 URIs may provide the amount automatically.");

            review.AmountBtc = requestedAmount.Value;
            review.AmountSats = BtcToSats(requestedAmount.Value);
            if (review.AmountSats == 0)
                return WithdrawError(review, "Amount is below 1 sat.");

            var balance = await GetWalletBalance(settings, cancellationToken);
            review.AvailableSats = balance?.ConfirmedSats ?? 0;
            if (review.AmountSats > review.AvailableSats)
                return WithdrawError(review, $"Insufficient confirmed Arkade balance. Available: {review.AvailableSats} sats.");

            review.FeeDescription = FeeDescription(review.DestinationType);
            review.CanExecute = review.DestinationType is not SatoraWithdrawDestinationType.BitcoinOnChain;
            if (!review.CanExecute)
                // TODO: Enable this when the local Satora SDK exposes Arkade-to-on-chain BTC withdrawal/exit.
                review.Error = "On-chain Bitcoin withdrawal was detected, but the local Satora SDK does not expose an Arkade-to-on-chain exit/send API yet.";
            return review;
        }

        public async Task<SatoraWithdrawResult> ExecuteWithdrawAsync(SatoraWithdrawRequest request, CancellationToken cancellationToken = default)
        {
            var review = await ReviewWithdrawAsync(request, cancellationToken);
            if (!review.CanExecute)
                throw new InvalidOperationException(review.Error ?? "Withdraw request cannot be executed.");

            var settings = await GetStoreSettings(request.StoreId);
            if (string.IsNullOrEmpty(settings.Seed))
                throw new InvalidOperationException("Satora wallet is not enabled for this store.");

            var network = ToSatoraNetwork(GetBitcoinNetwork().NBitcoinNetwork);
            if (review.DestinationType == SatoraWithdrawDestinationType.Arkade)
            {
                var txid = await satoraService.SendArkadeAsync(review.NormalizedDestination, review.AmountSats, settings.Seed, network, cancellationToken);
                return new SatoraWithdrawResult
                {
                    DestinationType = review.DestinationType,
                    DestinationTypeDisplay = review.DestinationTypeDisplay,
                    Destination = review.NormalizedDestination,
                    AmountSats = review.AmountSats,
                    AmountBtc = review.AmountBtc,
                    TransactionId = txid,
                    Status = "Sent",
                    FeeDescription = review.FeeDescription
                };
            }

            return await satoraService.SendLightningAsync(review, settings.Seed, network, cancellationToken);
        }

        private void DetectWithdrawDestination(SatoraWithdrawReview review, BTCPayNetwork network)
        {
            var destination = review.NormalizedDestination;
            if (string.IsNullOrWhiteSpace(destination))
            {
                review.DestinationType = SatoraWithdrawDestinationType.Unknown;
                review.DestinationTypeDisplay = "Unknown";
                return;
            }

            if (destination.StartsWith("lightning:", StringComparison.OrdinalIgnoreCase))
            {
                destination = destination["lightning:".Length..];
                review.NormalizedDestination = destination;
            }

            if (destination.StartsWith("ark1", StringComparison.OrdinalIgnoreCase) ||
                destination.StartsWith("tark1", StringComparison.OrdinalIgnoreCase))
            {
                review.DestinationType = SatoraWithdrawDestinationType.Arkade;
                review.DestinationTypeDisplay = "Arkade";
                return;
            }

            if (BOLT11PaymentRequest.TryParse(destination, out var invoice, network.NBitcoinNetwork) && invoice is not null)
            {
                review.DestinationType = SatoraWithdrawDestinationType.LightningInvoice;
                review.DestinationTypeDisplay = "Lightning invoice";
                return;
            }

            if (destination.StartsWith("lnurl1", StringComparison.OrdinalIgnoreCase))
            {
                review.DestinationType = SatoraWithdrawDestinationType.Lnurl;
                review.DestinationTypeDisplay = "LNURL";
                return;
            }

            if (destination.Contains('@') && Uri.CheckHostName(destination.Split('@').LastOrDefault() ?? string.Empty) != UriHostNameType.Unknown)
            {
                review.DestinationType = SatoraWithdrawDestinationType.LightningAddress;
                review.DestinationTypeDisplay = "Lightning address";
                return;
            }

            try
            {
                var bip21 = new BitcoinUrlBuilder(destination, network.NBitcoinNetwork);
                if (bip21.Address is not null)
                {
                    review.DestinationType = SatoraWithdrawDestinationType.BitcoinOnChain;
                    review.DestinationTypeDisplay = "Bitcoin on-chain";
                    review.NormalizedDestination = bip21.Address.ToString();
                    return;
                }
            }
            catch (FormatException)
            {
            }

            try
            {
                review.NormalizedDestination = BitcoinAddress.Create(destination, network.NBitcoinNetwork).ToString();
                review.DestinationType = SatoraWithdrawDestinationType.BitcoinOnChain;
                review.DestinationTypeDisplay = "Bitcoin on-chain";
            }
            catch
            {
                review.DestinationType = SatoraWithdrawDestinationType.Unknown;
                review.DestinationTypeDisplay = "Unknown";
            }
        }

        private static decimal? TryGetBip21Amount(string destination, BTCPayNetwork network)
        {
            try
            {
                return new BitcoinUrlBuilder(destination, network.NBitcoinNetwork).Amount?.ToDecimal(MoneyUnit.BTC);
            }
            catch
            {
                return null;
            }
        }

        private BTCPayNetwork GetBitcoinNetwork()
        {
            return networkProvider.GetNetwork<BTCPayNetwork>("BTC")
                ?? networkProvider.DefaultNetwork as BTCPayNetwork
                ?? throw new InvalidOperationException("BTC network is not configured.");
        }

        private static SatoraWithdrawReview WithdrawError(SatoraWithdrawReview review, string error)
        {
            review.Error = error;
            review.CanExecute = false;
            review.FeeDescription = "Not available until the request is valid.";
            return review;
        }

        private static ulong BtcToSats(decimal btc)
        {
            return checked((ulong)decimal.Ceiling(btc * 100_000_000m));
        }

        private static string FeeDescription(SatoraWithdrawDestinationType destinationType)
        {
            return destinationType switch
            {
                SatoraWithdrawDestinationType.Arkade => "Native Arkade send. The current SDK does not expose a separate pre-send fee estimate.",
                SatoraWithdrawDestinationType.LightningInvoice or SatoraWithdrawDestinationType.LightningAddress or SatoraWithdrawDestinationType.Lnurl =>
                    "Arkade-to-Lightning swap. The exact funding amount and fee difference are returned by Satora at execution.",
                SatoraWithdrawDestinationType.BitcoinOnChain =>
                    "Arkade-to-on-chain exit requires SDK support before fees can be quoted.",
                _ => "Not available."
            };
        }

        private static SatoraBitcoinNetwork ToSatoraNetwork(Network network)
        {
            if (network == Network.Main)
                return SatoraBitcoinNetwork.Mainnet;
            if (network == Network.RegTest)
                return SatoraBitcoinNetwork.Regtest;
            return SatoraBitcoinNetwork.Testnet;
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
