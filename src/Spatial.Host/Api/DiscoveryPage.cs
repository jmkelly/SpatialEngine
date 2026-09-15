using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// The host's discovery page (ADR-0018): one dependency-free HTML index of
/// every endpoint the process serves and every map exposed on each protocol.
/// It reads live state — the routing table and the map registry — so it
/// cannot drift from what is actually mounted. Documentation only: no spatial
/// logic and no client dependency.
/// </summary>
internal static class DiscoveryPage
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var sources = app.DataSources;
        app.MapGet("/routes", (
            HttpContext context,
            IMapRegistry maps,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
            ShowAsync(sources, context, maps, configuration, cancellationToken));
    }

    private static async Task<IResult> ShowAsync(
        ICollection<EndpointDataSource> sources,
        HttpContext context,
        IMapRegistry maps,
        IConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var origin = $"{context.Request.Scheme}://{context.Request.Host}";
        var esriRoot = configuration["Spatial:GeoServices:Root"] ?? "/arcgis/rest/services";
        var ogcRoot = configuration["Spatial:Ogc:Root"] ?? "/ogc";
        var published = await maps.ListAsync(cancellationToken);

        var html = new StringBuilder(16_384);
        html.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">")
            .Append("<title>Spatial.Host — routes</title><style>").Append(Css).Append("</style></head><body>");
        AppendHeader(html, origin, esriRoot, ogcRoot);
        AppendMaps(html, origin, esriRoot, ogcRoot, published);
        AppendRoutes(html, sources);
        html.Append("</body></html>");
        return Results.Content(html.ToString(), "text/html; charset=utf-8");
    }

    private static void AppendHeader(StringBuilder html, string origin, string esriRoot, string ogcRoot) =>
        html.Append("<header><h1>Spatial.Host</h1>")
            .Append("<p class=\"muted\">Live index of the routes and services on this host · ")
            .Append("<a href=\"/openapi/v1.json\">OpenAPI</a> · <a href=\"/health/ready\">health</a></p>")
            .Append("<dl class=\"roots\"><dt>Esri REST root</dt><dd><code>")
            .Append(Html($"{origin}{esriRoot}"))
            .Append("</code></dd><dt>OGC root</dt><dd><code>")
            .Append(Html($"{origin}{ogcRoot}"))
            .Append("</code></dd></dl></header>");

    private static void AppendMaps(
        StringBuilder html, string origin, string esriRoot, string ogcRoot, IReadOnlyList<Map> maps)
    {
        html.Append("<h2>Maps</h2>");
        if (maps.Count == 0)
        {
            html.Append("<p class=\"muted\">No maps published yet — create one with ")
                .Append("<code>PUT /api/maps/{name}</code>.</p>");
            return;
        }

        html.Append("<div class=\"maps\">");
        foreach (var map in maps)
        {
            AppendMap(html, origin, esriRoot, ogcRoot, map);
        }

        html.Append("</div>");
    }

    private static void AppendMap(StringBuilder html, string origin, string esriRoot, string ogcRoot, Map map)
    {
        html.Append("<article class=\"card\"><div class=\"card-head\"><h3>").Append(Html(map.Name)).Append("</h3>")
            .Append("<span class=\"store\">").Append(Html(map.Store)).Append("</span></div>");
        if (!string.IsNullOrWhiteSpace(map.Description))
        {
            html.Append("<p class=\"desc\">").Append(Html(map.Description)).Append("</p>");
        }

        html.Append("<p class=\"muted\">").Append(map.Layers.Count).Append(" layer(s) · ")
            .Append(Html(string.Join(", ", map.Layers.Select(LayerName)))).Append("</p>")
            .Append("<ul class=\"services\">");
        foreach (var link in ServiceLinks(origin, esriRoot, ogcRoot, map))
        {
            html.Append("<li><span class=\"badge\">").Append(Html(link.Badge)).Append("</span>")
                .Append("<a href=\"").Append(Html(link.Href)).Append("\">").Append(Html(link.Text)).Append("</a></li>");
        }

        html.Append("</ul>");
        if (PreviewUrl(origin, esriRoot, ogcRoot, map) is { } preview)
        {
            html.Append("<a class=\"preview\" href=\"").Append(Html(preview)).Append("\"><img loading=\"lazy\" src=\"")
                .Append(Html(preview)).Append("\" alt=\"Preview of ").Append(Html(map.Name)).Append("\"></a>");
        }

        html.Append("</article>");
    }

    private static void AppendRoutes(StringBuilder html, ICollection<EndpointDataSource> sources)
    {
        html.Append("<h2>Routes</h2>")
            .Append("<p class=\"muted\">Every endpoint mounted on this host, read from the live routing table.</p>");
        foreach (var group in GroupRoutes(Routes(sources)))
        {
            html.Append("<section class=\"routes\"><h3>").Append(Html(group.Key)).Append("</h3>");
            if (GroupNotes.TryGetValue(group.Key, out var note))
            {
                html.Append("<p class=\"muted\">").Append(Html(note)).Append("</p>");
            }

            html.Append("<table><tbody>");
            foreach (var route in group.OrderBy(route => route.Pattern, StringComparer.Ordinal))
            {
                html.Append("<tr><td class=\"methods\">");
                foreach (var method in route.Methods)
                {
                    html.Append("<span class=\"method\">").Append(Html(method)).Append("</span>");
                }

                html.Append("</td><td><code>").Append(Html(route.Pattern)).Append("</code></td></tr>");
            }

            html.Append("</tbody></table></section>");
        }
    }

    private static IEnumerable<RouteEntry> Routes(ICollection<EndpointDataSource> sources)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var endpoint in sources.SelectMany(source => source.Endpoints))
        {
            if (endpoint is not RouteEndpoint route)
            {
                continue;
            }

            var pattern = "/" + (route.RoutePattern.RawText ?? string.Empty).TrimStart('/');
            var methods = route.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            if (seen.Add($"{pattern}|{string.Join(",", methods)}"))
            {
                yield return new RouteEntry(pattern, [.. methods]);
            }
        }
    }

    private static IEnumerable<IGrouping<string, RouteEntry>> GroupRoutes(IEnumerable<RouteEntry> routes) =>
        routes.GroupBy(route => GroupName(route.Pattern)).OrderBy(group => GroupOrder(group.Key));

    private static string GroupName(string pattern) => pattern.Split('/', StringSplitOptions.RemoveEmptyEntries) switch
    {
        [] => "Host",
        ["health", ..] => "Health",
        ["openapi", ..] => "OpenAPI",
        ["api", ..] => "Engine API",
        ["arcgis", ..] => "Esri GeoServices REST",
        ["ogc", ..] => "OGC WMS / WFS",
        ["routes", ..] => "Host",
        var segments => segments[0],
    };

    private static int GroupOrder(string group) => group switch
    {
        "Host" => 0,
        "Health" => 1,
        "OpenAPI" => 2,
        "Engine API" => 3,
        "Esri GeoServices REST" => 4,
        "OGC WMS / WFS" => 5,
        _ => 6,
    };

    private static List<ServiceLink> ServiceLinks(string origin, string esriRoot, string ogcRoot, Map map)
    {
        var name = Uri.EscapeDataString(map.Name);
        var links = new List<ServiceLink>();
        if (map.Exposes(MapServiceKind.FeatureServer))
        {
            links.Add(new ServiceLink("FeatureServer", $"{origin}{esriRoot}/{name}/FeatureServer", $"{origin}{esriRoot}/{name}/FeatureServer?f=json"));
        }

        if (map.Exposes(MapServiceKind.MapServer))
        {
            links.Add(new ServiceLink("MapServer", $"{origin}{esriRoot}/{name}/MapServer", $"{origin}{esriRoot}/{name}/MapServer?f=json"));
        }

        if (map.Exposes(MapServiceKind.ImageServer))
        {
            links.Add(new ServiceLink("ImageServer", $"{origin}{esriRoot}/{name}/ImageServer", $"{origin}{esriRoot}/{name}/ImageServer?f=json"));
        }

        if (map.Exposes(MapServiceKind.Wms))
        {
            links.Add(new ServiceLink("WMS", $"{origin}{ogcRoot}/{name}/wms", $"{origin}{ogcRoot}/{name}/wms?service=WMS&request=GetCapabilities"));
        }

        if (map.Exposes(MapServiceKind.Wfs))
        {
            links.Add(new ServiceLink("WFS", $"{origin}{ogcRoot}/{name}/wfs", $"{origin}{ogcRoot}/{name}/wfs?service=WFS&request=GetCapabilities"));
        }

        if (map.Exposes(MapServiceKind.Tiles))
        {
            links.Add(new ServiceLink("Tiles", $"{origin}/api/maps/{name}/tiles/{{z}}/{{x}}/{{y}}.png", $"{origin}/api/maps/{name}/tiles/0/0/0.png"));
        }

        return links;
    }

    private static string? PreviewUrl(string origin, string esriRoot, string ogcRoot, Map map)
    {
        var name = Uri.EscapeDataString(map.Name);
        if (map.Exposes(MapServiceKind.Wms))
        {
            // CRS:84 keeps the bbox x-first; EPSG:4326 is latitude-first in WMS 1.3.0.
            return $"{origin}{ogcRoot}/{name}/wms?service=WMS&version=1.3.0&request=GetMap&crs=CRS:84"
                + "&bbox=-180,-85,180,85&width=360&height=180&format=image/png&transparent=false";
        }

        if (map.Exposes(MapServiceKind.MapServer))
        {
            return $"{origin}{esriRoot}/{name}/MapServer/export?bbox=-180,-85,180,85&bboxSR=4326&imageSR=4326"
                + "&size=360,180&format=png&f=image&transparent=false";
        }

        return map.Exposes(MapServiceKind.Tiles) ? $"{origin}/api/maps/{name}/tiles/0/0/0.png" : null;
    }

    private static string LayerName(MapLayer layer) => layer.Name ?? layer.Dataset;

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private const string Css = """
        :root { color-scheme: dark; --bg: #0b0f14; --panel: #121a23; --line: #22303c; --ink: #e6edf3; --muted: #8b9bab; --accent: #58a6ff; }
        * { box-sizing: border-box; }
        body { margin: 0; padding: 2rem clamp(1rem, 4vw, 3rem) 4rem; background: var(--bg); color: var(--ink);
               font: 15px/1.55 ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; }
        h1 { margin: 0 0 .25rem; font-size: 1.6rem; letter-spacing: -.01em; }
        h2 { margin: 2.5rem 0 .5rem; font-size: 1.15rem; border-bottom: 1px solid var(--line); padding-bottom: .4rem; }
        h3 { margin: 0 0 .35rem; font-size: 1rem; }
        a { color: var(--accent); text-decoration: none; }
        a:hover { text-decoration: underline; }
        code { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: .86em; }
        .muted { color: var(--muted); margin: .35rem 0 .9rem; }
        header .roots { display: flex; flex-wrap: wrap; gap: .25rem 1rem; margin: .75rem 0 0; }
        header .roots dt { color: var(--muted); font-size: .82rem; text-transform: uppercase; letter-spacing: .06em; }
        header .roots dd { margin: 0 1.5rem 0 0; }
        .maps { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 1rem; align-items: start; }
        .card { background: var(--panel); border: 1px solid var(--line); border-radius: 10px; padding: 1rem 1.1rem 1.2rem; }
        .card-head { display: flex; align-items: baseline; justify-content: space-between; gap: .5rem; }
        .store { color: var(--muted); font-size: .78rem; border: 1px solid var(--line); border-radius: 999px; padding: .05rem .5rem; }
        .desc { margin: .1rem 0 .5rem; }
        .services { list-style: none; margin: .5rem 0 0; padding: 0; display: grid; gap: .3rem; }
        .services li { display: flex; gap: .55rem; align-items: baseline; overflow-wrap: anywhere; }
        .services a { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; font-size: .82rem; }
        .badge { flex: none; font-size: .72rem; font-weight: 600; letter-spacing: .04em; color: #0b0f14; background: #7ee787;
                 border-radius: 5px; padding: .05rem .4rem; }
        .preview { display: block; margin-top: .9rem; border: 1px solid var(--line); border-radius: 8px; overflow: hidden; }
        .preview img { display: block; width: 100%; height: auto; background: #fff; }
        .routes table { width: 100%; border-collapse: collapse; margin: .4rem 0 0; }
        .routes td { border-top: 1px solid var(--line); padding: .3rem .4rem .3rem 0; vertical-align: top; }
        .routes td.methods { width: 10rem; white-space: nowrap; }
        .method { display: inline-block; font-size: .7rem; font-weight: 600; color: var(--muted); border: 1px solid var(--line);
                  border-radius: 5px; padding: .02rem .35rem; margin-right: .25rem; }
        """;

    private static readonly Dictionary<string, string> GroupNotes = new(StringComparer.Ordinal)
    {
        ["Host"] = "Identity and discovery.",
        ["Health"] = "Liveness and readiness probes.",
        ["OpenAPI"] = "The machine-readable contract, source of the generated client types.",
        ["Engine API"] = "The typed, one-contract API (ADR-0033): geometry, CRS, stores, maps and rendering.",
        ["Esri GeoServices REST"] = "The Esri boundary adapter (ADR-0035): FeatureServer, MapServer, ImageServer, Geometry Service and admin.",
        ["OGC WMS / WFS"] = "The read-only OGC projections of the same maps (ADR-0053 §3).",
    };

    private readonly record struct RouteEntry(string Pattern, IReadOnlyList<string> Methods);

    private readonly record struct ServiceLink(string Badge, string Text, string Href);
}
