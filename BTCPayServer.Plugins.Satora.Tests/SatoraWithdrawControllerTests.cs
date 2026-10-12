using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Plugins.Satora.Controllers;
using BTCPayServer.Plugins.Satora.Lightning;
using BTCPayServer.Plugins.Satora.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraWithdrawControllerTests
{
    private const string Store = "store";
    private const string Ark = "ark1qq4hfssprtcgnjzf8qlw2f78yvjau5kldfugg29k34y7j96q2w4t567uy9ukgfl2ntulwyyn8ky0yv26dmjpp6kfp6rcpm6t5wljw2es9qrsghuzz";

    private readonly MemoryAutoWithdrawRules _rules = new();
    private readonly SatoraWithdrawController _controller;

    public SatoraWithdrawControllerTests()
    {
        var events = new OutgoingEvents();
        var swaps = new MemoryOutgoingSwaps(events);
        var wallets = new FakeWalletAccess(new FakeWallet(events), events);
        var renewals = new RecordingRenewals();
        var service = new TestWithdrawalService(events, wallets, new MemoryWithdrawals(events, swaps), _rules,
            new SatoraLightningPayments(swaps, wallets, renewals, NullLogger<SatoraLightningPayments>.Instance), renewals, swaps);
        var http = new DefaultHttpContext();
        _controller = new SatoraWithdrawController(service)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, new MemoryTempDataProvider())
        };
    }

    [Fact]
    public async Task ShowsARefusedRuleAgainWithWhatWasEntered()
    {
        var request = new SatoraAutoWithdrawRequest { Destination = SatoraLightningCreditTests.Bolt11, ThresholdBtc = "0.001", MaxFeePercent = "1" };

        var result = await _controller.SaveAutoWithdraw(Store, request);
        var page = Assert.IsType<SatoraWithdrawPageModel>(Assert.IsType<ViewResult>(await _controller.Index(Store)).Model);

        Assert.Equal(nameof(SatoraWithdrawController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Contains("paid only once", (string)_controller.TempData[WellKnownTempData.ErrorMessage]!);
        Assert.Equal((SatoraLightningCreditTests.Bolt11, "0.001"), (page.AutoWithdrawForm?.Destination, page.AutoWithdrawForm?.ThresholdBtc));
        Assert.Null(page.AutoWithdraw);
    }

    // Refilled with the stale values under the current version, one more click would turn back on a
    // rule a co-owner just turned off.
    [Fact]
    public async Task ShowsTheStoredRuleAfterASaveRefusedBecauseTheRuleChanged()
    {
        await _controller.SaveAutoWithdraw(Store, new SatoraAutoWithdrawRequest { Destination = Ark, ThresholdBtc = "0.001", MaxFeePercent = "1" });
        await _controller.DisableAutoWithdraw(Store);

        await _controller.SaveAutoWithdraw(Store, new SatoraAutoWithdrawRequest { Destination = Ark, ThresholdBtc = "0.002", MaxFeePercent = "1", Version = 1 });
        var page = Assert.IsType<SatoraWithdrawPageModel>(Assert.IsType<ViewResult>(await _controller.Index(Store)).Model);

        Assert.Contains("changed since this page was loaded", (string)_controller.TempData[WellKnownTempData.ErrorMessage]!);
        Assert.Null(page.AutoWithdrawForm);
        Assert.True(page.AutoWithdraw is { Enabled: false, Version: 2, ThresholdSats: 100_000 });
    }

    [Fact]
    public async Task SavesARuleAndTurnsItOff()
    {
        await _controller.SaveAutoWithdraw(Store, new SatoraAutoWithdrawRequest { Destination = Ark, ThresholdBtc = "0.001", MaxFeePercent = "1" });
        Assert.Equal("Automatic withdrawal is on.", _controller.TempData[WellKnownTempData.SuccessMessage]);
        var page = Assert.IsType<SatoraWithdrawPageModel>(Assert.IsType<ViewResult>(await _controller.Index(Store)).Model);
        Assert.True(page.AutoWithdraw is { Enabled: true, Destination: Ark, Version: 1 });
        Assert.Null(page.AutoWithdrawForm);

        await _controller.DisableAutoWithdraw(Store);
        Assert.Equal("Automatic withdrawal is off.", _controller.TempData[WellKnownTempData.SuccessMessage]);
        await _controller.DisableAutoWithdraw(Store);
        Assert.Equal("Automatic withdrawal was off already.", _controller.TempData[WellKnownTempData.SuccessMessage]);
        Assert.True(_rules.Rows[Store] is { Enabled: false, Destination: Ark });
    }

    private sealed class MemoryTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values)
        {
        }
    }
}
