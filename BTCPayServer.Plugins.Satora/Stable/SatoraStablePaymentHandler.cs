using System.Globalization;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Satora.Sdk;
using SdkAddress = uniffi.satora_sdk_ffi.Address;
using SdkSwapDirection = uniffi.satora_sdk_ffi.SwapDirection;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;

namespace BTCPayServer.Plugins.Satora.Stable;

// Each prompt is a gasless swap from the coin to Arkade paying the amount due into the store's wallet,
// with a deposit address of its own that the customer sends the token to. Shaped after
// SatoraChainPaymentHandler.
public class SatoraStablePaymentHandler(
    SatoraStablecoin coin,
    BTCPayNetwork network,
    SatoraWalletRegistry registry,
    SatoraStableSwaps swaps,
    SatoraStableQuotes quotes,
    IConfiguration configuration) : IPaymentMethodHandler
{
    // The customer waits for this after picking the method; a cold quote adds a round trip to the swap's.
    public static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(20);

    public PaymentMethodId PaymentMethodId => coin.Id;

    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer(network.NBitcoinNetwork).Serializer;

    // A prompt waits for the customer to pick it whatever the store's lazy payment methods setting: an
    // active one would be a swap, and an address the worker reads, for every invoice. Invoice creation
    // stores the prompts only after this runs, while activation and renewal (InvoiceActivator) run it
    // on an invoice that has the prompt and must configure it.
    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = SatoraStablePaymentMethod.CryptoCode;
        context.Prompt.Divisibility = SatoraChainPaymentHandler.Divisibility;
        context.Prompt.PaymentMethodFee = 0m;
        context.Prompt.Inactive = context.InvoiceEntity.GetPaymentPrompt(PaymentMethodId) is null;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (context.InvoiceEntity.Type == InvoiceType.TopUp)
            throw new PaymentMethodUnavailableException("A Satora stablecoin payment needs an amount.");
        // An invoice made while the store offered the method can still be activated after it stopped.
        if (context.PaymentMethodConfig is null)
            throw new PaymentMethodUnavailableException($"The store no longer offers {coin.PrettyName}.");
        // Before the swap exists, so an invoice that cannot fit creates none on the swap server, which
        // BTCPay would ask for again each time the customer picks the method. Off mainnet the server's
        // VHTLC window is known only from a swap it made lately; without one the swap is made, and its
        // own cutoff decides (ToRow), as it always does.
        var now = DateTimeOffset.UtcNow;
        if ((SatoraStableActions.KnownVhtlcWindow(network.NBitcoinNetwork) ??
             await swaps.LastVhtlcWindowAsync(coin.Id.ToString(), now - SatoraStableActions.ObservedVhtlcWindowAge)) is { } window)
        {
            var expiresAt = context.InvoiceEntity.ExpirationTime;
            var cutoff = SatoraStableActions.FundingStartCutoff(now + window, network.NBitcoinNetwork);
            if (!SatoraStableActions.ExpiryFits(expiresAt, cutoff, network.NBitcoinNetwork))
                throw new PaymentMethodUnavailableException(
                    $"The invoice expires at {expiresAt:u}, too late for a payment to be funded before a swap's funding cutoff at about {cutoff:u}.");
        }
        var prompt = context.Prompt;
        var swap = await CreateSwapAsync(context.Store.Id, context.InvoiceEntity.Id,
            SatoraChainPaymentHandler.DueSats(prompt.Calculate().Due), context.InvoiceEntity.ExpirationTime, (decimal)context.InvoiceEntity.PaymentTolerance);
        prompt.Destination = swap.DepositAddress;
        context.TrackedDestinations.Add(swap.DepositAddress);
        prompt.Details = JObject.FromObject(new SatoraStablePromptDetails
        {
            SwapId = swap.SwapId,
            ChainId = swap.ChainId,
            Token = swap.Token,
            TokenAddress = swap.TokenAddress,
            Decimals = swap.Decimals,
            Units = swap.SourceUnits,
            ReceiveSats = swap.ReceiveSats
        }, Serializer);
    }

    protected virtual async Task<SatoraStableSwap> CreateSwapAsync(string storeId, string invoiceId, long sats, DateTimeOffset invoiceExpiresAt,
        decimal paymentTolerance)
    {
        if (sats <= 0)
            throw new PaymentMethodUnavailableException("Nothing is due.");
        if (network.NBitcoinNetwork.ChainName != ChainName.Mainnet && SatoraNetworks.EvmRpcUrl(configuration, coin.ChainId) is null)
            throw new PaymentMethodUnavailableException(
                $"Set {SatoraNetworks.EvmRpcKey(coin.ChainId)} to this network's {coin.ChainName} node to offer {coin.PrettyName}.");
        if (!(await registry.ReadStatusAsync(storeId)).Enabled)
            throw new PaymentMethodUnavailableException("The store's Satora wallet is not enabled.");
        // As in SatoraChainPaymentHandler: stops a wait BTCPay has stopped waiting for, and is not
        // disposed, as AsUnavailable's timeout of the same length can return first.
        var waitLimit = new CancellationTokenSource(CreateTimeout);
        return await SatoraChainPaymentHandler.AsUnavailable(async () =>
        {
            var quote = await quotes.GetAsync(storeId, coin, waitLimit.Token);
            // BTCPay settles against the tolerance the invoice was created with, which a Greenfield
            // request can set below the store's, while the settings page checks only the store's as a
            // coin is turned on. Below the floor an ordinary shortfall from the DEX would not settle the
            // invoice.
            var floor = SatoraStableActions.ToleranceFloorPercent(quote);
            if (paymentTolerance < floor)
                throw new PaymentMethodUnavailableException(string.Create(CultureInfo.InvariantCulture,
                    $"The invoice's payment tolerance of {paymentTolerance:0.##}% is below the {floor:0.#}% that {coin.PrettyName} needs."));
            CheckRange(sats, quote);
            return await registry.UseAsync(storeId, async wallet =>
            {
                // A disable may have finished while this waited for the wallet.
                if (!(await registry.ReadStatusAsync(storeId)).Enabled)
                    throw new PaymentMethodUnavailableException("The store's Satora wallet is not enabled.");
                var receiveTo = new SdkAddress.Arkade(await wallet.GetArkadeAddressAsync());
                var created = await wallet.Client.CreateEvmToArkadeSwapAsync(coin.SdkToken, (ulong)sats, receiveTo);
                // Written whatever becomes of the request once the swap exists, so the worker follows it.
                var row = ToRow(created, coin, storeId, invoiceId, network.NBitcoinNetwork, sats, quote, invoiceExpiresAt, DateTimeOffset.UtcNow, out var refusal);
                await swaps.AddAsync(row);
                return refusal is null ? row : throw refusal;
            }, waitLimit.Token);
        }, CreateTimeout, "stablecoin");
    }

    internal static void CheckRange(long sats, Quote quote)
    {
        var limit = sats < (long)quote.MinAmount ? SatoraSwapLimits.Limit(minimum: true, (long)quote.MinAmount)
            : sats > (long)quote.MaxAmount ? SatoraSwapLimits.Limit(minimum: false, (long)quote.MaxAmount)
            : null;
        if (limit is not null)
            throw new PaymentMethodUnavailableException($"Satora takes stablecoin payments of {limit}; this one is {sats} sats.");
    }

    // A swap the customer must not pay is still recorded, closed, and refused: nobody sees its address.
    // A response without a usable deposit address or locktimes is refused before anything is recorded.
    internal static SatoraStableSwap ToRow(SwapDetails created, SatoraStablecoin coin, string storeId, string invoiceId, Network network,
        long sats, Quote quote, DateTimeOffset invoiceExpiresAt, DateTimeOffset now, out Exception? refusal)
    {
        var deposit = (created.Funding as SdkSwapFunding.Gasless)?.depositAddress;
        if (created.Direction is not SdkSwapDirection.EvmToArkade || deposit is null || !SatoraEvmAddress.IsHex(deposit) ||
            created.VhtlcRefundLocktime is not { } vhtlcLocktime || created.EvmRefundLocktime is not { } evmLocktime)
            throw new InvalidOperationException($"The swap server returned swap {created.Id} without a valid deposit address or refund locktimes.");
        var vhtlcRefundLocktime = DateTimeOffset.FromUnixTimeSeconds(vhtlcLocktime);
        var row = new SatoraStableSwap
        {
            SwapId = created.Id,
            StoreId = storeId,
            InvoiceId = invoiceId,
            PaymentMethodId = coin.Id.ToString(),
            ChainId = coin.ChainId,
            Token = coin.Symbol,
            TokenAddress = coin.Contract,
            Decimals = coin.Decimals,
            DepositAddress = deposit,
            SourceUnits = created.DepositAmount,
            ReceiveSats = long.TryParse(created.ReceiveAmount, NumberStyles.None, CultureInfo.InvariantCulture, out var receive) ? receive : -1,
            Status = SatoraSwapStatuses.From(created.Status),
            VhtlcRefundLocktime = vhtlcRefundLocktime,
            EvmRefundLocktime = DateTimeOffset.FromUnixTimeSeconds(evmLocktime),
            FundingStartCutoff = SatoraStableActions.FundingStartCutoff(vhtlcRefundLocktime, network),
            InvoiceExpiresAt = invoiceExpiresAt,
            CreatedAt = now
        };
        refusal = created.DepositToken != coin.SdkToken
            ? new InvalidOperationException($"The swap server would take {created.DepositToken} instead of {coin.PrettyName}.")
            : row.ReceiveSats != sats
                ? new InvalidOperationException($"The swap server would pay {created.ReceiveAmount} sats into the wallet instead of {sats}.")
                : !SatoraStableActions.TryParseUnits(created.DepositAmount, out var units) || !SatoraStableActions.RateFits(units, sats, quote)
                    ? new InvalidOperationException($"The swap server asks the customer for {created.DepositAmount} units of {coin.PrettyName} to pay {sats} sats, too far from its quote.")
                    : !SatoraStableActions.ExpiryFits(invoiceExpiresAt, row.FundingStartCutoff, network)
                        ? new InvalidOperationException($"The invoice expires at {invoiceExpiresAt:u}, too late for a payment to be funded before the swap's funding cutoff at {row.FundingStartCutoff:u}.")
                        : null;
        if (refusal is not null)
            row.CompletedAt = now;
        return row;
    }

    public SatoraStablePromptDetails ParsePaymentPromptDetails(JToken details) =>
        details.ToObject<SatoraStablePromptDetails>(Serializer) ?? throw new FormatException($"Invalid {nameof(SatoraStablePromptDetails)}");

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details) => ParsePaymentPromptDetails(details);

    public object ParsePaymentMethodConfig(JToken config) =>
        config.ToObject<SatoraStablePaymentMethodConfig>(Serializer) ?? throw new FormatException($"Invalid {nameof(SatoraStablePaymentMethodConfig)}");

    public SatoraStablePaymentData ParsePaymentDetails(JToken details) =>
        details.ToObject<SatoraStablePaymentData>(Serializer) ?? throw new FormatException($"Invalid {nameof(SatoraStablePaymentData)}");

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details) => ParsePaymentDetails(details);
}
