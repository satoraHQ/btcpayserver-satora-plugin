using System.Globalization;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using SdkAddress = uniffi.satora_sdk_ffi.Address;
using SdkSwapDirection = uniffi.satora_sdk_ffi.SwapDirection;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;

namespace BTCPayServer.Plugins.Satora.Chain;

// Each prompt is a Bitcoin to Arkade swap paying the amount due into the store's wallet. The customer
// pays the swap's fee on top, shown at checkout as the network cost, as BitcoinLikePaymentHandler
// shows its fee. Shaped after BitcoinLikePaymentHandler and LightningLikePaymentHandler.
public class SatoraChainPaymentHandler(
    BTCPayNetwork network,
    SatoraWalletRegistry registry,
    SatoraChainSwaps swaps) : IPaymentMethodHandler
{
    // The customer waits for this after picking the method. LightningLikePaymentHandler gives a node
    // 5 s; a swap also needs the swap server and, on a cold wallet, arkd.
    public static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(15);
    // Most the swap may add to the amount due. The server's defaults for this route take 0.3% plus
    // 200 sats; the ceiling still catches a fee off by a unit, which the customer would be shown.
    public const int MaxFeePercent = 3;
    public const long MaxFeeFlatSats = 2_000;
    // Whole sats, so the due BTCPay computes is exactly what the swap is asked to pay.
    public const int Divisibility = 8;

    public PaymentMethodId PaymentMethodId => SatoraChainPaymentMethod.Id;

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer(network.NBitcoinNetwork).Serializer;

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = network.CryptoCode;
        context.Prompt.Divisibility = Divisibility;
        context.Prompt.PaymentMethodFee = 0m;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.InvoiceEntity.Type == InvoiceType.TopUp)
            throw new PaymentMethodUnavailableException("A Satora on-chain payment needs an amount.");
        if (!SatoraChainActions.ExpiryFitsKnownWindow(context.InvoiceEntity.ExpirationTime, DateTimeOffset.UtcNow, network.NBitcoinNetwork))
            throw new PaymentMethodUnavailableException("The invoice expires too late to confirm a payment before a Satora on-chain swap's claim deadline.");
        var prompt = context.Prompt;
        // Before the fee is set, so the swap pays exactly what is due into the wallet.
        var swap = await CreateSwapAsync(context.Store.Id, context.InvoiceEntity.Id, DueSats(prompt.Calculate().Due), context.InvoiceEntity.ExpirationTime);
        prompt.Destination = swap.HtlcAddress;
        prompt.PaymentMethodFee = FeeBtc(swap);
        context.TrackedDestinations.Add(swap.HtlcAddress);
        prompt.Details = JObject.FromObject(new SatoraChainPromptDetails { SwapId = swap.SwapId, DepositSats = swap.DepositSats }, Serializer);
    }

    public static long DueSats(decimal dueBtc) => (long)decimal.Ceiling(dueBtc * 100_000_000m);

    public static decimal FeeBtc(SatoraChainSwap swap) => Money.Satoshis(swap.DepositSats - swap.ReceiveSats).ToDecimal(MoneyUnit.BTC);

    public static long MaxFeeSats(long dueSats) => dueSats * MaxFeePercent / 100 + MaxFeeFlatSats;

    protected virtual async Task<SatoraChainSwap> CreateSwapAsync(string storeId, string invoiceId, long sats, DateTimeOffset invoiceExpiresAt)
    {
        if (sats <= 0)
            throw new PaymentMethodUnavailableException("Nothing is due.");
        if (!(await registry.ReadStatusAsync(storeId)).Enabled)
            throw new PaymentMethodUnavailableException("The store's Satora wallet is not enabled.");
        // Stops a wait for a replace or eviction that BTCPay has stopped waiting for, so it creates no
        // swap for a prompt already marked unavailable. Not disposed: AsUnavailable's timeout of the
        // same length can return first, and the timer must still cancel the wait after this returns.
        var waitLimit = new CancellationTokenSource(CreateTimeout);
        return await AsUnavailable(() => registry.UseAsync(storeId, async wallet =>
        {
            // A disable may have finished while this waited for the wallet.
            if (!(await registry.ReadStatusAsync(storeId)).Enabled)
                throw new PaymentMethodUnavailableException("The store's Satora wallet is not enabled.");
            var receiveTo = new SdkAddress.Arkade(await wallet.GetArkadeAddressAsync());
            var created = await SatoraSwapLimits.CheckAsync(() => wallet.Client.CreateBitcoinToArkadeSwapAsync((ulong)sats, receiveTo),
                limit => $"Satora takes on-chain payments of {limit}; this one is {sats} sats");
            // Written whatever becomes of the request once the swap exists, so the worker follows it.
            var row = ToRow(created, storeId, invoiceId, network.NBitcoinNetwork, sats, invoiceExpiresAt, DateTimeOffset.UtcNow, out var refusal);
            await swaps.AddAsync(row);
            return refusal is null ? row : throw refusal;
        }, waitLimit.Token), CreateTimeout);
    }

    // Below the route's minimum, no liquidity, an unreachable server or a wallet that cannot be opened:
    // BTCPay leaves the method out of this invoice and logs why.
    internal static async Task<T> AsUnavailable<T>(Func<Task<T>> create, TimeSpan timeout)
    {
        try
        {
            return await create().WaitAsync(timeout);
        }
        catch (PaymentMethodUnavailableException)
        {
            throw;
        }
        catch (TimeoutException)
        {
            throw new PaymentMethodUnavailableException("The Satora swap server did not answer in time.");
        }
        catch (Exception e)
        {
            throw new PaymentMethodUnavailableException($"No Satora on-chain swap for this invoice ({e.Message})", e);
        }
    }

    // A swap the customer must not pay is still recorded, closed, and refused: nobody sees its address.
    // A response without a usable address or locktimes is refused before anything is recorded.
    internal static SatoraChainSwap ToRow(SwapDetails created, string storeId, string invoiceId, Network network, long sats,
        DateTimeOffset invoiceExpiresAt, DateTimeOffset now, out Exception? refusal)
    {
        var address = (created.Funding as SdkSwapFunding.BitcoinAddress)?.address;
        if (created.Direction is not SdkSwapDirection.BtcToArkade || address is null || !IsAddressOn(address, network) ||
            created.VhtlcRefundLocktime is not { } vhtlcLocktime || created.BtcRefundLocktime is not { } btcLocktime)
            throw new InvalidOperationException($"The swap server returned swap {created.Id} without a valid HTLC address or refund locktimes.");
        var row = new SatoraChainSwap
        {
            SwapId = created.Id,
            StoreId = storeId,
            InvoiceId = invoiceId,
            Status = SatoraSwapStatuses.From(created.Status),
            HtlcAddress = address,
            DepositSats = ParseSats(created.DepositAmount),
            ReceiveSats = ParseSats(created.ReceiveAmount),
            VhtlcRefundLocktime = DateTimeOffset.FromUnixTimeSeconds(vhtlcLocktime),
            BtcRefundLocktime = DateTimeOffset.FromUnixTimeSeconds(btcLocktime),
            InvoiceExpiresAt = invoiceExpiresAt,
            CreatedAt = now
        };
        refusal = row.ReceiveSats != sats
            ? new InvalidOperationException($"The swap server would pay {created.ReceiveAmount} sats into the wallet instead of {sats}.")
            : row.DepositSats < sats || row.DepositSats - sats > MaxFeeSats(sats)
                ? new InvalidOperationException($"The swap server asks the customer for {created.DepositAmount} sats to pay {sats}.")
                : !SatoraChainActions.ExpiryFits(invoiceExpiresAt, row.ClaimCutoff, network)
                    ? new InvalidOperationException($"The invoice expires at {invoiceExpiresAt:u}, too late to confirm a payment before the swap's claim deadline at {row.ClaimCutoff:u}.")
                    : null;
        if (refusal is not null)
            row.CompletedAt = now;
        return row;
    }

    private static long ParseSats(string amount) =>
        long.TryParse(amount, NumberStyles.None, CultureInfo.InvariantCulture, out var sats) ? sats : -1;

    public static bool IsAddressOn(string address, Network network)
    {
        try
        {
            BitcoinAddress.Create(address, network);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public SatoraChainPromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<SatoraChainPromptDetails>(Serializer) ?? throw new FormatException($"Invalid {nameof(SatoraChainPromptDetails)}");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public object ParsePaymentMethodConfig(JToken config) =>
        config.ToObject<SatoraChainPaymentMethodConfig>(Serializer) ?? throw new FormatException($"Invalid {nameof(SatoraChainPaymentMethodConfig)}");

    public SatoraChainPaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<SatoraChainPaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(SatoraChainPaymentData)}");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
