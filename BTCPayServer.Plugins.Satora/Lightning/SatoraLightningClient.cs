using System.ComponentModel.DataAnnotations;
using System.Text;
using BTCPayServer.Lightning;
using BTCPayServer.Payments.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using NBitcoin;
using Satora.Sdk;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using SdkAddress = uniffi.satora_sdk_ffi.Address;
using SdkQuoteAmount = uniffi.satora_sdk_ffi.QuoteAmount;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;

namespace BTCPayServer.Plugins.Satora.Lightning;

// The store's Satora wallet as BTCPay's Lightning node, shaped after pseudozach's PR #2: invoices are
// Lightning to Arkade swaps paid into the wallet, payments Arkade to Lightning swaps paid from it
// (SatoraLightningPayments). Reads come from the plugin database, which SatoraLightningWorker and
// SatoraOutgoingWorker keep current; nothing here claims or refunds. ToString carries the
// connection key, so the client is never logged.
public class SatoraLightningClient(
    string storeId,
    string connectionKey,
    Network network,
    SatoraWalletRegistry registry,
    SatoraLightningSwaps swaps,
    SatoraLightningInvoiceHub invoiceHub,
    SatoraClaimWindow claimWindow,
    SatoraLightningPayments payments,
    ILogger<SatoraLightningClient> logger) : IExtendedLightningClient
{
    // The swap server's limit on the invoice description.
    private const int MaxDescriptionBytes = 639;
    // Most a swap may keep of the amount due. The server's defaults for this route take 0.3% plus a
    // sat; the ceiling leaves room for any fee a server could reasonably set, and still catches a
    // receive amount off by a unit (msat for sat, BTC for sat).
    public const int MaxFeePercent = 3;
    public const long MaxFeeFlatSats = 100;
    private const int ListLimit = 100;

    public string? DisplayName => "Satora (Arkade)";

    public Uri? ServerUri => null;

    public override string ToString() => SatoraLightningConnectionString.Format(storeId, connectionKey);

    public async Task<ValidationResult?> Validate()
    {
        var status = await registry.ReadStatusAsync(storeId);
        return status.Enabled
            ? ValidationResult.Success
            : new ValidationResult("The store's Satora wallet is not enabled.");
    }

    public Task<LightningInvoice> CreateInvoice(LightMoney amount, string description, TimeSpan expiry, CancellationToken cancellation = default) =>
        CreateInvoice(new CreateInvoiceParams(amount, description, expiry), cancellation);

    public async Task<LightningInvoice> CreateInvoice(CreateInvoiceParams request, CancellationToken cancellation = default)
    {
        var amountMsat = request.Amount?.MilliSatoshi ?? 0;
        if (amountMsat <= 0)
            throw new NotSupportedException("Satora cannot create an invoice without an amount.");
        // LNURL-pay asks for one; the swap server only takes a plain description.
        if (request.DescriptionHashOnly || request.DescriptionHash is not null)
            throw new NotSupportedException("Satora cannot create an invoice that commits to a description hash.");
        if (claimWindow.Current is { } window && !SatoraClaimWindow.Fits(request.Expiry, window))
            throw ExpiryTooLong(request.Expiry, window);
        await EnsureCurrentAsync();

        // Whole sats rounded up: BTCPay adds the difference to the due amount (AddTweakFee).
        var sats = (ulong)((amountMsat + 999) / 1000);
        var description = Truncate(request.Description);
        var swap = await registry.UseAsync(storeId, async wallet =>
        {
            // A key rotation or a disable may have finished while this waited for the wallet.
            await EnsureCurrentAsync();
            var receiveTo = new SdkAddress.Arkade(await wallet.GetArkadeAddressAsync(cancellation));
            // The server holds what the swap credits, after its fee, to the route's limits.
            var created = await SatoraSwapLimits.CheckAsync(() => wallet.Client.CreateLightningToArkadeSwapAsync(
                    new SdkQuoteAmount.Source(sats), receiveTo, invoiceDescription: description, cancellationToken: cancellation),
                limit => $"Satora takes Lightning payments of {limit} after its fee.");
            // BTCPay awaits this call whatever its timeout, so once the swap exists the row is written
            // even if the request was cancelled: a swap without a row could be paid and never claimed.
            var row = ToRow(created, storeId, network, request.Expiry, sats, DateTimeOffset.UtcNow, out var refusal);
            await swaps.AddAsync(row);
            if (refusal is not null)
                throw refusal;
            return row;
        }, cancellation);
        return SatoraLightningActions.ToInvoice(swap, DateTimeOffset.UtcNow);
    }

    private async Task EnsureCurrentAsync()
    {
        if (!registry.IsLightningKey(storeId, connectionKey))
            throw new UnauthorizedAccessException("This Satora connection string is no longer valid; its key was rotated.");
        if (!(await registry.ReadStatusAsync(storeId)).Enabled)
            throw new InvalidOperationException("The store's Satora wallet is not enabled.");
    }

    // A swap the plugin must not take payments on is still recorded, marked do-not-claim, and refused:
    // nobody sees its BOLT11. A response without a usable invoice is refused before anything is
    // recorded, as nobody can pay it.
    internal static SatoraLightningSwap ToRow(SwapDetails created, string storeId, Network network, TimeSpan requestedExpiry, ulong sats,
        DateTimeOffset now, out Exception? refusal)
    {
        var invoice = (created.Funding as SdkSwapFunding.Bolt11Invoice)?.invoice;
        if (invoice is null || !BOLT11PaymentRequest.TryParse(invoice, out var parsed, network) ||
            parsed is not { PaymentHash: { } paymentHash } bolt11 || created.VhtlcRefundLocktime is not { } locktime)
            throw new InvalidOperationException($"The swap server returned swap {created.Id} without a valid invoice or claim deadline.");
        var claimCutoff = SatoraClaimWindow.ClaimCutoff(locktime);
        var row = new SatoraLightningSwap
        {
            SwapId = created.Id,
            StoreId = storeId,
            Direction = SatoraLightningDirection.LightningToArkade,
            Status = SatoraSwapStatuses.From(created.Status),
            PaymentHash = paymentHash.ToString(),
            Bolt11 = invoice,
            AmountMsat = bolt11.MinimumAmount.MilliSatoshi,
            ReceiveSats = long.TryParse(created.ReceiveAmount, out var receive) ? receive : null,
            ClaimCutoff = claimCutoff,
            ExpiresAt = SatoraClaimWindow.ExpiresAt(bolt11.ExpiryDate, claimCutoff),
            CreatedAt = now
        };
        var window = SatoraClaimWindow.Observed(created.CreatedAt, locktime, now);
        refusal = row.AmountMsat != (long)sats * 1000
            ? new InvalidOperationException($"The swap server returned an invoice for {row.AmountMsat} msat instead of {sats} sats.")
            : row.ReceiveSats is not { } receiveSats || receiveSats <= 0 || receiveSats > (long)sats || (long)sats - receiveSats > MaxFeeSats((long)sats)
                ? new InvalidOperationException($"The swap server would pay {created.ReceiveAmount} sats into the wallet for an invoice of {sats} sats.")
                : !SatoraClaimWindow.Fits(requestedExpiry, window)
                    ? ExpiryTooLong(requestedExpiry, window)
                    : null;
        row.DoNotClaim = refusal is not null;
        return row;
    }

    public static long MaxFeeSats(long dueSats) => dueSats * MaxFeePercent / 100 + MaxFeeFlatSats;

    private static NotSupportedException ExpiryTooLong(TimeSpan requested, TimeSpan window) => new(
        $"The invoice expires in {(int)requested.TotalSeconds} s, but a Satora Lightning payment must be claimed within " +
        $"{(int)window.TotalSeconds} s of the invoice being created. Set the store's invoice expiry to at most " +
        $"{Math.Max(0, (int)SatoraClaimWindow.MaxInvoiceExpiry(window).TotalMinutes)} minutes to take Lightning payments.");

    private static string? Truncate(string? description)
    {
        if (string.IsNullOrEmpty(description))
            return null;
        if (Encoding.UTF8.GetByteCount(description) <= MaxDescriptionBytes)
            return description;
        var length = Math.Min(description.Length, MaxDescriptionBytes);
        while (Encoding.UTF8.GetByteCount(description.AsSpan(0, length)) > MaxDescriptionBytes)
            length--;
        if (length > 0 && char.IsHighSurrogate(description[length - 1]))
            length--;
        return description[..length];
    }

    public async Task<LightningInvoice> GetInvoice(string invoiceId, CancellationToken cancellation = default) =>
        await swaps.FindAsync(storeId, invoiceId, cancellation) is { } swap
            ? SatoraLightningActions.ToInvoice(swap, DateTimeOffset.UtcNow)
            : null!;

    public async Task<LightningInvoice> GetInvoice(uint256 paymentHash, CancellationToken cancellation = default) =>
        await swaps.FindByPaymentHashAsync(storeId, paymentHash.ToString(), cancellation) is { } swap
            ? SatoraLightningActions.ToInvoice(swap, DateTimeOffset.UtcNow)
            : null!;

    public Task<LightningInvoice[]> ListInvoices(CancellationToken cancellation = default) =>
        ListInvoices(new ListInvoicesParams(), cancellation);

    public async Task<LightningInvoice[]> ListInvoices(ListInvoicesParams request, CancellationToken cancellation = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await swaps.ListAsync(storeId, request.PendingOnly is true, now, ListLimit, cancellation);
        return rows.Select(row => SatoraLightningActions.ToInvoice(row, now)).ToArray();
    }

    public Task<ILightningInvoiceListener> Listen(CancellationToken cancellation = default) =>
        Task.FromResult(invoiceHub.Subscribe(storeId));

    // BTCPay calls this only for a BOLT11 a partial payment superseded. Not claiming lets the hold
    // expire, so the payer gets the payment back from Lightning. A swap whose claim already started
    // is still claimed, and credited once claimed.
    public async Task CancelInvoice(string invoiceId, CancellationToken cancellation = default)
    {
        if (await swaps.SetDoNotClaimAsync(storeId, invoiceId))
            logger.LogInformation("Satora Lightning swap {SwapId} will not be claimed: BTCPay cancelled its invoice", invoiceId);
        else if (await swaps.FindAsync(storeId, invoiceId, cancellation) is { ClaimStartedAt: not null })
            logger.LogWarning("BTCPay cancelled the invoice of Satora Lightning swap {SwapId} after its claim started; the claim goes on and may overpay the invoice if it lands", invoiceId);
    }

    // LightningLikePaymentHandler skips the node's block height check when GetInfo is not supported.
    public Task<LightningNodeInformation> GetInfo(CancellationToken cancellation = default) =>
        throw new NotSupportedException("The Satora wallet is not a Lightning node.");

    public async Task<LightningNodeBalance> GetBalance(CancellationToken cancellation = default)
    {
        var wallet = await registry.GetAsync(storeId);
        return ToNodeBalance(await wallet.Client.GetArkadeBalanceAsync(cancellation));
    }

    // Recoverable sats can only be spent after settling them, so they are not reported as spendable.
    public static LightningNodeBalance ToNodeBalance(ArkadeBalance balance) => new(null, new OffchainBalance
    {
        Local = LightMoney.Satoshis(balance.confirmedSats),
        Opening = LightMoney.Satoshis(balance.preConfirmedSats)
    });

    public Task<PayResponse> Pay(string bolt11, CancellationToken cancellation = default) =>
        Pay(bolt11, null!, cancellation);

    // Keysend: there is no invoice for the swap server to pay.
    public Task<PayResponse> Pay(PayInvoiceParams payParams, CancellationToken cancellation = default) =>
        Pay(null!, payParams, cancellation);

    public Task<PayResponse> Pay(string bolt11, PayInvoiceParams payParams, CancellationToken cancellation = default) =>
        payments.PayAsync(storeId, EnsureCurrentAsync, bolt11, payParams, network, cancellation);

    // BTCPay's payout listener cancels a payout whose payment it cannot find, so a stored one is always returned.
    public async Task<LightningPayment> GetPayment(string paymentHash, CancellationToken cancellation = default) =>
        (await payments.GetPaymentAsync(storeId, paymentHash))!;

    public Task<LightningPayment[]> ListPayments(CancellationToken cancellation = default) =>
        ListPayments(new ListPaymentsParams(), cancellation);

    public Task<LightningPayment[]> ListPayments(ListPaymentsParams request, CancellationToken cancellation = default) =>
        payments.ListPaymentsAsync(storeId, request);

    public Task<OpenChannelResponse> OpenChannel(OpenChannelRequest openChannelRequest, CancellationToken cancellation = default) =>
        throw new NotSupportedException("The Satora wallet has no Lightning channels.");

    public Task<BitcoinAddress> GetDepositAddress(CancellationToken cancellation = default) =>
        throw new NotSupportedException("The Satora wallet has no on-chain deposit address.");

    public Task<ConnectionResult> ConnectTo(NodeInfo nodeInfo, CancellationToken cancellation = default) =>
        throw new NotSupportedException("The Satora wallet does not connect to Lightning peers.");

    public Task<LightningChannel[]> ListChannels(CancellationToken cancellation = default) =>
        Task.FromResult(Array.Empty<LightningChannel>());
}
