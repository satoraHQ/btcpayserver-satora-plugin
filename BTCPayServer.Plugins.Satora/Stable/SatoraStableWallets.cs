using System.Numerics;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using NBitcoin;
using Satora.Sdk;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;
using SdkArkadeSpend = uniffi.satora_sdk_ffi.ArkadeSpend;
using SdkRelayFundingState = uniffi.satora_sdk_ffi.RelayFundingState;

namespace BTCPayServer.Plugins.Satora.Stable;

// The SDK calls SatoraStableWorker makes. Status reads and claims go through the same registry access
// as the other swaps' claims; the relay funding and its reconcile, which write the SDK's journal of the
// swap, through UseAsync as well, so a seed replace waits for them. Virtual so the worker's tests run
// without a swap server, arkd or EVM node.
public class SatoraStableWallets(SatoraLightningWallets wallets, SatoraWalletRegistry registry, IConfiguration configuration, BTCPayNetwork network)
{
    // Bounds the relay's wait for its funding receipt too. A call cut short after the request left
    // makes the next one throw UnresolvedFunding, which a reconcile resolves.
    public static readonly TimeSpan FundTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan ReconcileTimeout = TimeSpan.FromSeconds(60);

    public virtual Task<BulkStatus> GetBulkStatusAsync(string storeId, IEnumerable<string> swapIds, CancellationToken cancellationToken) =>
        wallets.GetBulkStatusAsync(storeId, swapIds, cancellationToken);

    public virtual Task<string> GetClaimAddressAsync(string storeId, CancellationToken cancellationToken) =>
        wallets.GetClaimAddressAsync(storeId, cancellationToken);

    // A VHTLC the server funded below the swap's target, as when the deposit converted to less than
    // quoted, is claimed only within shortfallLimit, in basis points of the target, and only if the
    // swap's own lock, read through the chain's node, shows that shortfall. Both are set on the client
    // right before the claim; the worker claims one stablecoin swap of a store at a time, and nothing
    // else sets the limit, so the limit in force is this swap's.
    public virtual async Task<SdkArkadeSettlement> ClaimAsync(SatoraStableSwap swap, string destination, uint shortfallLimit,
        CancellationToken cancellationToken)
    {
        return await registry.UseAsync(swap.StoreId, async wallet =>
        {
            wallet.Client.SetConversionShortfallLimit(shortfallLimit);
            // The claim goes ahead without the node, or without its setting: one under way, whose
            // preimage arkd may hold, reads no lock and must finish before the VHTLC locktime, and the SDK
            // refuses a short new one without a reader.
            Exception? readerFailure = null;
            try
            {
                await wallet.Client.SetEvmReaderAsync(checked((ulong)swap.ChainId), NodeRpcUrl(swap), cancellationToken);
            }
            catch (Exception e) when (e is SdkException or InvalidOperationException)
            {
                readerFailure = e;
            }
            try
            {
                return await wallet.Client.SettleAsync(swap.SwapId, SdkArkadeSpend.Claim, destination);
            }
            catch (Exception e) when (readerFailure is not null)
            {
                throw new AggregateException($"Claiming Satora stablecoin swap {swap.SwapId} failed, after its chain's node could not be set as the SDK's EVM reader", e, readerFailure);
            }
        }, cancellationToken);
    }

    public virtual Task<bool> ClaimPossibleAsync(string storeId, string swapId) =>
        wallets.ClaimPossibleAsync(storeId, swapId);

    // What arkd's indexer shows of the VHTLC.
    public virtual async Task<VhtlcAmounts> AmountsAsync(string storeId, string swapId, CancellationToken cancellationToken) =>
        await (await registry.GetAsync(storeId, cancellationToken)).Client.AmountsForSwapAsync(swapId, cancellationToken);

    // Everything the VHTLC ever held, spent or not, for a claim settled in a batch, whose receipt has
    // no amount. The amount is written once, so an indexer that shows nothing yet fails the claim,
    // which the next call resumes from the SDK's journal before reading again.
    public async Task<long> VhtlcSatsAsync(string storeId, string swapId, CancellationToken cancellationToken)
    {
        var amounts = await AmountsAsync(storeId, swapId, cancellationToken);
        var sats = checked((long)(amounts.SpendableSats + amounts.RecoverableSats + amounts.SpentSats));
        return sats > 0 ? sats : throw new InvalidOperationException($"The Arkade indexer shows nothing in the VHTLC of swap {swapId} yet.");
    }

    // The swap's token at its deposit address, in the token's smallest unit.
    public virtual async Task<BigInteger> GetDepositedAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
    {
        var node = NodeRpcUrl(swap);
        var wallet = await registry.GetAsync(swap.StoreId, cancellationToken);
        var balance = await wallet.Client.GetDepositorBalanceAsync(swap.SwapId, token: null, node, cancellationToken);
        return SatoraStableActions.Units(balance.TokenUnits) ?? throw new FormatException($"The SDK returned an invalid token balance, {balance.TokenUnits}.");
    }

    public virtual async Task<RelayFunding> FundAsync(SatoraStableSwap swap, TimeSpan permitDeadline, CancellationToken cancellationToken)
    {
        var node = NodeRpcUrl(swap);
        return await registry.UseAsync(swap.StoreId, wallet =>
            wallet.Client.FundSwapViaRelayAsync(swap.SwapId, permitDeadline, FundTimeout, nodeRpcUrl: node), cancellationToken);
    }

    public virtual async Task<SdkRelayFundingState> ReconcileAsync(SatoraStableSwap swap, CancellationToken cancellationToken)
    {
        var node = NodeRpcUrl(swap);
        return await registry.UseAsync(swap.StoreId, wallet =>
            wallet.Client.ReconcileRelayFundingAsync(swap.SwapId, ReconcileTimeout, node), cancellationToken);
    }

    // Null takes the SDK's public node for the chain, which is a mainnet one. A test fork keeps mainnet's
    // chain id, so off mainnet the SDK would read mainnet without noticing: an empty deposit address,
    // and a relay journal judged against mainnet's nonces and time.
    private string? NodeRpcUrl(SatoraStableSwap swap) =>
        SatoraNetworks.EvmRpcUrl(configuration, swap.ChainId) ??
        (network.NBitcoinNetwork.ChainName == ChainName.Mainnet
            ? null
            : throw new InvalidOperationException(
                $"Set {SatoraNetworks.EvmRpcKey(swap.ChainId)} to this network's node for chain {swap.ChainId} to follow Satora stablecoin swap {swap.SwapId}."));
}
