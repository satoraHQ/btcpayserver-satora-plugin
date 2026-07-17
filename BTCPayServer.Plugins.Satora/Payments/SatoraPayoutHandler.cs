using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.HostedServices;
using BTCPayServer.Payouts;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using PayoutData = BTCPayServer.Data.PayoutData;
using StoreData = BTCPayServer.Data.StoreData;

namespace BTCPayServer.Plugins.Satora.Payments;

public class SatoraPayoutHandler(PaymentMethodHandlerDictionary paymentHandlers) : IPayoutHandler
{
    public string Currency => SatoraPaymentMethod.Currency;
    public PayoutMethodId PayoutMethodId => SatoraPaymentMethod.PayoutMethodId;

    public bool IsSupported(StoreData storeData)
    {
        return storeData.GetPaymentMethodConfig<SatoraPaymentMethodConfig>(
            SatoraPaymentMethod.PaymentMethodId,
            paymentHandlers,
            true)?.Enabled is true;
    }

    public Task TrackClaim(ClaimRequest claimRequest, PayoutData payoutData)
    {
        return Task.CompletedTask;
    }

    public Task<(IClaimDestination destination, string error)> ParseClaimDestination(string destination, CancellationToken cancellationToken)
    {
        destination = destination.Trim();
        if (string.IsNullOrEmpty(destination))
            return Task.FromResult<(IClaimDestination, string)>((null!, "A Satora Arkade address was not provided."));

        return Task.FromResult<(IClaimDestination, string)>((new SatoraArkadeClaimDestination(destination), null!));
    }

    public (bool valid, string? error) ValidateClaimDestination(IClaimDestination claimDestination, PullPaymentBlob? pullPaymentBlob)
    {
        return claimDestination is SatoraArkadeClaimDestination
            ? (true, null)
            : (false, "A valid Satora Arkade destination was not provided.");
    }

    public IPayoutProof ParseProof(PayoutData payout)
    {
        if (payout.Proof is null)
            return null!;

        var proof = JObject.Parse(payout.Proof);
        return proof.ToObject<ManualPayoutProof>() ?? null!;
    }

    public void StartBackgroundCheck(Action<Type[]> subscribe)
    {
    }

    public Task BackgroundCheck(object o)
    {
        return Task.CompletedTask;
    }

    public Task<decimal> GetMinimumPayoutAmount(IClaimDestination claimDestination)
    {
        return Task.FromResult(0.00000001m);
    }

    public Dictionary<PayoutState, List<(string Action, string Text)>> GetPayoutSpecificActions()
    {
        return new Dictionary<PayoutState, List<(string Action, string Text)>>();
    }

    public Task<StatusMessageModel> DoSpecificAction(string action, string[] payoutIds, string storeId)
    {
        return Task.FromResult(new StatusMessageModel
        {
            Severity = StatusMessageModel.StatusSeverity.Error,
            Message = "Satora payout execution will be available with the withdraw flow."
        });
    }

    public Task<IActionResult> InitiatePayment(string[] payoutIds)
    {
        return Task.FromResult<IActionResult>(new BadRequestObjectResult("Satora payout execution will be available with the withdraw flow."));
    }
}
