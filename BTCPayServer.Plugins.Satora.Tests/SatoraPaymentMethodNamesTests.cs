using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Satora.Chain;
using BTCPayServer.Plugins.Satora.Services;
using BTCPayServer.Services;
using Microsoft.Extensions.Localization;
using Xunit;

namespace BTCPayServer.Plugins.Satora.Tests;

public class SatoraPaymentMethodNamesTests
{
    private static readonly PaymentMethodId Lightning = PaymentTypes.LN.GetPaymentMethodId("BTC");
    private static readonly PaymentMethodId Unnamed = new("XYZ-CHAIN");

    // As BTCPay's localizer serves the defaults AddDefaultPrettyName registers. XYZ-CHAIN has none, so
    // PrettyName falls back to the id.
    private static readonly PrettyNameProvider PrettyNames = new([], new Translations(new()
    {
        [PrettyNameProvider.GetTranslationKey(Lightning)] = "Lightning",
        [PrettyNameProvider.GetTranslationKey(SatoraChainPaymentMethod.Id)] = SatoraChainPaymentMethod.PrettyName
    }));

    private static readonly Dictionary<string, string> Names = new()
    {
        ["BTC-CHAIN"] = "Bitcoin",
        ["BTC-LN"] = "Lightning",
        ["BTC-SATORA-CHAIN"] = "Bitcoin (Satora)"
    };

    [Fact]
    public void NamesEachMethodThatHasAPrettyNameAndLeavesTheRestOut()
    {
        var names = SatoraPaymentMethodNames.For("store", [Lightning, SatoraChainPaymentMethod.Id, Unnamed], PrettyNames);

        Assert.Equal(new Dictionary<string, string>
        {
            ["BTC-LN"] = "Lightning",
            ["BTC-SATORA-CHAIN"] = "Bitcoin (Satora)"
        }, names);
    }

    [Fact]
    public void NamesNothingOutsideAStore()
    {
        Assert.Empty(SatoraPaymentMethodNames.For(null, [Lightning, SatoraChainPaymentMethod.Id], PrettyNames));
    }

    // The markup is BTCPay v2.4.4's: the create invoice form's checkboxes and default payment method, a
    // checkout settings amount criteria row and an invoice's payments. The ids are only there to find
    // each element.
    [Fact]
    public void RelabelsTheIdsBTCPayListsAndKeepsWhatItsFormsSubmit()
    {
        var page = Parse("""
            <form>
              <div class="form-check mb-2">
                <label class="form-check-label" id="checkbox">
                  <input name="SupportedTransactionCurrencies" class="form-check-input" checked="checked" type="checkbox" value="BTC-SATORA-CHAIN">
                  BTC-SATORA-CHAIN
                </label>
              </div>
              <select name="DefaultPaymentMethod" class="form-select">
                <option value="" selected>Use the store's default</option>
                <option value="BTC-LN" id="option">BTC-LN</option>
              </select>
              <table class="table table-sm mt-0 mx-0">
                <tr>
                  <td class="border-0 ps-0 align-middle" id="criteria">
                    <input type="hidden" name="PaymentMethodCriteria[0].PaymentMethod" value="BTC-SATORA-CHAIN">
                    BTC-SATORA-CHAIN
                  </td>
                </tr>
              </table>
            </form>
            <table>
              <thead><tr><th id="header">BTC-CHAIN</th></tr></thead>
              <tbody>
                <tr><td class="text-nowrap" id="payment">BTC-SATORA-CHAIN</td></tr>
                <tr><td id="longer">BTC-SATORA-CHAIN payment</td></tr>
                <tr><td id="split">BTC-<b>LN</b></td></tr>
              </tbody>
            </table>
            <p id="paragraph">BTC-LN</p>
            """);
        var submitted = Submitted(page);

        Relabel(page);

        Assert.Equal("Bitcoin (Satora)", Text(page, "checkbox"));
        Assert.NotNull(page.QuerySelector("#checkbox > input[type=checkbox]"));
        Assert.Equal("Lightning", Text(page, "option"));
        Assert.Equal("Bitcoin (Satora)", Text(page, "criteria"));
        Assert.Equal("Bitcoin", Text(page, "header"));
        Assert.Equal("Bitcoin (Satora)", Text(page, "payment"));
        Assert.Equal("BTC-SATORA-CHAIN payment", Text(page, "longer"));
        Assert.Equal("BTC-LN", Text(page, "split"));
        Assert.Equal("BTC-LN", Text(page, "paragraph"));
        Assert.Equal(submitted, Submitted(page));
    }

    [Fact]
    public void NeverRelabelsTextAFormSubmits()
    {
        var page = Parse("""
            <form>
              <select name="Bare"><option selected>BTC-LN</option></select>
              <table>
                <tr>
                  <th><select name="InHeader"><option selected>BTC-CHAIN</option></select></th>
                  <td><select name="InCell"><option selected>BTC-LN</option></select></td>
                  <td><textarea name="Notes">BTC-CHAIN</textarea></td>
                </tr>
              </table>
              <label class="form-check-label"><select name="InLabel"><option selected>BTC-SATORA-CHAIN</option></select></label>
            </form>
            """);
        var submitted = Submitted(page);
        Assert.Equal(new[] { "Bare=BTC-LN", "InHeader=BTC-CHAIN", "InCell=BTC-LN", "InLabel=BTC-SATORA-CHAIN", "Notes=BTC-CHAIN" }, submitted);

        Relabel(page);

        Assert.Equal(submitted, Submitted(page));
    }

    private static IDocument Parse(string body) => new HtmlParser().ParseDocument($"<!doctype html><html><body>{body}</body></html>");

    private static string Text(IDocument page, string id) => Assert.IsAssignableFrom<IElement>(page.GetElementById(id)).TextContent.Trim();

    // The partial's script, step for step, with the selectors it is given.
    private static void Relabel(IDocument page)
    {
        foreach (var element in page.QuerySelectorAll(SatoraPaymentMethodNames.Labels))
        {
            var id = element.TextContent.Trim();
            if (!Names.TryGetValue(id, out var name))
                continue;
            var text = element.Descendants<IText>().FirstOrDefault(t => t.Data.Trim() == id && t.ParentElement?.Closest(SatoraPaymentMethodNames.FormText) is null);
            if (text is not null)
                text.Data = text.Data.Replace(id, name);
        }
    }

    // Each control's name and the value a browser submits for it.
    private static string[] Submitted(IDocument page) =>
    [
        ..page.QuerySelectorAll<IHtmlInputElement>("input").Select(i => $"{i.Name}={i.Value}"),
        ..page.QuerySelectorAll<IHtmlSelectElement>("select").Select(s => $"{s.Name}={s.Value}"),
        ..page.QuerySelectorAll<IHtmlTextAreaElement>("textarea").Select(t => $"{t.Name}={t.Value}")
    ];

    private sealed class Translations(Dictionary<string, string> texts) : IStringLocalizer
    {
        public LocalizedString this[string name] =>
            texts.TryGetValue(name, out var text) ? new(name, text) : new(name, name, resourceNotFound: true);

        public LocalizedString this[string name, params object[] arguments] => this[name];

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) =>
            texts.Select(t => new LocalizedString(t.Key, t.Value));
    }
}
