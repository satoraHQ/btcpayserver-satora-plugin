using BTCPayServer.Lightning;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;
using Satora.Sdk;
using Xunit;
using ArkadeBalance = uniffi.satora_sdk_ffi.ArkadeBalance;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraLightningPaymentsTests
{
    private const string Store = "store";
    private const string Bolt11 = SatoraLightningCreditTests.Bolt11;
    private const string PaymentHash = SatoraLightningCreditTests.PaymentHash;
    // Within the test invoice's validity.
    private static readonly DateTimeOffset Now = BOLT11PaymentRequest.Parse(Bolt11, Network.Main).Timestamp.AddHours(1);

    private readonly OutgoingEvents _events = new();
    private readonly MemoryOutgoingSwaps _swaps;
    private readonly FakeWallet _wallet;
    private readonly SatoraLightningPayments _payments;
    private int _keyChecks;

    public SatoraLightningPaymentsTests()
    {
        _swaps = new MemoryOutgoingSwaps(_events);
        _wallet = new FakeWallet(_events) { Create = _ => FakeWallet.ArkadeToLightning("swap") };
        _payments = new SatoraLightningPayments(_swaps, new FakeWalletAccess(_wallet, _events), NullLogger<SatoraLightningPayments>.Instance)
        {
            Clock = () => Now
        };
    }

    private Task<PayResponse> Pay(PayInvoiceParams? payParams = null) =>
        _payments.PayAsync(Store, () =>
        {
            _keyChecks++;
            return Task.CompletedTask;
        }, Bolt11, payParams, Network.Main, CancellationToken.None);

    [Fact]
    public async Task CreatesRecordsMarksAndFundsInsideTheGateThenReportsPending()
    {
        var response = await Pay();

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.Equal(LightningPaymentStatus.Pending, response.Details.Status);
        Assert.Equal(["create (gated)", "add swap (gated)", "attempted swap (gated)", "fund swap (gated)"],
            _events.All.Select(e => e.StartsWith("create", StringComparison.Ordinal) ? e.EndsWith("(gated)", StringComparison.Ordinal) ? "create (gated)" : "create" : e));
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: true, FundingTxId: "ark-swap", FundingSats: 12_400, AmountMsat: 12_345_000, PaymentHash: PaymentHash });
        // Checked before the lookup, and again once it holds the gate.
        Assert.Equal(2, _keyChecks);
    }

    // A retried payout brings the same invoice back: what was attempted is only reported.
    [Theory]
    [InlineData(SatoraSwapStatus.Pending, PayResult.Unknown)]
    [InlineData(SatoraSwapStatus.ServerFunded, PayResult.Unknown)]
    [InlineData(SatoraSwapStatus.ServerRedeemed, PayResult.Ok)]
    [InlineData(SatoraSwapStatus.ServerWontFund, PayResult.Error)]
    [InlineData(SatoraSwapStatus.Expired, PayResult.Error)]
    public async Task ReportsAnAttemptedPaymentWithoutTouchingTheWallet(SatoraSwapStatus status, PayResult expected)
    {
        await _swaps.AddAsync(Row("swap", status, fundingAttempted: true));
        _events.All.Clear();

        var response = await Pay();

        Assert.Equal(expected, response.Result);
        Assert.Empty(_events.All);
        if (expected == PayResult.Ok)
            Assert.Equal((LightMoney.Satoshis(12_400), LightMoney.Satoshis(55)), (response.Details.TotalAmount, response.Details.FeeAmount));
    }

    // A Greenfield caller may retry the same invoice after it expired, or with another amount; the
    // payment it made may still go out, or have gone out already.
    [Theory]
    [InlineData(SatoraSwapStatus.ServerRedeemed, true, null, PayResult.Ok)]
    [InlineData(SatoraSwapStatus.Pending, true, null, PayResult.Unknown)]
    [InlineData(SatoraSwapStatus.ServerFunded, false, 12_000L, PayResult.Unknown)]
    public async Task ReportsAnAttemptedPaymentWhoseInvoiceItWouldNowRefuse(SatoraSwapStatus status, bool expired, long? amountSats, PayResult expected)
    {
        await _swaps.AddAsync(Row("swap", status, fundingAttempted: true));
        _events.All.Clear();
        if (expired)
            _payments.Clock = () => BOLT11PaymentRequest.Parse(Bolt11, Network.Main).ExpiryDate;

        var response = await Pay(amountSats is { } sats ? new PayInvoiceParams { Amount = LightMoney.Satoshis(sats) } : null);

        Assert.Equal(expected, response.Result);
        Assert.Empty(_events.All);
        Assert.Equal(0, _keyChecks);
    }

    [Fact]
    public async Task RefusesAnExpiredInvoiceWhoseSwapWasNeverAttempted()
    {
        await _swaps.AddAsync(Row("swap", SatoraSwapStatus.Pending, fundingAttempted: false));
        _events.All.Clear();
        _payments.Clock = () => BOLT11PaymentRequest.Parse(Bolt11, Network.Main).ExpiryDate;

        var response = await Pay();

        Assert.Equal((PayResult.Error, "The invoice has expired."), (response.Result, response.ErrorDetail));
        Assert.Empty(_events.All);
        Assert.False(_swaps.Rows["swap"].FundingAttempted);
    }

    [Fact]
    public async Task ReportsAnAttemptedPaymentOnceTheWalletIsDisabledOrItsKeyRotated()
    {
        await _swaps.AddAsync(Row("swap", SatoraSwapStatus.ServerFunded, fundingAttempted: true));

        var response = await _payments.PayAsync(Store, () => throw new InvalidOperationException("The store's Satora wallet is not enabled."),
            Bolt11, null, Network.Main, CancellationToken.None);

        Assert.Equal(PayResult.Unknown, response.Result);
    }

    [Fact]
    public async Task ResumesARecordedSwapWhoseFundingWasNeverAttempted()
    {
        await _swaps.AddAsync(Row("swap", SatoraSwapStatus.Pending, fundingAttempted: false));
        _events.All.Clear();

        var response = await Pay();

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.Equal(["attempted swap (gated)", "fund swap (gated)"], _events.All);
    }

    [Fact]
    public async Task RefusesARecordedSwapTheServerNoLongerAwaitsFundingFor()
    {
        await _swaps.AddAsync(Row("swap", SatoraSwapStatus.Expired, fundingAttempted: false));
        _events.All.Clear();

        Assert.Equal(PayResult.Error, (await Pay()).Result);
        Assert.Empty(_events.All);
    }

    // The worker closes an unattempted swap the server no longer knows, recording it expired. A row
    // closed some other way is refused all the same.
    [Fact]
    public async Task RefusesAClosedSwapForTheInvoiceBeforeTouchingTheWallet()
    {
        var closed = Row("swap", SatoraSwapStatus.Pending, fundingAttempted: false);
        closed.CompletedAt = Now;
        await _swaps.AddAsync(closed);
        _events.All.Clear();

        var response = await Pay();

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Contains("closed", response.ErrorDetail);
        Assert.Empty(_events.All);
    }

    // The worker closed the swap after the payment read it and before it marked the attempt.
    [Fact]
    public async Task ASwapClosedBeforeTheAttemptIsMarkedIsAnErrorAndIsNeverFunded()
    {
        await _swaps.AddAsync(Row("swap", SatoraSwapStatus.Pending, fundingAttempted: false));
        _swaps.BeforeFundingAttempted = () => (_swaps.Rows["swap"].Status, _swaps.Rows["swap"].CompletedAt) = (SatoraSwapStatus.Expired, Now);

        var response = await Pay();

        Assert.Equal(PayResult.Error, response.Result);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("fund", StringComparison.Ordinal));
        Assert.False(_swaps.Rows["swap"].FundingAttempted);
    }

    // Before the attempt is recorded nothing can have left the wallet, so BTCPay may retry the payout.
    [Fact]
    public async Task AFailedSwapCreationIsAnError()
    {
        _wallet.CreateError = new SdkException(SdkErrorKind.Transport, "connection reset");

        Assert.Equal(PayResult.Error, (await Pay()).Result);
        Assert.Empty(_swaps.Rows);
    }

    // BTCPay shows a payout's error to the store and the payee.
    [Fact]
    public async Task AnInvoiceBelowTheServersMinimumIsRefusedInTheStoresTerms()
    {
        _wallet.CreateError = new SdkException(SdkErrorKind.Api, "Min amount is ₿ 0.00 020 000", 400);

        var response = await Pay();

        Assert.Equal((PayResult.Error, "Satora pays Lightning amounts of at least 20000 sats (0.00020000 BTC)."), (response.Result, response.ErrorDetail));
        Assert.Empty(_swaps.Rows);
    }

    // Recoverable funds are not spent, and once VTXOs expire nothing pays until someone settles them,
    // which a total below dust does not allow.
    [Theory]
    [InlineData(50_000UL, " Another 50000 sats are recoverable. Settle them on Satora's withdrawals page, then try again.")]
    [InlineData(329UL, " Another 329 sats are recoverable but too small to settle yet.")]
    [InlineData(0UL, "")]
    public async Task ASwapTheWalletCannotFundIsAnErrorAndIsNeverMarkedAttempted(ulong recoverable, string hint)
    {
        _wallet.Balance = new ArkadeBalance(12_000, 399, recoverable);

        var response = await Pay();

        Assert.Equal((PayResult.Error, "The swap takes 12400 sats, but the wallet can spend 12399 sats." + hint), (response.Result, response.ErrorDetail));
        Assert.False(_swaps.Rows["swap"].FundingAttempted);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("fund", StringComparison.Ordinal));
    }

    // BTCPay's payout processor caps routing fees at 3%; 55 sats of fees on 12 345 sats is 0.45%.
    [Theory]
    [InlineData(3.0, null, PayResult.Unknown)]
    [InlineData(0.4, null, PayResult.Error)]
    [InlineData(0.4, 55L, PayResult.Unknown)]
    [InlineData(null, 54L, PayResult.Error)]
    [InlineData(null, null, PayResult.Unknown)]
    public async Task RefusesASwapWhoseFeesPassTheCallersCap(double? maxFeePercent, long? maxFeeFlat, PayResult expected)
    {
        var payParams = new PayInvoiceParams { MaxFeePercent = maxFeePercent, MaxFeeFlat = maxFeeFlat is { } flat ? Money.Satoshis(flat) : null };

        Assert.Equal(expected, (await Pay(payParams)).Result);
        Assert.Equal(expected != PayResult.Error, _swaps.Rows["swap"].FundingAttempted);
    }

    // Without a cap from the caller, a swap may keep what a Lightning receive may: 3% plus 100 sats,
    // 470 sats on this invoice.
    [Theory]
    [InlineData(12_815L, PayResult.Unknown)]
    [InlineData(12_816L, PayResult.Error)]
    public async Task CapsASwapsFeesWhenTheCallerSetsNoCap(long fundingSats, PayResult expected)
    {
        _wallet.Create = _ => FakeWallet.ArkadeToLightning("swap", fundingSats);

        Assert.Equal(expected, (await Pay()).Result);
        Assert.Equal(expected != PayResult.Error, _swaps.Rows["swap"].FundingAttempted);
        Assert.Equal(expected != PayResult.Error, _events.All.Contains("fund swap (gated)"));
    }

    [Fact]
    public async Task ARotatedKeyIsAnErrorBeforeAnythingIsCreated()
    {
        var response = await _payments.PayAsync(Store, () => throw new UnauthorizedAccessException("rotated"), Bolt11, null, Network.Main, CancellationToken.None);

        Assert.Equal(PayResult.Error, response.Result);
        Assert.Empty(_events.All);
    }

    // Once the attempt is recorded, any error at all may follow a payment that went out.
    [Theory]
    [InlineData(SdkErrorKind.Transport)]
    [InlineData(SdkErrorKind.Timeout)]
    [InlineData(SdkErrorKind.Storage)]
    [InlineData(SdkErrorKind.InvalidSwap)]
    [InlineData(SdkErrorKind.Api)]
    [InlineData(SdkErrorKind.Internal)]
    public async Task AnyFundingErrorIsUnknownAndLeftToTheWorker(SdkErrorKind kind)
    {
        _wallet.FundError = new SdkException(kind, "funding failed");

        var response = await Pay();

        Assert.Equal(PayResult.Unknown, response.Result);
        Assert.Equal(LightningPaymentStatus.Pending, response.Details.Status);
        Assert.True(_swaps.Rows["swap"] is { FundingAttempted: true, FundingTxId: null });
    }

    // The write may have committed though it threw; the worker would then fund the swap.
    [Fact]
    public async Task AFailedWriteOfTheAttemptIsUnknown()
    {
        _swaps.FundingAttemptedError = new InvalidOperationException("connection lost");

        Assert.Equal(PayResult.Unknown, (await Pay()).Result);
        Assert.DoesNotContain(_events.All, e => e.StartsWith("fund", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusesInvoicesItCannotPayBeforeTouchingAnything()
    {
        Assert.Equal(PayResult.Error, (await Pay(new PayInvoiceParams { Amount = LightMoney.Satoshis(12_000) })).Result);
        _payments.Clock = () => Now.AddDays(2);
        Assert.Equal(PayResult.Error, (await Pay()).Result);
        Assert.Equal(PayResult.Error, (await _payments.PayAsync(Store, () => Task.CompletedTask, null, null, Network.Main, CancellationToken.None)).Result);
        Assert.Equal(PayResult.Error, (await _payments.PayAsync(Store, () => Task.CompletedTask, Bolt11, null, Network.RegTest, CancellationToken.None)).Result);

        Assert.Empty(_events.All);
        Assert.Equal(0, _keyChecks);
    }

    [Fact]
    public void PaysOnlyInvoicesWithAnAmountThatHaveNotExpired()
    {
        var invoice = SatoraLightningPayments.ParseInvoice(Bolt11, Network.Main, out _)!;
        Assert.Null(SatoraLightningPayments.ParseInvoice(Bolt11, Network.RegTest, out var otherNetwork));
        Assert.NotNull(otherNetwork);

        Assert.Null(SatoraLightningPayments.Refusal(invoice, null, Now));
        Assert.Null(SatoraLightningPayments.Refusal(invoice, new PayInvoiceParams { Amount = LightMoney.Satoshis(12_345) }, Now));
        Assert.Contains("12345000 msat", SatoraLightningPayments.Refusal(invoice, new PayInvoiceParams { Amount = LightMoney.Satoshis(12_346) }, Now));
        Assert.Equal("The invoice has expired.", SatoraLightningPayments.Refusal(invoice, null, invoice.ExpiryDate));
    }

    [Theory]
    [InlineData(SatoraSwapStatus.ServerRedeemed, LightningPaymentStatus.Complete)]
    [InlineData(SatoraSwapStatus.ServerWontFund, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.Expired, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.ClientFundedServerRefunded, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.ClientInvalidFunded, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.ClientFundedTooLate, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.Pending, LightningPaymentStatus.Pending)]
    [InlineData(SatoraSwapStatus.ClientFunded, LightningPaymentStatus.Pending)]
    [InlineData(SatoraSwapStatus.ServerFunded, LightningPaymentStatus.Pending)]
    [InlineData(SatoraSwapStatus.ClientRefunded, LightningPaymentStatus.Pending)]
    [InlineData(SatoraSwapStatus.Unknown, LightningPaymentStatus.Pending)]
    public async Task GetPaymentNeverReturnsNullForAStoredSwap(SatoraSwapStatus status, LightningPaymentStatus expected)
    {
        await _swaps.AddAsync(Row("swap", status, fundingAttempted: true));

        var payment = await _payments.GetPaymentAsync(Store, PaymentHash);

        Assert.NotNull(payment);
        Assert.Equal(expected, payment.Status);
        Assert.Equal(("swap", PaymentHash, Bolt11), (payment.Id, payment.PaymentHash, payment.BOLT11));
        Assert.Equal((LightMoney.Satoshis(12_345), LightMoney.Satoshis(12_400), LightMoney.Satoshis(55)), (payment.Amount, payment.AmountSent, payment.Fee));
        Assert.Null(await _payments.GetPaymentAsync("other-store", PaymentHash));
        Assert.Null(await _payments.GetPaymentAsync(Store, new string('0', 64)));
    }

    // A payment refused before its funding, as for a wallet that could not cover it, moved nothing.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReportsNothingSentForAPaymentNeverFunded(bool closed)
    {
        var refused = Row("swap", SatoraSwapStatus.Pending, fundingAttempted: false);
        refused.CompletedAt = closed ? Now : null;
        await _swaps.AddAsync(refused);

        var payment = (await _payments.GetPaymentAsync(Store, PaymentHash))!;

        Assert.Equal((LightMoney.Satoshis(12_345), null, null), (payment.Amount, payment.AmountSent, payment.Fee));
    }

    // A refund takes the whole VHTLC back into the wallet, so a payment funded and then refunded, with
    // or without the swap server, moved nothing either.
    [Theory]
    [InlineData(SatoraSwapStatus.ServerWontFund, true, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.ServerFunded, false, LightningPaymentStatus.Pending)]
    public async Task ReportsNothingSentForAPaymentRefundedToTheWallet(SatoraSwapStatus status, bool closed, LightningPaymentStatus expected)
    {
        var refunded = Row("swap", status, fundingAttempted: true);
        refunded.FundingTxId = "ark-swap";
        refunded.RefundTxId = "ark-refund";
        refunded.CompletedAt = closed ? Now : null;
        await _swaps.AddAsync(refunded);

        var payment = (await _payments.GetPaymentAsync(Store, PaymentHash))!;

        Assert.Equal((expected, LightMoney.Satoshis(12_345), null, null), (payment.Status, payment.Amount, payment.AmountSent, payment.Fee));
    }

    // A closed swap is complete only once recorded paid, whatever its last status.
    [Theory]
    [InlineData(SatoraSwapStatus.ServerRedeemed, true, LightningPaymentStatus.Complete)]
    [InlineData(SatoraSwapStatus.ServerFunded, true, LightningPaymentStatus.Complete)]
    [InlineData(SatoraSwapStatus.ServerFunded, false, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.ClientRefunded, false, LightningPaymentStatus.Failed)]
    [InlineData(SatoraSwapStatus.Pending, false, LightningPaymentStatus.Failed)]
    public async Task AClosedSwapIsCompleteOnlyOnceRecordedPaid(SatoraSwapStatus status, bool paid, LightningPaymentStatus expected)
    {
        var closed = Row("swap", status, fundingAttempted: true);
        closed.CompletedAt = Now;
        closed.PaidAt = paid ? Now : null;
        await _swaps.AddAsync(closed);

        Assert.Equal(expected, (await _payments.GetPaymentAsync(Store, PaymentHash))!.Status);
    }

    [Fact]
    public async Task ListsCompletedPaymentsUnlessPendingOnesAreAskedFor()
    {
        var done = Row("done", SatoraSwapStatus.ServerRedeemed, fundingAttempted: true);
        done.CompletedAt = Now;
        await _swaps.AddAsync(done);
        await _swaps.AddAsync(Row("open", SatoraSwapStatus.ServerFunded, fundingAttempted: true));

        Assert.Equal(["done"], (await _payments.ListPaymentsAsync(Store, new ListPaymentsParams())).Select(p => p.Id));
        Assert.Equal(["done", "open"], (await _payments.ListPaymentsAsync(Store, new ListPaymentsParams { IncludePending = true })).Select(p => p.Id).Order());
    }

    [Fact]
    public void RecordsTheSwapWithItsFundingAmountAndRefundLocktime()
    {
        var row = SatoraLightningPayments.ToRow(FakeWallet.ArkadeToLightning("swap"), Store, Network.Main, PaymentHash, Now);

        Assert.True(row is { Direction: SatoraLightningDirection.ArkadeToLightning, FundingAttempted: false, FundingSats: 12_400, AmountMsat: 12_345_000 });
        Assert.Equal(FakeWallet.Locktime, row.ClaimCutoff);
    }

    // The server's answer must be for the invoice the plugin named, and take at least its amount.
    [Fact]
    public void RefusesASwapForAnotherInvoiceOrBelowItsAmount()
    {
        Assert.Throws<InvalidOperationException>(() =>
            SatoraLightningPayments.ToRow(FakeWallet.ArkadeToLightning("swap"), Store, Network.Main, new string('1', 64), Now));
        Assert.Throws<InvalidOperationException>(() =>
            SatoraLightningPayments.ToRow(FakeWallet.ArkadeToLightning("swap", fundingSats: 12_344), Store, Network.Main, PaymentHash, Now));
        Assert.NotNull(SatoraLightningPayments.ToRow(FakeWallet.ArkadeToLightning("swap"), Store, Network.Main, PaymentHash, Now));
    }

    private static SatoraLightningSwap Row(string swapId, SatoraSwapStatus status, bool fundingAttempted) => new()
    {
        SwapId = swapId,
        StoreId = Store,
        Direction = SatoraLightningDirection.ArkadeToLightning,
        Status = status,
        PaymentHash = swapId == "swap" ? PaymentHash : swapId,
        Bolt11 = Bolt11,
        AmountMsat = 12_345_000,
        FundingSats = 12_400,
        FundingAttempted = fundingAttempted,
        ClaimCutoff = FakeWallet.Locktime,
        ExpiresAt = Now.AddDays(1),
        CreatedAt = swapId == "open" ? Now.AddMinutes(1) : Now
    };
}
