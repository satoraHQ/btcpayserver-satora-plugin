using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Configuration;
using BTCPayServer.Plugins.Satora.Lightning;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

// The worker sends the claim deadline alert also after the deadline, for a claim still being
// finished, so its text follows when BTCPay says it was sent, not when it is read.
public class SatoraClaimDeadlineAlertTests
{
    private static readonly DateTimeOffset Deadline = new(2026, 10, 3, 14, 15, 55, TimeSpan.Zero);
    private static readonly BTCPayServerOptions Options = new() { RootPath = "/" };

    public static TheoryData<int, bool> SentAt => new()
    {
        { -600, false },
        { -1, false },
        { 0, true },
        { 42, true }
    };

    [Theory]
    [MemberData(nameof(SentAt))]
    public void TheLightningAlertSaysTheDeadlinePassedWhenSentFromIt(int secondsFromDeadline, bool passed)
    {
        INotificationHandler handler = new SatoraLightningNotification.Handler(new RouteLinks(), Options);
        var vm = new NotificationViewModel { Created = Deadline.AddSeconds(secondsFromDeadline) };

        handler.FillViewModel(new SatoraLightningNotification
        {
            StoreId = "store",
            SwapId = "swap",
            Problem = SatoraLightningNotification.Problems.NearClaimCutoff,
            ClaimCutoff = Deadline
        }, vm);

        Assert.Equal(passed
            ? "Satora swap swap is paid but was not claimed into the Satora wallet by its claim deadline, 2026-10-03 14:15:55Z. Unless a claim already in progress completes, the payment goes back to the payer and does not pay the invoice. The return can take hours."
            : "Satora swap swap is paid but not yet claimed into the Satora wallet. If it is not claimed by 2026-10-03 14:15:55Z, the payment goes back to the payer and does not pay the invoice.",
            vm.Body);
    }
}
