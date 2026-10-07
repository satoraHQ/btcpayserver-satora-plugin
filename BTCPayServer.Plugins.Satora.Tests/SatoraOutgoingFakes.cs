using System.Text.Json;
using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using BTCPayServer.Plugins.Satora.Services;
using LNURL;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Satora.Sdk;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;
using SdkArkadeSpend = uniffi.satora_sdk_ffi.ArkadeSpend;
using SdkLightningDestination = uniffi.satora_sdk_ffi.LightningDestination;
using SdkSwapDirection = uniffi.satora_sdk_ffi.SwapDirection;
using SdkSwapFunding = uniffi.satora_sdk_ffi.SwapFunding;
using SdkSwapStatus = uniffi.satora_sdk_ffi.SwapStatus;
using SdkTokenId = uniffi.satora_sdk_ffi.TokenId;

namespace BTCPayServer.Plugins.Satora.Tests;

// What the payment, worker and withdrawal tests run on instead of the plugin database and the SDK.
// Every call is written to one event list, marked when it ran with the store's spend lock held, or
// inside the registry's UseAsync without it.
public sealed class OutgoingEvents
{
    public List<string> All { get; } = [];

    public int GateDepth { get; set; }

    public int UseDepth { get; set; }

    public void Add(string name) => All.Add(GateDepth > 0 ? $"{name} (gated)" : UseDepth > 0 ? $"{name} (in use)" : name);
}

public sealed class MemoryOutgoingSwaps(OutgoingEvents events) : SatoraOutgoingSwaps(null!)
{
    public Dictionary<string, SatoraLightningSwap> Rows { get; } = [];

    public Exception? FundingAttemptedError { get; set; }

    public Exception? ClearFundingAttemptedError { get; set; }

    // Thrown by the closes in SatoraWithdrawals, as a transaction that failed.
    public Exception? CompletionError { get; set; }

    // Runs once ListOpenAsync has copied the rows, as a payment writing between the worker's read and its write would.
    public Action? AfterListOpen { get; set; }

    // Runs before SetFundingAttemptedAsync looks at the row, as a close committing just before it would.
    public Action? BeforeFundingAttempted { get; set; }

    public override Task AddAsync(SatoraLightningSwap swap)
    {
        events.Add($"add {swap.SwapId}");
        Rows[swap.SwapId] = Copy(swap);
        return Task.CompletedTask;
    }

    public override Task<SatoraLightningSwap?> FindByPaymentHashAsync(string storeId, string paymentHash) =>
        Task.FromResult(Rows.Values.Where(s => s.StoreId == storeId && s.PaymentHash == paymentHash).Select(Copy).FirstOrDefault());

    public override Task<List<SatoraLightningSwap>> ListAsync(string storeId, bool includePending, int offset, int limit) =>
        Task.FromResult(Rows.Values.Where(s => s.StoreId == storeId && (includePending || s.CompletedAt != null))
            .OrderByDescending(s => s.CreatedAt).Skip(offset).Take(limit).Select(Copy).ToList());

