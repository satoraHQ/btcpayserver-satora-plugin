using System.Text.Json;
using System.Text.Json.Serialization;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Services;
using Satora.Sdk;
using SdkArkadeSettlement = uniffi.satora_sdk_ffi.ArkadeSettlement;

namespace BTCPayServer.Plugins.Satora.Chain;

// The SDK calls and chain reads SatoraChainWorker makes. Status reads and claims go through the same
// registry access as Lightning claims. Virtual so the worker's tests run without a swap server, arkd
// or esplora.
public class SatoraChainWallets(SatoraLightningWallets wallets, SatoraWalletRegistry registry, IHttpClientFactory httpClientFactory)
{
    // Most a refund may pay in fees, whatever the estimate: about 50 sat/vB for one HTLC input. The
    // SDK leaves an output the cap cannot cover for a later call.
    public const ulong MaxRefundFeeSats = 10_000;
    // A refund spends one HTLC output through its refund leaf to one output: about 138 vB to a
    // Taproot address, less to any other.
    public const long RefundVbytes = 140;
    private const double MinRelaySatVb = 1;
    private static readonly TimeSpan EsploraTimeout = TimeSpan.FromSeconds(20);

    public virtual Task<BulkStatus> GetBulkStatusAsync(string storeId, IEnumerable<string> swapIds, CancellationToken cancellationToken) =>
        wallets.GetBulkStatusAsync(storeId, swapIds, cancellationToken);

    public virtual Task<string> GetClaimAddressAsync(string storeId, CancellationToken cancellationToken) =>
        wallets.GetClaimAddressAsync(storeId, cancellationToken);

    public virtual Task<SdkArkadeSettlement> ClaimAsync(string storeId, string swapId, string destination, CancellationToken cancellationToken) =>
        wallets.ClaimAsync(storeId, swapId, destination, cancellationToken);

    public virtual Task<bool> ClaimPossibleAsync(string storeId, string swapId) =>
        wallets.ClaimPossibleAsync(storeId, swapId);

    // A null fee rate takes the SDK's own rate, which EstimateRefundFeeSatsAsync mirrors. Called again
    // after any error, it returns the refund it already broadcast instead of building another.
    public virtual Task<BitcoinRefundReceipt> RefundAsync(string storeId, string swapId, string destination, CancellationToken cancellationToken) =>
        registry.UseAsync(storeId, wallet => wallet.Client.RefundBitcoinAsync(swapId, destination, feeRateSatVb: null, MaxRefundFeeSats), cancellationToken);

    // Unspent outputs at the address from the esplora the SDK reads: those paid in the mempool are in,
    // those spent in the mempool are out.
    public virtual async Task<IReadOnlyList<SatoraHtlcOutput>> ListUnspentAsync(string address, CancellationToken cancellationToken) =>
        ParseUtxos(await GetAsync<List<EsploraUtxo>>($"address/{Uri.EscapeDataString(address)}/utxo", cancellationToken));

    // Whether a transaction spending one of the address's outputs may be a refund the node took though
    // its answer was lost: one still in the mempool, or one paying the refund address, confirmed or not.
    // Read from the address's history, because the mempool backend over an Electrum server, as the
    // regtest stack runs it, does not serve txs/mempool.
    public virtual async Task<bool> HasPossibleRefundAsync(string address, string? refundAddress, CancellationToken cancellationToken) =>
        PossibleRefund(await GetAsync<List<EsploraTx>>($"address/{Uri.EscapeDataString(address)}/txs", cancellationToken), address, refundAddress);

    // A transaction esplora does not know, such as one dropped from the mempool, is not confirmed.
    public virtual async Task<bool> IsConfirmedAsync(string txId, CancellationToken cancellationToken) =>
        (await GetAsync<EsploraStatus>($"tx/{Uri.EscapeDataString(txId)}/status", cancellationToken, notFound: true))?.Confirmed is true;

    // What a refund would pay at the rate the SDK takes when given none (rust-sdk bitcoin/esplora.rs
    // fee_rate): mempool's recommended fees, or the estimates of an esplora without mempool's API.
    // mempool serves no fee-estimates over an Electrum backend, as the regtest stack runs it.
    public virtual async Task<long> EstimateRefundFeeSatsAsync(CancellationToken cancellationToken)
    {
        var recommended = await ReadAsync<MempoolRecommendedFees>("v1/fees/recommended", cancellationToken, notFound: true);
        return RefundFee(recommended.Found
            ? Estimates(recommended.Body)
            : Estimates(await GetAsync<Dictionary<string, double>>("fee-estimates", cancellationToken)));
    }

