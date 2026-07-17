using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Payments;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Services.Stores;
using BTCPayServer.Data;
using BTCPayServer.Lightning;
using BTCPayServer.Client.Models;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using NBitcoin.Payment;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using BTCPayServer.Rating;
using BTCPayServer.Services.Rates;
using BTCPayServer.Services.Invoices;
using uniffi.satora_sdk_ffi;
using System.Text;
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
        BTCPayNetworkProvider networkProvider,
        InvoiceRepository invoiceRepository)
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
                var swaps = txs.Select(CreateSwapListItem).ToList();

                return new SatoraModel
                {
                    Settings = settings,
                    Transactions = txs,
                    Swaps = swaps,
                    SwapSummary = CreateSwapSummary(swaps),
                    WalletBalance = walletBalance
                };
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

        private static SatoraSwapListItem CreateSwapListItem(SatoraTx tx)
        {
            return new SatoraSwapListItem
            {
                LocalTx = tx,
                Status = MapSwapStatus(tx.Status, tx.DateT)
            };
        }

        private static SatoraSwapSummary CreateSwapSummary(IEnumerable<SatoraSwapListItem> swaps)
        {
            var statuses = swaps.Select(s => s.Status).ToList();
            return new SatoraSwapSummary
            {
                Total = statuses.Count,
                Active = statuses.Count(s => !s.IsTerminal),
                NeedsAction = statuses.Count(s => s.NeedsManualAction),
                Stale = statuses.Count(s => s.IsStale),
                Completed = statuses.Count(s => s.RawStatus is "ClientRedeemed" or "ServerRedeemed")
            };
        }

        private static SatoraSwapStatusView MapSwapStatus(string? rawStatus, DateTime? createdAt)
        {
            var status = string.IsNullOrWhiteSpace(rawStatus) ? "Pending" : rawStatus;
            var age = createdAt.HasValue ? DateTime.UtcNow - createdAt.Value.ToUniversalTime() : TimeSpan.Zero;
            var stale = IsNonTerminalStatus(status) && age > TimeSpan.FromMinutes(30);

            var view = status switch
            {
                "Pending" => new SatoraSwapStatusView
                {
                    RawStatus = status,
                    DisplayStatus = "Waiting for deposit",
                    Severity = stale ? "warning" : "info",
                    ActionHint = stale
                        ? "No deposit has been detected for more than 30 minutes. Check the customer payment or retry the watcher step."
                        : "Waiting for the customer stablecoin deposit."
                },
                "ClientFundingSeen" => new SatoraSwapStatusView
                {
                    RawStatus = status,
                    DisplayStatus = "Deposit seen",
                    Severity = stale ? "warning" : "info",
                    ActionHint = stale
                        ? "Deposit was seen but has not progressed. Retry can re-check and fund the swap if the deposit is sufficient."
                        : "Deposit is visible; waiting for confirmation and funding."
                },
                "ClientFunded" => new SatoraSwapStatusView
                {
                    RawStatus = status,
                    DisplayStatus = "Deposit confirmed",
                    Severity = stale ? "warning" : "info",
                    ActionHint = stale
                        ? "Deposit is confirmed but the server side has not funded yet. Retry refreshes the state."
                        : "Waiting for Satora server funding."
                },
                "ServerFunded" => new SatoraSwapStatusView
                {
                    RawStatus = status,
                    DisplayStatus = "Ready to claim",
                    Severity = "warning",
                    ActionHint = "Swap is funded on the server side and needs the Arkade claim step."
                },
                "ClientRedeeming" => new SatoraSwapStatusView
                {
                    RawStatus = status,
                    DisplayStatus = "Claim in progress",
                    Severity = stale ? "warning" : "info",
                    ActionHint = stale
                        ? "Claim has been in progress for more than 30 minutes. Retry refreshes the claim state."
                        : "Arkade claim is in progress."
                },
                "ClientRedeemed" => new SatoraSwapStatusView
                {
                    RawStatus = status,
                    DisplayStatus = "Claimed",
                    Severity = "success",
                    ActionHint = "Arkade claim completed; waiting for final server redemption if needed.",
                    IsTerminal = true
                },
                "ServerRedeemed" => new SatoraSwapStatusView
                {
                    RawStatus = status,
                    DisplayStatus = "Complete",
                    Severity = "success",
                    ActionHint = "Swap completed.",
                    IsTerminal = true
                },
                "ClientRefunded" => RecoveryStatus(status, "Refunded", "Client-side funds were refunded. Reconcile the invoice and swap state."),
                "ClientFundedServerRefunded" => RecoveryStatus(status, "Needs retry", "Client funded, but server side is refunded. Retry should deliver funds to the BTC side once SDK recovery is exposed."),
                "ClientRefundedServerFunded" => RecoveryStatus(status, "Needs retry", "Client side is refunded while server side remains funded. Retry should reconcile and deliver funds to the BTC side once SDK recovery is exposed."),
                "ClientRefundedServerRefunded" => RecoveryStatus(status, "Refunded", "Both sides are refunded. Reconcile the invoice and swap state."),
                "Expired" => RecoveryStatus(status, "Expired", "Swap expired. Retry/recovery should be handled by the SDK contract manager; do not route merchant recovery to an EVM refund wallet."),
                "ClientInvalidFunded" => RecoveryStatus(status, "Invalid funding", "Customer funding did not match the expected swap terms. Retry/recovery should be handled by the SDK contract manager."),
                "ClientFundedTooLate" => RecoveryStatus(status, "Funded too late", "Customer funded after expiry. Retry/recovery should be handled by the SDK contract manager."),
                "ServerWontFund" => RecoveryStatus(status, "Server refused funding", "Satora server will not fund this swap. Retry/recovery should be handled by the SDK contract manager."),
                "ClientRedeemedAndClientRefunded" => RecoveryStatus(status, "Redeemed and refunded", "Swap has both redeemed and refunded markers. Reconcile before changing the invoice."),
                _ => RecoveryStatus(status, "Unknown status", "Unknown Satora status. Refresh or inspect the swap details before taking action.", canRetry: true, retryUnavailableReason: null)
            };

            view.NeedsManualAction = view.NeedsManualAction || stale;
            view.IsStale = stale;
            view.CanRetry = view.CanRetry || (!view.IsTerminal && IsRetryableStatus(status));

            return view;
        }

        private static SatoraSwapStatusView RecoveryStatus(
            string status,
            string displayStatus,
            string actionHint,
            bool canRetry = false,
            string? retryUnavailableReason = "Retry is pending SDK/contract-manager recovery support for this terminal state.")
        {
            return new SatoraSwapStatusView
            {
                RawStatus = status,
                DisplayStatus = displayStatus,
                Severity = "danger",
                ActionHint = actionHint,
                IsTerminal = true,
                NeedsManualAction = true,
                CanRetry = canRetry,
                RetryUnavailableReason = retryUnavailableReason
            };
        }

        private static bool IsRetryableStatus(string status)
        {
            return status is "Pending" or "ClientFundingSeen" or "ClientFunded" or "ServerFunded" or "ClientRedeeming" or "ClientRedeemed";
        }

        private static bool IsNonTerminalStatus(string status)
        {
            return status is "Pending" or "ClientFundingSeen" or "ClientFunded" or "ServerFunded" or "ClientRedeeming";
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

        public async Task<SwapResponse> CreateCheckoutSwapAsync(string storeId, SwapRequest req, CancellationToken cancellationToken = default)
        {
            await using var _context = pluginDbContextFactory.CreateContext();
            var settings = await _context.SatoraSettings.FirstOrDefaultAsync(a => a.StoreId == storeId, cancellationToken);
            if (settings == null || !IsWalletUsable(settings))
                throw new InvalidOperationException("Satora wallet is not enabled for this store.");

            var seed = settings.Seed;
            if (string.IsNullOrWhiteSpace(seed))
                throw new InvalidOperationException("Satora wallet seed is not configured for this store.");

            if (!SatoraData.AvailableCryptos.TryGetValue(req.CryptoFrom, out var blockchains) ||
                !blockchains.Contains(req.NetworkFrom))
                throw new InvalidOperationException("This stablecoin/network pair is not supported.");

            if (string.IsNullOrWhiteSpace(req.BtcPayInvoiceId))
                throw new InvalidOperationException("BTCPay invoice id is required.");

            var invoice = await invoiceRepository.GetInvoice(req.BtcPayInvoiceId);
            if (invoice is null || invoice.StoreId != storeId)
                throw new InvalidOperationException("BTCPay invoice was not found for this store.");

            if (invoice.Status is InvoiceStatus.Settled or InvoiceStatus.Invalid)
                throw new InvalidOperationException($"BTCPay invoice is {invoice.Status} and cannot accept a new Satora swap.");

            if (invoice.Status is InvoiceStatus.Expired || invoice.ExpirationTime <= DateTimeOffset.UtcNow)
                throw new InvalidOperationException("BTCPay invoice is expired. Create a new invoice before starting a Satora swap.");

            var prompt = invoice.GetPaymentPrompt(SatoraPaymentMethod.PaymentMethodId)
                         ?? throw new InvalidOperationException("The BTCPay invoice does not have a Satora payment prompt.");
            var due = prompt.Calculate().Due;
            if (due <= 0m)
                throw new InvalidOperationException("BTCPay invoice has no remaining Satora amount due.");

            var existing = await _context.SatoraTransactions
                .Where(tx =>
                    tx.StoreId == storeId &&
                    tx.BTCPayInvoiceId == invoice.Id &&
                    tx.Stablecoin == req.CryptoFrom.ToString() &&
                    tx.Blockchain == req.NetworkFrom.ToString() &&
                    (tx.Status == null || !SatoraSwapStatuses.Terminal.Contains(tx.Status)))
                .OrderByDescending(tx => tx.DateT)
                .FirstOrDefaultAsync(cancellationToken);

            if (existing is not null)
            {
                var existingResponse = await satoraService.GetSwapPaymentDetailsAsync(existing.TxID, req.CryptoFrom, seed);
                existingResponse.StatusMessage = "Existing active swap reused";
                return existingResponse;
            }

            req.BtcAmount = due;
            req.BtcNetwork = "ARKADE";
            req.BtcDestination = await satoraService.GetArkadeAddressAsync(seed);

            var quote = await satoraService.GetQuoteAsync(req, seed, cancellationToken);
            var dueSats = (ulong)Math.Round(due * 100000000m, MidpointRounding.AwayFromZero);
            if (dueSats < quote.MinAmount || dueSats > quote.MaxAmount)
            {
                throw new InvalidOperationException(
                    $"Satora quote amount is outside allowed limits. Due: {dueSats} sats; min: {quote.MinAmount} sats; max: {quote.MaxAmount} sats.");
            }

            var response = await satoraService.CreateSwapAsync(req, seed, quote);
            await _context.SatoraTransactions.AddAsync(new SatoraTx
            {
                StoreId = storeId,
                Blockchain = req.NetworkFrom.ToString(),
                Stablecoin = req.CryptoFrom.ToString(),
                DateT = DateTime.UtcNow,
                BTCAmount = (float)due,
                TxID = response.SwapId,
                BTCPayInvoiceId = invoice.Id,
                InvoiceArkadeAddress = req.BtcDestination,
                Status = "Pending"
            }, cancellationToken);
            await _context.SaveChangesAsync(cancellationToken);

            return response;
        }

        public async Task<string> ExportSwapsCsvAsync(string storeId, CancellationToken cancellationToken = default)
        {
            await using var _context = pluginDbContextFactory.CreateContext();
            var rows = await _context.SatoraTransactions
                .Where(tx => tx.StoreId == storeId)
                .OrderByDescending(tx => tx.DateT)
                .ToListAsync(cancellationToken);

            var csv = new StringBuilder();
            csv.AppendLine("created,invoice_id,swap_id,status,human_status,needs_action,stale,stablecoin,blockchain,btc_amount,claim_tx_id,sweep_tx_id,invoice_arkade_address");
            foreach (var tx in rows)
            {
                var status = MapSwapStatus(tx.Status, tx.DateT);
                AppendCsv(csv,
                    tx.DateT.ToString("O"),
                    tx.BTCPayInvoiceId,
                    tx.TxID,
                    status.RawStatus,
                    status.DisplayStatus,
                    status.NeedsManualAction.ToString(),
                    status.IsStale.ToString(),
                    tx.Stablecoin,
                    tx.Blockchain,
                    tx.BTCAmount.ToString("0.########"),
                    tx.ClaimTxId ?? "",
                    tx.SweepTxId ?? "",
                    tx.InvoiceArkadeAddress ?? "");
            }
            return csv.ToString();
        }

        private static void AppendCsv(StringBuilder csv, params string[] values)
        {
            csv.AppendLine(string.Join(",", values.Select(EscapeCsv)));
        }

        private static string EscapeCsv(string value)
        {
            if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
                return $"\"{value.Replace("\"", "\"\"")}\"";
            return value;
        }

        public async Task<string?> GetSeedPhraseBySwapId(string swapId)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var dbSwap    = await _context.SatoraTransactions.FirstOrDefaultAsync(s => s.TxID == swapId);
                if (dbSwap == null)
                    return null;

                var settings  = await _context.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == dbSwap.StoreId);
                return settings?.Seed;
            }
            catch (Exception e)
            {
                logger.LogError(e, "SatoraPlugin:GetSeedPhraseBySwapId()");
                throw;
            }
        }

        public async Task<SatoraSwapStatusView> DoGetSwapStatus(string swapId)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var dbSwap      = await _context.SatoraTransactions.FirstOrDefaultAsync(s => s.TxID == swapId);
                if (dbSwap == null)
                    throw new InvalidOperationException("Swap was not found in the local Satora transaction table.");

                var storeSettings = await _context.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == dbSwap.StoreId);
                if (storeSettings == null)
                    throw new InvalidOperationException("Satora settings were not found for the swap's store.");
                var seed = storeSettings.Seed;
                if (string.IsNullOrWhiteSpace(seed))
                    throw new InvalidOperationException("Satora wallet seed is not configured for this store.");

                var status      = await satoraService.GetSwapInfoAsync(swapId, seed);
                if (dbSwap.Status != status)
                {
                    dbSwap.Status = status;
                    _context.SatoraTransactions.Update(dbSwap);
                    await _context.SaveChangesAsync();
                }
                return MapSwapStatus(status, dbSwap.DateT);
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
            if (model.LocalTx != null)
                model.StatusView = MapSwapStatus(model.LocalTx.Status, model.LocalTx.DateT);

            var seed = storeSettings?.Seed;
            if (string.IsNullOrWhiteSpace(seed))
            {
                model.BackendError = "Satora wallet seed is not configured for this store.";
                return model;
            }

            try
            {
                var swap = await satoraService.GetSwapAsync(swapId, seed);
                model.BackendStatus  = swap.Status.GetType().Name;
                model.StatusView = MapSwapStatus(model.BackendStatus, model.LocalTx?.DateT);
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
                model.DerivedArkadeAddress = await satoraService.GetArkadeAddressAsync(seed);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "SatoraPlugin:GetSwapDetailsAsync({SwapId}): derived address lookup failed", swapId);
            }

            return model;
        }

        public async Task<(string action, SatoraSwapStatusView status)> RetrySwapAsync(string storeId, string swapId)
        {
            await using var _context = pluginDbContextFactory.CreateContext();
            var dbSwap = await _context.SatoraTransactions
                .FirstOrDefaultAsync(s => s.TxID == swapId && s.StoreId == storeId);
            if (dbSwap == null)
                throw new InvalidOperationException("Swap was not found in the local Satora transaction table.");

            var currentStatus = MapSwapStatus(dbSwap.Status, dbSwap.DateT);
            if (!IsRetryableStatus(currentStatus.RawStatus))
            {
                // TODO: Replace this guard with the SDK/contract-manager retry
                // entry point once it exists. Per Satora guidance, merchant
                // recovery should retry delivery to the BTC side, not expose an
                // EVM refund path to the merchant.
                throw new NotSupportedException(
                    $"Retry for terminal swap status {currentStatus.RawStatus} is pending SDK contract-manager recovery support.");
            }

            var (action, rawStatus) = await ContinueSwapAsync(swapId);
            return (action, MapSwapStatus(rawStatus, dbSwap.DateT));
        }

        private async Task MarkSwapProcessingAsync(SatoraTx dbSwap, string swapId)
        {
            if (string.IsNullOrWhiteSpace(dbSwap.BTCPayInvoiceId))
                return;

            var expectedSats = (ulong)Math.Round((decimal)dbSwap.BTCAmount * 100_000_000m);
            if (expectedSats == 0)
                return;

            try
            {
                await settlementService.MarkProcessingAsync(dbSwap.BTCPayInvoiceId, swapId, expectedSats);
            }
            catch (Exception pe)
            {
                logger.LogWarning(pe, "SatoraPlugin:ContinueSwap({SwapId}): processing payment marker failed", swapId);
            }
        }


        public async Task<(string action, string status)> ContinueSwapAsync(string swapId, string? destinationOverride = null)
        {
            try
            {
                await using var _context = pluginDbContextFactory.CreateContext();
                var dbSwap = await _context.SatoraTransactions.FirstOrDefaultAsync(s => s.TxID == swapId)
                             ?? throw new InvalidOperationException("Swap was not found in the local Satora transaction table.");

                var storeSettings = await _context.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == dbSwap.StoreId);
                if (storeSettings == null)
                    throw new InvalidOperationException("Satora settings were not found for the swap's store.");
                var seed = storeSettings.Seed;
                if (string.IsNullOrWhiteSpace(seed))
                    throw new InvalidOperationException("Satora wallet seed is not configured for this store.");

                var swap            = await satoraService.GetSwapAsync(swapId, seed);
                var statusName      = swap.Status.GetType().Name;
                var originalClaimTxId = dbSwap.ClaimTxId;
                var originalSweepTxId = dbSwap.SweepTxId;

                string action;
                switch (swap.Status)
                {
                    case SwapStatus.Pending:
                        var deposit = await satoraService.CheckDepositAsync(swapId, seed);
                        if (!deposit.HasSufficientSourceToken)
                        {
                            action = $"waiting_for_deposit ({deposit.SourceTokenBalance}/{deposit.SourceTokenRequired})";
                            break;
                        }
                        var fundReceipt = await satoraService.FundSwapAsync(swapId, seed);
                        logger.LogInformation("SatoraPlugin:ContinueSwap({SwapId}): funded, userOpHash={Hash}", swapId, fundReceipt.UserOpHash);
                        action     = $"funded:{fundReceipt.UserOpHash}";
                        await MarkSwapProcessingAsync(dbSwap, swapId);
                        swap       = await satoraService.GetSwapAsync(swapId, seed);
                        statusName = swap.Status.GetType().Name;
                        break;

                    case SwapStatus.ClientFundingSeen:
                    case SwapStatus.ClientFunded:
                        await MarkSwapProcessingAsync(dbSwap, swapId);
                        action = "waiting_for_server";
                        break;

                    case SwapStatus.ServerFunded:
                        await MarkSwapProcessingAsync(dbSwap, swapId);
                        var destination = destinationOverride
                            ?? await satoraService.GetArkadeAddressAsync(seed);
                        var claimReceipt = await satoraService.ClaimAsync(swapId, destination, seed);
                        logger.LogInformation("SatoraPlugin:ContinueSwap({SwapId}): claimed {Sats} sats → wallet Satora, ark_txid={Txid}",
                            swapId, claimReceipt.ClaimAmountSats, claimReceipt.ArkTxid);
                        dbSwap.ClaimTxId = claimReceipt.ArkTxid;
                        action = $"claimed:{claimReceipt.ArkTxid}";

                        await settlementService.SettleAsync(dbSwap.BTCPayInvoiceId, dbSwap.ClaimTxId, claimReceipt.ClaimAmountSats, swapId);

                        swap = await satoraService.GetSwapAsync(swapId, seed);
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
                                 seed,
                                 swapId,
                                 dbSwap.InvoiceArkadeAddress);

                             if (retrySweepTxId != null)
                                 dbSwap.SweepTxId = retrySweepTxId;
                         }*/
                        if (!string.IsNullOrEmpty(dbSwap.ClaimTxId))
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

                if (dbSwap.Status     != statusName      ||
                    dbSwap.ClaimTxId  != originalClaimTxId ||
                    dbSwap.SweepTxId  != originalSweepTxId)
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