    public override Task<List<string>> ListOpenStoresAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Rows.Values.Where(s => s.CompletedAt == null).Select(s => s.StoreId).Distinct().ToList());

    public override Task<List<SatoraLightningSwap>> ListOpenAsync(string storeId, CancellationToken cancellationToken)
    {
        var open = Rows.Values.Where(s => s.StoreId == storeId && s.CompletedAt == null).Select(Copy).ToList();
        AfterListOpen?.Invoke();
        return Task.FromResult(open);
    }

    public override Task<bool> SetFundingAttemptedAsync(string swapId, bool again = false)
    {
        events.Add($"attempted {swapId}");
        if (FundingAttemptedError is not null)
            throw FundingAttemptedError;
        BeforeFundingAttempted?.Invoke();
        if (Rows[swapId].CompletedAt is not null || again && !Rows[swapId].FundingAttempted)
            return Task.FromResult(false);
        Rows[swapId].FundingAttempted = true;
        return Task.FromResult(true);
    }

    public override Task<bool> ClearFundingAttemptedAsync(string swapId)
    {
        events.Add($"undo {swapId}");
        if (ClearFundingAttemptedError is not null)
            throw ClearFundingAttemptedError;
        if (Rows[swapId] is not { CompletedAt: null, FundingTxId: null } row)
            return Task.FromResult(false);
        row.FundingAttempted = false;
        return Task.FromResult(true);
    }

    public override Task SetFundedAsync(string swapId, string fundingTxId) => Set(swapId, s => s.FundingTxId = fundingTxId);

    public override Task SetStatusAsync(string swapId, SatoraSwapStatus status) => Set(swapId, s => s.Status = status);

    public override Task SetRefundTxIdAsync(string swapId, string refundTxId) => Set(swapId, s => s.RefundTxId = refundTxId);

    // The swap's side of a close in SatoraWithdrawals: applies only to a row it matches, and throws
    // CompletionError, as a transaction that failed, before changing anything.
    public bool Close(string swapId, Func<SatoraLightningSwap, bool> matches, Action<SatoraLightningSwap> close)
    {
        if (CompletionError is not null)
            throw CompletionError;
        if (!Rows.TryGetValue(swapId, out var row) || !matches(row))
            return false;
        close(row);
        return true;
    }

    private Task Set(string swapId, Action<SatoraLightningSwap> update)
    {
        update(Rows[swapId]);
        return Task.CompletedTask;
    }

    // Copies, so only what the code under test writes through these methods reaches the rows.
    public static SatoraLightningSwap Copy(SatoraLightningSwap swap) =>
        JsonSerializer.Deserialize<SatoraLightningSwap>(JsonSerializer.Serialize(swap))!;
}

public sealed class FakeWalletAccess(FakeWallet wallet, OutgoingEvents events) : SatoraWalletAccess(null!)
{
    // The store's spend lock; cancellationToken stops only the wait for it, as in the registry.
    private readonly SemaphoreSlim _spendLock = new(1, 1);

    // What the registry throws when it cannot open the store's wallet.
    public Exception? OpenError { get; set; }

    public override Task<ISatoraWallet> GetAsync(string storeId) =>
        OpenError is null ? Task.FromResult<ISatoraWallet>(wallet) : throw OpenError;

    public override async Task<T> SpendAsync<T>(string storeId, Func<ISatoraWallet, Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (OpenError is not null)
            throw OpenError;
        await _spendLock.WaitAsync(cancellationToken);
        events.GateDepth++;
        try
        {
            return await action(wallet);
        }
        finally
        {
            events.GateDepth--;
            _spendLock.Release();
        }
    }

    public override async Task<T> UseAsync<T>(string storeId, Func<ISatoraWallet, Task<T>> action, CancellationToken cancellationToken = default)
    {
        if (OpenError is not null)
            throw OpenError;
        events.UseDepth++;
        try
        {
            return await action(wallet);
        }
        finally
        {
            events.UseDepth--;
        }
    }
}

public sealed class FakeWallet(OutgoingEvents events) : ISatoraWallet
{
    public ArkadeBalance Balance { get; set; } = new(100_000, 0, 0);
    public Func<SdkLightningDestination, SwapDetails>? Create { get; set; }
    public Exception? CreateError { get; set; }
    public Exception? FundError { get; set; }
    // Awaited by FundArkadeSwapAsync before it returns, once it has completed FundStarted.
    public Task FundGate { get; set; } = Task.CompletedTask;
    public TaskCompletionSource FundStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Dictionary<string, SdkSwapStatus> Statuses { get; } = [];
    public List<string> NotFound { get; } = [];
    public Exception? StatusError { get; set; }
    public Dictionary<SdkArkadeSpend, Exception> RefundErrors { get; } = [];
    public Action? DuringRefund { get; set; }
    // What every swap's VHTLC holds.
    public VhtlcAmounts Amounts { get; set; } = new(0, 0, 0, []);
    public Exception? AmountsError { get; set; }
    // Awaited by AmountsForSwapAsync before it answers.
    public Func<Task> AmountsGate { get; set; } = () => Task.CompletedTask;
    public Exception? SendError { get; set; }
    // Awaited by SendArkadeAsync before it returns, once it has completed SendStarted.
    public Task SendGate { get; set; } = Task.CompletedTask;
    public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Exception? SettleError { get; set; }
    // What RenewArkadeAsync returns, unless RenewError is set.
    public string? RenewedBatch { get; set; }
    public Exception? RenewError { get; set; }
    public LightningSendQuote? LightningQuote { get; set; }
    // The quote for a destination, instead of LightningQuote for every one.
    public Func<SdkLightningDestination, LightningSendQuote>? QuoteFor { get; set; }
    // What the amount-only quote says any amount pays: 12,345 sats, as the test invoice.
    public Quote SendMaxQuote { get; set; } = new("1.0", 10, 0, 45, 0.003, 1_000, 1_000_000, "100000", "100000", "100000", "12345", null);
    // Thrown by both Lightning quotes.
    public Exception? QuoteError { get; set; }
    // What an exit of everything sends; an exit of an amount sends that amount, at the same fee.
    public OffboardQuote OffboardQuote { get; set; } = new(50_000, 200);
    public Exception? OffboardQuoteError { get; set; }
    // Runs as the balance is read.
    public Action? DuringBalanceRead { get; set; }