    // The block targets the swap server reads each recommended fee for.
    internal static IReadOnlyList<(int Blocks, double Rate)> Estimates(MempoolRecommendedFees? fees) =>
        fees is null
            ? throw new FormatException("Esplora returned no recommended fees.")
            : [(1, fees.FastestFee), (3, fees.HalfHourFee), (6, fees.HourFee), (25, fees.EconomyFee), (144, fees.MinimumFee)];

    internal static IReadOnlyList<(int Blocks, double Rate)> Estimates(Dictionary<string, double>? estimates) =>
        (estimates ?? throw new FormatException("Esplora returned no fee estimates."))
        .Select(e => (int.Parse(e.Key, System.Globalization.CultureInfo.InvariantCulture), e.Value))
        .ToList();

    // As the SDK picks it: the rate for the most blocks up to six, else for the fewest, never below
    // the 1 sat/vB most nodes need to relay a transaction.
    internal static long RefundFee(IReadOnlyList<(int Blocks, double Rate)> estimates)
    {
        var rate = estimates.Where(e => e.Blocks <= 6).OrderByDescending(e => e.Blocks).Select(e => (double?)e.Rate).FirstOrDefault()
                   ?? estimates.OrderBy(e => e.Blocks).Select(e => (double?)e.Rate).FirstOrDefault()
                   ?? throw new FormatException("Esplora returned no fee estimates.");
        if (!double.IsFinite(rate) || rate <= 0 || rate > 1_000_000)
            throw new FormatException($"Esplora returned an invalid fee rate, {rate} sat/vB.");
        return (long)Math.Ceiling(Math.Max(rate, MinRelaySatVb) * RefundVbytes);
    }

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken, bool notFound = false) =>
        (await ReadAsync<T>(path, cancellationToken, notFound)).Body;

    // Found is false only for a 404 with notFound set; a body of null is found.
    private async Task<(bool Found, T? Body)> ReadAsync<T>(string path, CancellationToken cancellationToken, bool notFound)
    {
        var esplora = registry.EsploraUrl ?? throw new InvalidOperationException(registry.NetworkError ?? "No esplora for this network.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(EsploraTimeout);
        using var http = httpClientFactory.CreateClient(nameof(SatoraChainWallets));
        using var response = await http.GetAsync($"{esplora.TrimEnd('/')}/{path}", timeout.Token);
        if (notFound && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return (false, default);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
        return (true, await JsonSerializer.DeserializeAsync<T>(body, cancellationToken: timeout.Token));
    }

    internal static IReadOnlyList<SatoraHtlcOutput> ParseUtxos(List<EsploraUtxo>? utxos) =>
        (utxos ?? throw new FormatException("Esplora returned no outputs list."))
        .Select(u => new SatoraHtlcOutput(u.TxId, u.Vout, u.Value, u.Status?.Confirmed is true))
        .ToList();

    internal static bool PossibleRefund(List<EsploraTx>? txs, string address, string? refundAddress) =>
        (txs ?? throw new FormatException("Esplora returned no transactions list."))
        .Any(tx => tx.Vin?.Any(input => input.Prevout?.Address == address) is true &&
                   (tx.Status?.Confirmed is not true ||
                    refundAddress is not null && tx.Vout?.Any(output => output.Address == refundAddress) is true));

    // Required, as the SDK's serde decode needs every field: a fee missing from the body is an error,
    // never a rate of 0.
    internal sealed record MempoolRecommendedFees(
        [property: JsonPropertyName("fastestFee"), JsonRequired] double FastestFee,
        [property: JsonPropertyName("halfHourFee"), JsonRequired] double HalfHourFee,
        [property: JsonPropertyName("hourFee"), JsonRequired] double HourFee,
        [property: JsonPropertyName("economyFee"), JsonRequired] double EconomyFee,
        [property: JsonPropertyName("minimumFee"), JsonRequired] double MinimumFee);

    internal sealed record EsploraUtxo(
        [property: JsonPropertyName("txid")] string TxId,
        [property: JsonPropertyName("vout")] uint Vout,
        [property: JsonPropertyName("value")] long Value,
        [property: JsonPropertyName("status")] EsploraStatus? Status);

    internal sealed record EsploraStatus([property: JsonPropertyName("confirmed")] bool Confirmed);

    internal sealed record EsploraTx(
        [property: JsonPropertyName("vin")] List<EsploraInput>? Vin,
        [property: JsonPropertyName("vout")] List<EsploraPrevout>? Vout,
        [property: JsonPropertyName("status")] EsploraStatus? Status);

    internal sealed record EsploraInput([property: JsonPropertyName("prevout")] EsploraPrevout? Prevout);

    internal sealed record EsploraPrevout([property: JsonPropertyName("scriptpubkey_address")] string? Address);
}
