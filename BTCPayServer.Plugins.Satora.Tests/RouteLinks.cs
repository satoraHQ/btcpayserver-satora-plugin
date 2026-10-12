using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace BTCPayServer.Plugins.Satora.Tests;

// Renders a notification's action link as controller/action/storeId, or invoiceId for an invoice's page, so a
// handler can be tested without ASP.NET routing.
internal sealed class RouteLinks : LinkGenerator
{
    public override string GetPathByAddress<TAddress>(TAddress address, RouteValueDictionary values, PathString pathBase = default,
        FragmentString fragment = default, LinkOptions? options = null) =>
        $"{values["controller"]}/{values["action"]}/{values["storeId"] ?? values["invoiceId"]}";

    public override string GetPathByAddress<TAddress>(HttpContext httpContext, TAddress address, RouteValueDictionary values,
        RouteValueDictionary? ambientValues = null, PathString? pathBase = null, FragmentString fragment = default, LinkOptions? options = null) =>
        throw new NotSupportedException();

    public override string GetUriByAddress<TAddress>(TAddress address, RouteValueDictionary values, string? scheme, HostString host,
        PathString pathBase = default, FragmentString fragment = default, LinkOptions? options = null) =>
        throw new NotSupportedException();

    public override string GetUriByAddress<TAddress>(HttpContext httpContext, TAddress address, RouteValueDictionary values,
        RouteValueDictionary? ambientValues = null, string? scheme = null, HostString? host = null, PathString? pathBase = null,
        FragmentString fragment = default, LinkOptions? options = null) =>
        throw new NotSupportedException();
}