    public Task<ArkadeBalance> GetBalanceAsync()
    {
        DuringBalanceRead?.Invoke();
        return Task.FromResult(Balance);
    }

    public Task<string?> SettleArkadeAsync()
    {
        events.Add("settle");
        if (SettleError is not null)
            throw SettleError;
        if (Balance.recoverableSats == 0)
            return Task.FromResult<string?>(null);
        Balance = new ArkadeBalance(Balance.confirmedSats + Balance.recoverableSats, Balance.preConfirmedSats, 0);
        return Task.FromResult<string?>("commitment");
    }

    public Task<string?> RenewArkadeAsync()
    {
        events.Add("renew");
        return RenewError is null ? Task.FromResult(RenewedBatch) : throw RenewError;
    }

    public async Task<string> SendArkadeAsync(string destination, ulong amountSats)
    {
        events.Add($"send {destination} {amountSats}");
        SendStarted.TrySetResult();
        await SendGate;
        return SendError is null ? "ark-send" : throw SendError;
    }

    public Task<OffboardQuote> GetOffboardQuoteAsync(string address, ulong? amountSats)
    {
        events.Add($"offboard quote {address} {amountSats?.ToString() ?? "all"}");
        if (OffboardQuoteError is not null)
            throw OffboardQuoteError;
        return Task.FromResult(amountSats is { } sats ? new OffboardQuote(sats, OffboardQuote.FeeSats) : OffboardQuote);
    }

    public Task<string> OffboardAsync(string address, ulong? amountSats)
    {
        events.Add($"offboard {address} {amountSats?.ToString() ?? "all"}");
        return SendError is null ? Task.FromResult("commitment-offboard") : throw SendError;
    }

    public Task<LightningSendQuote> GetLightningSendQuoteAsync(SdkLightningDestination destination)
    {
        events.Add($"quote {destination}");
        return QuoteError is null ? Task.FromResult(QuoteFor?.Invoke(destination) ?? LightningQuote!) : throw QuoteError;
    }

    public Task<Quote> GetLightningSendMaxQuoteAsync(ulong sourceSats)
    {
        events.Add($"send-max quote {sourceSats}");
        return QuoteError is null ? Task.FromResult(SendMaxQuote) : throw QuoteError;
    }

    public Task<SwapDetails> CreateArkadeToLightningSwapAsync(SdkLightningDestination destination)
    {
        events.Add($"create {destination}");
        if (CreateError is not null)
            throw CreateError;
        return Task.FromResult(Create!(destination));
    }

    public async Task<string> FundArkadeSwapAsync(string swapId)
    {
        events.Add($"fund {swapId}");
        FundStarted.TrySetResult();
        await FundGate;
        return FundError is null ? "ark-" + swapId : throw FundError;
    }

    public Task<BulkStatus> GetBulkStatusAsync(IReadOnlyList<string> swapIds)
    {
        if (StatusError is not null)
            throw StatusError;
        return Task.FromResult(new BulkStatus(
            swapIds.Where(Statuses.ContainsKey).Select(id => new SwapStatusEntry(id, Statuses[id])).ToArray(),
            swapIds.Where(NotFound.Contains).ToArray()));
    }

    public Task<string> RefundAsync(string swapId, SdkArkadeSpend operation)
    {
        events.Add($"refund {swapId} {operation}");
        DuringRefund?.Invoke();
        return RefundErrors.TryGetValue(operation, out var error) ? throw error : Task.FromResult($"refund-{swapId}");
    }

