using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Data;
using BTCPayServer.Plugins.Satora.Services;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Satora.Payments;

public class SatoraPaymentMethodHandler(
    SatoraPluginDbContextFactory pluginDbContextFactory,
    SatoraService satoraService) : IPaymentMethodHandler
{
    public PaymentMethodId PaymentMethodId => SatoraPaymentMethod.PaymentMethodId;
    public JsonSerializer Serializer { get; } = JsonSerializer.CreateDefault();

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = SatoraPaymentMethod.Currency;
        context.Prompt.Divisibility = 8;
        context.Prompt.RateDivisibility = 8;
        context.Prompt.PaymentMethodFee = 0m;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        await using var db = pluginDbContextFactory.CreateContext();
        var settings = await db.SatoraSettings.FirstOrDefaultAsync(s => s.StoreId == context.Store.Id);
        if (settings is null || !settings.Enabled || string.IsNullOrEmpty(settings.Seed))
            throw new PaymentMethodUnavailableException("Satora wallet is not enabled for this store");

        var arkadeAddress = await satoraService.GetArkadeAddressAsync(settings.Seed);
        context.Prompt.Destination = arkadeAddress;
        context.Prompt.Details = JObject.FromObject(new SatoraPaymentPromptDetails
        {
            ArkadeAddress = arkadeAddress
        }, Serializer);
        context.TrackedDestinations.Add(arkadeAddress);
    }

    public object ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<SatoraPaymentPromptDetails>(Serializer)
               ?? throw new FormatException($"Invalid {nameof(SatoraPaymentPromptDetails)}");
    }

    public object ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<SatoraPaymentMethodConfig>(Serializer)
               ?? throw new FormatException($"Invalid {nameof(SatoraPaymentMethodConfig)}");
    }

    public object ParsePaymentDetails(JToken details)
    {
        return details.ToObject<SatoraPaymentData>(Serializer)
               ?? throw new FormatException($"Invalid {nameof(SatoraPaymentData)}");
    }
}
