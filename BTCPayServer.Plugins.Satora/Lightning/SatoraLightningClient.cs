using System.ComponentModel.DataAnnotations;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.EntityFrameworkCore;
using NBitcoin;
using Satora.Sdk;
using SatoraBitcoinNetwork = uniffi.satora_sdk_ffi.BitcoinNetwork;
using SatoraLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;
using SatoraSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;
using SatoraSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;

namespace BTCPayServer.Plugins.Satora.Lightning;

public class SatoraLightningClient(
    string storeId,
    Network network,
    SatoraPluginDbContextFactory dbContextFactory,
    SatoraPluginService pluginService,
    ILogger<SatoraLightningClient> logger) : IExtendedLightningClient
{
    private const string Incoming = "LightningToArkade";
    private const string Outgoing = "ArkadeToLightning";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);

    public string? DisplayName => "Satora Arkade";
    public Uri? ServerUri => new("satora://arkade-lightning");

    public override string ToString() => SatoraLightningConnectionStringHandler.ToConnectionString(storeId);

    public async Task<ValidationResult?> Validate()
    {
        var settings = await pluginService.GetStoreSettings(storeId);
        return settings.Enabled && !string.IsNullOrEmpty(settings.Seed)
            ? ValidationResult.Success
            : new ValidationResult("Satora wallet is not enabled for this store.");
    }

    public async Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default)
    {
        return await CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);
    }

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams createInvoiceRequest, CancellationToken cancellation = default)
    {
        var seed = await GetSeed(cancellation);
        var amountSats = ToSats(createInvoiceRequest.Amount);
        if (amountSats <= 0)
            throw new NotSupportedException("Satora Lightning invoices must have a fixed positive amount.");

        using var client = CreateSdkClient(seed);
        var swap = await client.CreateLightningToArkadeSwapAsync((ulong)amountSats, receiveTo: null, cancellationToken: cancellation);
        var bolt11 = swap.Funding is SatoraSwapFunding.Bolt11Invoice invoice
            ? invoice.invoice
            : throw new InvalidOperationException("Satora did not return a Lightning invoice for the swap.");
        var parsed = BOLT11PaymentRequest.Parse(bolt11, network);
        var now = DateTimeOffset.UtcNow;
        var record = new SatoraLightningSwap
        {
            SwapId = swap.Id,
            StoreId = storeId,
            Direction = Incoming,
            Bolt11 = bolt11,
            PaymentHash = parsed.PaymentHash.ToString(),
            AmountSats = amountSats,
            Status = StatusName(swap.Status),
            Created = now,
            ExpiresAt = parsed.ExpiryDate
        };

        await using var db = dbContextFactory.CreateContext();
        db.SatoraLightningSwaps.Add(record);
        await db.SaveChangesAsync(cancellation);
        return ToLightningInvoice(record, parsed, swap.Status);
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var record = await db.SatoraLightningSwaps.FirstOrDefaultAsync(s => s.StoreId == storeId && s.SwapId == invoiceId, cancellation);
        if (record is null)
            return null!;
        return await RefreshInvoice(record, cancellation);
    }

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var hash = paymentHash.ToString();
        var record = await db.SatoraLightningSwaps.FirstOrDefaultAsync(s => s.StoreId == storeId && s.PaymentHash == hash, cancellation);
        if (record is null)
            return null!;
        return await RefreshInvoice(record, cancellation);
    }

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default)
    {
        return ListInvoices(new ListInvoicesParams(), cancellation);
    }

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var records = await db.SatoraLightningSwaps
            .Where(s => s.StoreId == storeId && s.Direction == Incoming)
            .OrderByDescending(s => s.Created)
            .Take(100)
            .ToListAsync(cancellation);
        var invoices = new List<LightningInvoice>();
        foreach (var record in records)
        {
            var invoice = await RefreshInvoice(record, cancellation);
            if (request.PendingOnly is true && invoice.Status != LightningInvoiceStatus.Unpaid)
                continue;
            invoices.Add(invoice);
        }
        return invoices.ToArray();
    }

    public async Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default)
    {
        await Task.CompletedTask;
        return new SatoraLightningInvoiceListener(this);
    }

    public async Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default)
    {
        return await Pay(bolt11, new PayInvoiceParams(), cancellation);
    }

    public async Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        await Task.CompletedTask;
        return new PayResponse(PayResult.Error, "Satora requires a BOLT11 invoice to pay Lightning.");
    }

    public async Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default)
    {
        BOLT11PaymentRequest parsed;
        try
        {
            parsed = BOLT11PaymentRequest.Parse(bolt11, network);
        }
        catch (Exception ex)
        {
            return new PayResponse(PayResult.Error, ex.Message);
        }

        var amount = parsed.MinimumAmount;
        if (amount == LightMoney.Zero && payParams.Amount is not null)
            amount = payParams.Amount;
        if (amount == LightMoney.Zero)
            return new PayResponse(PayResult.Error, "Satora requires a fixed-amount BOLT11 invoice.");

        var seed = await GetSeed(cancellation);
        using var client = CreateSdkClient(seed);
        var swap = await client.CreateArkadeToLightningSwapAsync(new SatoraLightningDestination.Invoice(bolt11), cancellation);
        if (swap.Funding is not SatoraSwapFunding.ArkadeAddress funding)
            return new PayResponse(PayResult.Error, "Satora did not return an Arkade funding address.");

        var fundingSats = ParseSats(swap.DepositAmount);
        var txid = await client.SendArkadeAsync(funding.address, (ulong)fundingSats, cancellation);
        var record = new SatoraLightningSwap
        {
            SwapId = swap.Id,
            StoreId = storeId,
            Direction = Outgoing,
            Bolt11 = bolt11,
            PaymentHash = parsed.PaymentHash.ToString(),
            AmountSats = ToSats(amount),
            FundingAmountSats = fundingSats,
            FundingAddress = funding.address,
            FundingTxId = txid,
            Status = StatusName(swap.Status),
            Created = DateTimeOffset.UtcNow,
            ExpiresAt = parsed.ExpiryDate
        };

        await using (var db = dbContextFactory.CreateContext())
        {
            db.SatoraLightningSwaps.Add(record);
            await db.SaveChangesAsync(cancellation);
        }

        var timeout = payParams.SendTimeout ?? PayInvoiceParams.DefaultSendTimeout;
        SatoraSwapStatus status;
        try
        {
            status = await client.WaitForSwapStatusAsync(
                swap.Id,
                new SatoraSwapStatus[] { new SatoraSwapStatus.ServerFunded(), new SatoraSwapStatus.ServerRedeemed(), new SatoraSwapStatus.Expired(), new SatoraSwapStatus.ServerWontFund(), new SatoraSwapStatus.ClientInvalidFunded() },
                timeout,
                cancellation);
            await UpdateStatus(record.SwapId, status, DateTimeOffset.UtcNow, cancellation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation(ex, "Satora Lightning: outbound swap {SwapId} is still pending after initial wait", record.SwapId);
            return new PayResponse(PayResult.Unknown, new PayDetails
            {
                Status = LightningPaymentStatus.Pending,
                PaymentHash = parsed.PaymentHash,
                TotalAmount = LightMoney.Satoshis(fundingSats),
                FeeAmount = LightMoney.Satoshis(Math.Max(0, fundingSats - record.AmountSats))
            });
        }

        if (!IsOutgoingPaid(status))
            return new PayResponse(PayResult.Error, $"Satora swap ended with status {StatusName(status)}.");

        return new PayResponse(PayResult.Ok, new PayDetails
        {
            Status = LightningPaymentStatus.Complete,
            PaymentHash = parsed.PaymentHash,
            TotalAmount = LightMoney.Satoshis(fundingSats),
            FeeAmount = LightMoney.Satoshis(Math.Max(0, fundingSats - record.AmountSats))
        });
    }

    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var record = await db.SatoraLightningSwaps.FirstOrDefaultAsync(s => s.StoreId == storeId && s.Direction == Outgoing && s.PaymentHash == paymentHash, cancellation);
        if (record is null)
            return null!;
        return await RefreshPayment(record, cancellation);
    }

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default)
    {
        return ListPayments(new ListPaymentsParams { IncludePending = true }, cancellation);
    }

    public async Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default)
    {
        await using var db = dbContextFactory.CreateContext();
        var records = await db.SatoraLightningSwaps
            .Where(s => s.StoreId == storeId && s.Direction == Outgoing)
            .OrderByDescending(s => s.Created)
            .Take(100)
            .ToListAsync(cancellation);
        var payments = new List<LightningPayment>();
        foreach (var record in records)
        {
            var payment = await RefreshPayment(record, cancellation);
            if (request.IncludePending is false && payment.Status == LightningPaymentStatus.Pending)
                continue;
            payments.Add(payment);
        }
        return payments.ToArray();
    }

    public async Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default)
    {
        await GetSeed(cancellation);
        return new LightningNodeInformation
        {
            Alias = "Satora Arkade",
            Version = "Satora SDK",
            BlockHeight = 0
        };
    }

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var seed = await GetSeed(cancellation);
        using var client = CreateSdkClient(seed);
        var balance = await client.GetArkadeBalanceAsync(cancellation);
        return new LightningNodeBalance(
            new OnchainBalance
            {
                Confirmed = Money.Zero,
                Unconfirmed = Money.Zero,
                Reserved = Money.Zero
            },
            new OffchainBalance
            {
                Opening = LightMoney.Zero,
                Local = LightMoney.Satoshis(balance.confirmedSats),
                Remote = LightMoney.Zero,
                Closing = LightMoney.Satoshis(balance.recoverableSats)
            });
    }

    public Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        return Task.CompletedTask;
    }

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Satora does not expose Lightning channels.");
    }

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Use the Satora Arkade boarding address instead.");
    }

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default)
    {
        throw new NotSupportedException("Satora does not expose peer connections.");
    }

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default)
    {
        return Task.FromResult(Array.Empty<LightningChannel>());
    }

    internal async Task<LightningInvoice> WaitAnyInvoice(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            await using (var db = dbContextFactory.CreateContext())
            {
                var records = await db.SatoraLightningSwaps
                    .Where(s => s.StoreId == storeId && s.Direction == Incoming && s.PaidAt == null && s.Status != "Expired")
                    .OrderBy(s => s.Created)
                    .Take(100)
                    .ToListAsync(cancellation);
                foreach (var record in records)
                {
                    var invoice = await RefreshInvoice(record, cancellation);
                    if (invoice.Status is LightningInvoiceStatus.Paid or LightningInvoiceStatus.Expired)
                        return invoice;
                }
            }
            await Task.Delay(PollInterval, cancellation);
        }
    }

    private async Task<LightningInvoice> RefreshInvoice(SatoraLightningSwap record, CancellationToken cancellation)
    {
        var seed = await GetSeed(cancellation);
        using var client = CreateSdkClient(seed);
        var swap = await client.GetSwapAsync(record.SwapId, cancellation);
        if (IsIncomingClaimable(swap.Status) && string.IsNullOrEmpty(record.ClaimTxId))
        {
            try
            {
                var address = await client.GetArkadeAddressAsync(cancellation);
                var claim = await client.ClaimAsync(record.SwapId, address, cancellation);
                record.ClaimTxId = claim.ArkTxid;
                record.PaidAt ??= DateTimeOffset.UtcNow;
                logger.LogInformation("Satora Lightning: claimed inbound swap {SwapId} into Arkade tx {Txid}", record.SwapId, claim.ArkTxid);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Satora Lightning: failed claiming inbound swap {SwapId}", record.SwapId);
            }
        }

        record.Status = StatusName(swap.Status);
        if (IsIncomingPaid(swap.Status) || !string.IsNullOrEmpty(record.ClaimTxId))
            record.PaidAt ??= DateTimeOffset.UtcNow;
        await SaveRecord(record, cancellation);
        return ToLightningInvoice(record, BOLT11PaymentRequest.Parse(record.Bolt11, network), swap.Status);
    }

    private async Task<LightningPayment> RefreshPayment(SatoraLightningSwap record, CancellationToken cancellation)
    {
        var seed = await GetSeed(cancellation);
        using var client = CreateSdkClient(seed);
        var swap = await client.GetSwapAsync(record.SwapId, cancellation);
        record.Status = StatusName(swap.Status);
        if (IsOutgoingPaid(swap.Status))
            record.PaidAt ??= DateTimeOffset.UtcNow;
        await SaveRecord(record, cancellation);
        return ToLightningPayment(record, BOLT11PaymentRequest.Parse(record.Bolt11, network), swap.Status);
    }

    private async Task SaveRecord(SatoraLightningSwap record, CancellationToken cancellation)
    {
        await using var db = dbContextFactory.CreateContext();
        db.SatoraLightningSwaps.Update(record);
        await db.SaveChangesAsync(cancellation);
    }

    private async Task UpdateStatus(string swapId, SatoraSwapStatus status, DateTimeOffset paidAt, CancellationToken cancellation)
    {
        await using var db = dbContextFactory.CreateContext();
        var record = await db.SatoraLightningSwaps.FindAsync(new object[] { swapId }, cancellation);
        if (record is null)
            return;
        record.Status = StatusName(status);
        if (IsOutgoingPaid(status))
            record.PaidAt ??= paidAt;
        await db.SaveChangesAsync(cancellation);
    }

    private LightningInvoice ToLightningInvoice(SatoraLightningSwap record, BOLT11PaymentRequest parsed, SatoraSwapStatus status)
    {
        return new LightningInvoice
        {
            Id = record.SwapId,
            PaymentHash = record.PaymentHash,
            Status = ToInvoiceStatus(record, status),
            BOLT11 = record.Bolt11,
            PaidAt = record.PaidAt,
            ExpiresAt = record.ExpiresAt,
            Amount = parsed.MinimumAmount == LightMoney.Zero ? LightMoney.Satoshis(record.AmountSats) : parsed.MinimumAmount,
            AmountReceived = record.PaidAt is null ? null : parsed.MinimumAmount == LightMoney.Zero ? LightMoney.Satoshis(record.AmountSats) : parsed.MinimumAmount
        };
    }

    private LightningPayment ToLightningPayment(SatoraLightningSwap record, BOLT11PaymentRequest parsed, SatoraSwapStatus status)
    {
        var funding = record.FundingAmountSats ?? record.AmountSats;
        return new LightningPayment
        {
            Id = record.SwapId,
            PaymentHash = record.PaymentHash,
            Status = ToPaymentStatus(status),
            BOLT11 = record.Bolt11,
            CreatedAt = record.Created,
            Amount = LightMoney.Satoshis(record.AmountSats),
            AmountSent = LightMoney.Satoshis(funding),
            Fee = LightMoney.Satoshis(Math.Max(0, funding - record.AmountSats))
        };
    }

    private LightningInvoiceStatus ToInvoiceStatus(SatoraLightningSwap record, SatoraSwapStatus status)
    {
        if (record.PaidAt is not null || IsIncomingPaid(status))
            return LightningInvoiceStatus.Paid;
        if (status is SatoraSwapStatus.Expired || DateTimeOffset.UtcNow > record.ExpiresAt)
            return LightningInvoiceStatus.Expired;
        return LightningInvoiceStatus.Unpaid;
    }

    private LightningPaymentStatus ToPaymentStatus(SatoraSwapStatus status)
    {
        if (IsOutgoingPaid(status))
            return LightningPaymentStatus.Complete;
        if (status is SatoraSwapStatus.Expired or SatoraSwapStatus.ServerWontFund or SatoraSwapStatus.ClientInvalidFunded)
            return LightningPaymentStatus.Failed;
        return LightningPaymentStatus.Pending;
    }

    private async Task<string> GetSeed(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var settings = await pluginService.GetStoreSettings(storeId);
        if (!settings.Enabled || string.IsNullOrEmpty(settings.Seed))
            throw new InvalidOperationException("Satora wallet is not enabled for this store.");
        return settings.Seed;
    }

    private global::Satora.Sdk.Client CreateSdkClient(string seed)
    {
        return new global::Satora.Sdk.Client(seed, ToSatoraNetwork(network));
    }

    private static SatoraBitcoinNetwork ToSatoraNetwork(Network network)
    {
        if (network == Network.Main)
            return SatoraBitcoinNetwork.Mainnet;
        if (network == Network.RegTest)
            return SatoraBitcoinNetwork.Regtest;
        return SatoraBitcoinNetwork.Testnet;
    }

    private static long ToSats(LightMoney amount)
    {
        return checked((long)decimal.Ceiling(amount.ToUnit(LightMoneyUnit.Satoshi)));
    }

    private static long ParseSats(string amount)
    {
        return checked(long.Parse(amount, System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool IsIncomingClaimable(SatoraSwapStatus status)
    {
        return status is SatoraSwapStatus.ServerFunded;
    }

    private static bool IsIncomingPaid(SatoraSwapStatus status)
    {
        return status is SatoraSwapStatus.ClientRedeemed or SatoraSwapStatus.ServerRedeemed;
    }

    private static bool IsOutgoingPaid(SatoraSwapStatus status)
    {
        return status is SatoraSwapStatus.ServerFunded or SatoraSwapStatus.ServerRedeemed;
    }

    private static string StatusName(SatoraSwapStatus status) => status.GetType().Name;

    private sealed class SatoraLightningInvoiceListener(SatoraLightningClient client) : ILightningInvoiceListener
    {
        public Task<LightningInvoice> WaitInvoice(CancellationToken cancellation) => client.WaitAnyInvoice(cancellation);
        public void Dispose()
        {
        }
    }
}