    public async Task<VhtlcAmounts> AmountsForSwapAsync(string swapId)
    {
        await AmountsGate();
        return AmountsError is null ? Amounts : throw AmountsError;
    }

    // An Arkade to Lightning swap paying the mainnet test invoice, as the swap server answers.
    public static SwapDetails ArkadeToLightning(string swapId, long fundingSats = 12_400, string bolt11 = SatoraLightningCreditTests.Bolt11) => new(
        swapId, new SdkSwapStatus.Pending(), new SdkSwapDirection.ArkadeToLightning(), new SdkSwapFunding.ArkadeAddress("ark1vhtlc"),
        fundingSats.ToString(), new SdkTokenId.Btc(), bolt11, "12345", new SdkTokenId.Btc(), "2026-04-09T05:41:00Z",
        Locktime.ToUnixTimeSeconds(), null, null, null);

    public static readonly DateTimeOffset Locktime = new(2026, 4, 30, 0, 0, 0, TimeSpan.Zero);
}

public sealed class MemoryWithdrawals(OutgoingEvents events, MemoryOutgoingSwaps? swaps = null) : SatoraWithdrawals(null!)
{
    public Dictionary<string, SatoraWithdrawal> Rows { get; } = [];

    public override Task<bool> TryAddAsync(SatoraWithdrawal withdrawal)
    {
        events.Add($"withdrawal {withdrawal.Id}");
        return Task.FromResult(Rows.TryAdd(withdrawal.Id, Copy(withdrawal)));
    }

    public override Task<SatoraWithdrawal?> FindAsync(string storeId, string id) =>
        Task.FromResult(Rows.TryGetValue(id, out var w) && w.StoreId == storeId ? Copy(w) : null);

    public override Task<List<SatoraWithdrawal>> ListAsync(string storeId, int limit) =>
        Task.FromResult(Rows.Values.Where(w => w.StoreId == storeId).Select(Copy).ToList());

    public override Task<List<SatoraWithdrawal>> ListUnresolvedAsync(string storeId) =>
        Task.FromResult(Rows.Values.Where(w => w.StoreId == storeId && IsUnresolved(w)).Select(Copy).ToList());

    public override Task<bool> HasOpenAsync(string storeId) =>
        Task.FromResult(Rows.Values.Any(w => w.StoreId == storeId && w.Status is SatoraWithdrawalStatus.Pending or SatoraWithdrawalStatus.Unknown));

    public override async Task AttachSwapAsync(string withdrawalId, SatoraLightningSwap swap)
    {
        if (swaps is not null)
            await swaps.AddAsync(swap);
        Rows[withdrawalId].SwapId = swap.SwapId;
    }

    public override Task<bool> MoveSwapAsync(string withdrawalId, string swapId)
    {
        events.Add($"move {swapId} to {withdrawalId}");
        if (swaps!.Rows.GetValueOrDefault(swapId) is not { FundingAttempted: false, CompletedAt: null } ||
            Rows[withdrawalId] is not { Status: SatoraWithdrawalStatus.Pending, SwapId: null } withdrawal ||
            Rows.Values.Any(w => w.SwapId == swapId && w.Status != SatoraWithdrawalStatus.Failed))
            return Task.FromResult(false);
        foreach (var failed in Rows.Values.Where(w => w.SwapId == swapId))
            failed.SwapId = null;
        withdrawal.SwapId = swapId;
        return Task.FromResult(true);
    }

    public override Task SetAmountsAsync(string id, long? amountSats, long? feeSats)
    {
        Rows[id].AmountSats = amountSats;
        Rows[id].FeeSats = feeSats;
        return Task.CompletedTask;
    }

    public override Task SetOutcomeAsync(string id, SatoraWithdrawalStatus status, string? txId, string? error)
    {
        if (Rows[id].Status == SatoraWithdrawalStatus.Pending)
            (Rows[id].Status, Rows[id].TxId, Rows[id].Error) = (status, txId ?? Rows[id].TxId, error);
        return Task.CompletedTask;
    }

    public override Task<bool> CloseUnattemptedSwapAsync(string swapId, SatoraSwapStatus status, string error, DateTimeOffset completedAt) =>
        Close(swapId, s => s is { FundingAttempted: false, CompletedAt: null }, s => (s.Status, s.CompletedAt) = (status, completedAt),
            SatoraWithdrawalStatus.Failed, null, error);

    public override Task<bool> CloseSwapAsync(string storeId, string swapId, bool paid, DateTimeOffset completedAt)
    {
        events.Add($"close {swapId}");
        return Close(swapId, s => s.StoreId == storeId && s.CompletedAt is null, s => (s.PaidAt, s.CompletedAt) = (paid ? completedAt : null, completedAt),
            paid ? SatoraWithdrawalStatus.Sent : SatoraWithdrawalStatus.Failed, null, paid ? null : "marked failed");
    }

    public override Task<bool> ClosePaidSwapAsync(string swapId, string? fundingTxId, DateTimeOffset paidAt) =>
        Close(swapId, s => s.CompletedAt is null, s => s.PaidAt = s.CompletedAt = paidAt, SatoraWithdrawalStatus.Sent, fundingTxId, null);

    public override Task<bool> CloseRefundedSwapAsync(string swapId, string refundTxId, string error, DateTimeOffset completedAt) =>
        Close(swapId, s => s.CompletedAt is null, s => (s.RefundTxId, s.CompletedAt) = (refundTxId, completedAt), SatoraWithdrawalStatus.Failed, null, error);

    public override Task<bool> CloseUnfundedSwapAsync(string swapId, string error, DateTimeOffset completedAt) =>
        Close(swapId, s => s.CompletedAt is null, s => s.CompletedAt = completedAt, SatoraWithdrawalStatus.Failed, null, error);

    // The withdrawal takes the outcome only with the swap's close, and keeps a status already decided.
    private Task<bool> Close(string swapId, Func<SatoraLightningSwap, bool> matches, Action<SatoraLightningSwap> close,
        SatoraWithdrawalStatus status, string? txId, string? error)
    {
        if (!swaps!.Close(swapId, matches, close))
            return Task.FromResult(false);
        foreach (var w in Rows.Values.Where(w => w.SwapId == swapId && w.Status == SatoraWithdrawalStatus.Pending))
            (w.Status, w.TxId, w.Error) = (status, txId ?? w.TxId, error);
        return Task.FromResult(true);
    }

    public override Task<SatoraWithdrawal?> FindBySwapIdAsync(string storeId, string swapId) =>
        Task.FromResult(Rows.Values.Where(w => w.StoreId == storeId && w.SwapId == swapId).Select(Copy).FirstOrDefault());

    public override Task<bool> ResolveAsync(string storeId, string id, bool sent, bool running, DateTimeOffset now)
    {
        if (!Rows.TryGetValue(id, out var w) || w.StoreId != storeId || !OwnerCanResolve(w, running, now))
            return Task.FromResult(false);
        w.Status = sent ? SatoraWithdrawalStatus.Sent : SatoraWithdrawalStatus.Failed;
        return Task.FromResult(true);
    }

    public static SatoraWithdrawal Copy(SatoraWithdrawal withdrawal) =>
        JsonSerializer.Deserialize<SatoraWithdrawal>(JsonSerializer.Serialize(withdrawal))!;
}

public sealed class MemoryAutoWithdrawRules() : SatoraAutoWithdrawRules(null!)
{
    public Dictionary<string, SatoraAutoWithdrawRule> Rows { get; } = [];

    public override Task<SatoraAutoWithdrawRule?> GetAsync(string storeId) =>
        Task.FromResult(Rows.TryGetValue(storeId, out var rule) ? Copy(rule) : null);

    public override Task<(bool Saved, SatoraAutoWithdrawRule? Previous)> SaveAsync(SatoraAutoWithdrawRule rule, int? expectedVersion, DateTimeOffset now)
    {
        var previous = Rows.GetValueOrDefault(rule.StoreId);
        if (previous?.Version != expectedVersion)
            return Task.FromResult<(bool, SatoraAutoWithdrawRule?)>((false, null));
        var saved = Copy(rule);
        (saved.Enabled, saved.Version, saved.UpdatedAt) = (true, (expectedVersion ?? 0) + 1, now);
        Rows[rule.StoreId] = saved;
        return Task.FromResult<(bool, SatoraAutoWithdrawRule?)>((true, previous));
    }

    public override Task<bool> DisableAsync(string storeId, DateTimeOffset now)
    {
        if (!Rows.TryGetValue(storeId, out var rule) || !rule.Enabled)
            return Task.FromResult(false);
        (rule.Enabled, rule.Version, rule.UpdatedAt) = (false, rule.Version + 1, now);
        return Task.FromResult(true);
    }

    // An enabled rule as the withdrawals page saves it.
    public SatoraAutoWithdrawRule Set(string storeId, SatoraWithdrawalType type, string destination, long thresholdSats, long reserveSats, int maxFeeBps = 100) =>
        Rows[storeId] = new SatoraAutoWithdrawRule
        {
            StoreId = storeId,
            Enabled = true,
            DestinationType = type,
            Destination = destination,
            ThresholdSats = thresholdSats,
            ReserveSats = reserveSats,
            MaxFeeBps = maxFeeBps,
            Version = 1
        };

    public static SatoraAutoWithdrawRule Copy(SatoraAutoWithdrawRule rule) =>
        JsonSerializer.Deserialize<SatoraAutoWithdrawRule>(JsonSerializer.Serialize(rule))!;
}

// The withdrawal service on mainnet over the fakes above, with every Lightning address and LNURL
// answered here and Unknown notifications recorded.
public sealed class TestWithdrawalService(OutgoingEvents events, SatoraWalletAccess wallets, SatoraWithdrawals withdrawals,
    SatoraAutoWithdrawRules rules, SatoraLightningPayments payments, SatoraWalletRenewals renewals, SatoraOutgoingSwaps outgoing,
    ILogger<SatoraWithdrawalService>? logger = null)
    : SatoraWithdrawalService(wallets, withdrawals, rules, payments, renewals, outgoing, null!, null!, null!, null!, null!, logger ?? NullLogger<SatoraWithdrawalService>.Instance)
{
    public List<string> Notified { get; } = [];

    public LNURLPayRequest PayRequest { get; } = new()
    {
        Callback = new Uri("https://example.com/lnurlp/alice/callback"),
        MinSendable = LightMoney.Satoshis(1),
        MaxSendable = LightMoney.Satoshis(1_000_000)
    };

    public string Invoice { get; set; } = SatoraLightningCreditTests.Bolt11;

    // Answers each invoice request with an invoice for the amount asked, instead of Invoice.
    public Func<long, string>? InvoiceFor { get; set; }

    // Awaited by every Lightning address and LNURL lookup before it answers.
    public Task PayRequestGate { get; set; } = Task.CompletedTask;

    // Makes every pay request answer only once the lookup it is part of has timed out, as an endpoint
    // whose answer arrives just as the timeout runs out.
    public bool PayRequestAnswersAtTimeout { get; set; }

    // Thrown by every Lightning address and LNURL lookup, as by an endpoint that cannot be reached.
    public Exception? PayeeError { get; set; }

    protected override TimeSpan LnurlTimeout => TimeSpan.FromMilliseconds(500);

    protected override TimeSpan HeldReadTimeout => TimeSpan.FromMilliseconds(500);

    public override string? Unavailable => null;

    protected override Network Network => Network.Main;

    protected override TimeSpan CloseWait => TimeSpan.FromMilliseconds(200);

    public override Task<bool> HasAutomatedLightningPayoutsAsync(string storeId) => Task.FromResult(false);

    protected override Task NotifyUnknownAsync(SatoraWithdrawal withdrawal)
    {
        Notified.Add(withdrawal.Id);
        return Task.CompletedTask;
    }

    protected override async Task<LNURLPayRequest> FetchPayRequestAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        events.Add($"lnurl {endpoint}");
        await PayRequestGate.WaitAsync(cancellationToken);
        if (PayRequestAnswersAtTimeout)
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return PayeeError is null ? PayRequest : throw PayeeError;
    }

    protected override Task<string> RequestInvoiceAsync(LNURLPayRequest payRequest, LightMoney amount, Network network, CancellationToken cancellationToken)
    {
        events.Add($"lnurl invoice {amount.MilliSatoshi / 1000}");
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(InvoiceFor?.Invoke(amount.MilliSatoshi / 1000) ?? Invoice);
    }
}
