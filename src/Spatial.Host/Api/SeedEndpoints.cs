using System.Net;
using System.Text.Json.Nodes;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// Downloads a seed source over HTTP(S) under the ingest byte cap
/// (ADR-0078). Only absolute http(s) URLs are fetched; anything else is
/// <c>invalid.arguments</c>, so <c>file://</c> and other local schemes stay
/// rejected. Transport failures surface as <see cref="SpatialException"/>
/// failures the runner records per item.
/// </summary>
internal interface ISeedSourceFetcher
{
    Task<byte[]> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken);
}

/// <summary>The production <see cref="ISeedSourceFetcher"/> over <see cref="HttpClient"/>.</summary>
internal sealed class HttpSeedFetcher(HttpClient client) : ISeedSourceFetcher
{
    public async Task<byte[]> FetchAsync(string url, long maxBytes, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw SpatialException.BadArguments($"Seed source URL '{url}' must be an absolute http(s) URL.");
        }

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw SpatialException.Missing($"Download failed (404) for {url}.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw SpatialException.BadArguments($"Download failed ({(int)response.StatusCode}) for {url}.");
            }

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                total += read;
                if (total > maxBytes)
                {
                    throw SpatialException.BadArguments(
                        $"The download from '{url}' exceeds the configured maximum of {maxBytes} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw SpatialException.BadArguments($"Download timed out for {url}: {exception.Message}");
        }
        catch (HttpRequestException exception)
        {
            throw SpatialException.BadArguments($"Download failed for {url}: {exception.Message}");
        }
    }
}

/// <summary>
/// Lowers a seed map layer's compact draw recipe to the persisted MapLibre
/// fragment (ADR-0047/ADR-0053): the same lowering <c>tools/seed</c> and the
/// workbench composer perform, so endpoint-seeded and tool-seeded services
/// draw identically.
/// </summary>
internal static class SeedStyle
{
    private const string DefaultColor = "#4fc3f7";

    public static string Lower(SeedMapLayer layer)
    {
        var style = layer.Style;
        var color = string.IsNullOrWhiteSpace(style?.Color) ? DefaultColor : style!.Color;
        var opacity = style?.Opacity ?? 1;
        var lineWidth = style?.LineWidth ?? 2;
        var radius = style?.Radius ?? 5;
        var visibility = style?.Visible == false ? "none" : "visible";
        var geometry = (layer.Geometry ?? "mixed").Trim().ToLowerInvariant();
        var specs = new JsonArray();

        switch (geometry)
        {
            case "polygon":
            case "mixed":
                specs.Add(new JsonObject
                {
                    ["type"] = "fill",
                    ["layout"] = new JsonObject { ["visibility"] = visibility },
                    ["paint"] = new JsonObject
                    {
                        ["fill-color"] = color,
                        ["fill-opacity"] = opacity,
                        ["fill-outline-color"] = color,
                    },
                });
                break;
            case "point":
            case "line":
                break;
            default:
                throw SpatialException.BadArguments(
                    $"Seed layer '{layer.Dataset}' has an unknown geometry '{layer.Geometry}'; expected point, line, polygon or mixed.");
        }

        if (geometry is "line" or "polygon" or "mixed")
        {
            specs.Add(new JsonObject
            {
                ["type"] = "line",
                ["layout"] = new JsonObject { ["visibility"] = visibility },
                ["paint"] = new JsonObject
                {
                    ["line-color"] = color,
                    ["line-width"] = lineWidth,
                    ["line-opacity"] = opacity,
                },
            });
        }

        if (geometry is "point" or "mixed")
        {
            specs.Add(new JsonObject
            {
                ["type"] = "circle",
                ["layout"] = new JsonObject { ["visibility"] = visibility },
                ["paint"] = new JsonObject
                {
                    ["circle-color"] = color,
                    ["circle-radius"] = radius,
                    ["circle-opacity"] = opacity,
                    ["circle-stroke-color"] = "#0b0f14",
                    ["circle-stroke-width"] = 1,
                },
            });
        }

        return specs.ToJsonString();
    }
}

/// <summary>
/// Runs a seed document: download → decode → reproject → atomic ingest per
/// source, then map publication per service entry (ADR-0078). Sources and
/// maps selected out by <c>only</c> are skipped; every other failure is
/// recorded per item and the run continues, mirroring the seed tool's
/// summary semantics.
/// </summary>
internal sealed class SeedRunner(
    ISeedSourceFetcher fetcher,
    IStoreRegistry stores,
    ICoordinateTransforms transforms,
    IMapRegistry registry,
    IngestOptions ingest)
{
    public async Task<SeedResponse> RunAsync(SeedRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var store = string.IsNullOrWhiteSpace(request.Store) ? AdminEndpoints.DefaultIngestStore : request.Store;
        var only = request.Only is null ? null : new HashSet<string>(request.Only, StringComparer.Ordinal);
        var ingested = 0;
        var reused = 0;
        var published = 0;
        var failures = new List<SeedFailure>();

        foreach (var source in request.Sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (only is not null && !only.Contains(source.Id))
            {
                continue;
            }

            try
            {
                if (await SeedSourceAsync(store, source, request.Force, cancellationToken))
                {
                    ingested++;
                }
                else
                {
                    reused++;
                }
            }
            catch (SpatialException exception)
            {
                failures.Add(new SeedFailure(source.Id, exception.Code, exception.Message));
            }
        }

        foreach (var map in request.Maps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (only is not null && !only.Contains(map.Name))
            {
                continue;
            }

            try
            {
                await SeedMapAsync(store, map, cancellationToken);
                published++;
            }
            catch (SpatialException exception)
            {
                failures.Add(new SeedFailure(map.Name, exception.Code, exception.Message));
            }
        }

        return new SeedResponse(store, ingested, reused, published, failures);
    }

    private async Task<bool> SeedSourceAsync(
        string store, SeedSource source, bool force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(source.Id))
        {
            throw SpatialException.BadArguments("A seed source 'id' is required.");
        }

        if (string.IsNullOrWhiteSpace(source.Url))
        {
            throw SpatialException.BadArguments($"Seed source '{source.Id}' has no 'url'.");
        }

        if (source.Srid <= 0)
        {
            throw SpatialException.BadArguments($"Seed source '{source.Id}' has an invalid 'srid'.");
        }

        var target = stores.Ingest(store)
            ?? throw SpatialException.BadArguments($"Store '{store}' does not support ingest.");

        if (!force && await ExistsAsync(store, source.Id, cancellationToken))
        {
            return false;
        }

        var sourceSrid = source.SourceSrid == source.Srid ? null : source.SourceSrid;
        var bytes = await fetcher.FetchAsync(source.Url, ingest.MaxBytes, cancellationToken);
        var format = IngestPipeline.ParseFormat(ingest, source.Format ?? string.Empty);
        using var body = new MemoryStream(bytes, writable: false);
        var pages = IngestPipeline.DecodePages(body, format, sourceSrid ?? source.Srid, ingest, EmptyToNull(source.IdentityField));
        pages = IngestPipeline.ConvertIfNeeded(pages, sourceSrid, source.Srid, transforms, cancellationToken);
        var identity = IngestPipeline.ParseIdentity(source.Identity ?? string.Empty);
        await target.IngestAsync(
            new IngestRequest(source.Id, source.Srid, identity, EmptyToNull(source.IdentityField)),
            pages,
            cancellationToken);
        return true;
    }

    private async Task<bool> ExistsAsync(string store, string dataset, CancellationToken cancellationToken)
    {
        try
        {
            await stores.Catalogue(store).DescribeAsync(dataset, cancellationToken);
            return true;
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
            return false;
        }
    }

    private async Task SeedMapAsync(string store, SeedMap map, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(map.Name))
        {
            throw SpatialException.BadArguments("A seed map 'name' is required.");
        }

        if (map.Layers is null || map.Layers.Count == 0)
        {
            throw SpatialException.BadArguments($"Seed map '{map.Name}' has no layers.");
        }

        Map? existing = null;
        try
        {
            existing = await registry.GetAsync(map.Name, cancellationToken);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
        }

        // Preserve published ids so re-running never renumbers the layers; a
        // new layer asks the registry to assign the next free id.
        var existingIds = (existing?.Layers ?? []).ToDictionary(
            layer => layer.Dataset, layer => layer.LayerId, StringComparer.Ordinal);
        var layers = map.Layers.Select(layer => new MapLayer(
            layer.Dataset,
            existingIds.TryGetValue(layer.Dataset, out var id) ? id : -1,
            layer.Name,
            SeedStyle.Lower(layer),
            layer.Kind)).ToList();
        var named = new Map(map.Name, store, layers, map.Services, map.Description, map.Copyright);
        await AdminEndpoints.EnsureLayersAreServableAsync(stores, named, cancellationToken);
        await registry.PutAsync(named, cancellationToken);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
/// The development seed endpoint (ADR-0078): <c>POST /api/seed</c> runs a
/// seed document against the running host, so demo data no longer needs a
/// restart. Mounted only in Development; in production the route does not
/// exist. Enforces the admin gate through the shared
/// <see cref="AuthGuard"/> (ADR-0071), so it accepts a local bearer or the
/// legacy static admin token when one is configured.
/// </summary>
internal static class SeedEndpoints
{
    public static void Map(
        IEndpointRouteBuilder app,
        IHostEnvironment environment,
        AdminOptions admin,
        IngestOptions ingest,
        AuthOptions authOptions,
        IAuthService auth)
    {
        if (!environment.IsDevelopment())
        {
            return;
        }

        app.MapPost("/api/seed", (
            HttpContext context,
            SeedRequest? request,
            IStoreRegistry stores,
            ICoordinateTransforms transforms,
            IMapRegistry registry,
            ISeedSourceFetcher fetcher,
            CancellationToken token) => SeedAsync(
                new SeedRoute(context, admin, authOptions, auth, ingest, request, stores, transforms, registry, fetcher, token)));
    }

    private sealed record SeedRoute(
        HttpContext Context,
        AdminOptions Admin,
        AuthOptions AuthOptions,
        IAuthService Auth,
        IngestOptions Ingest,
        SeedRequest? Request,
        IStoreRegistry Stores,
        ICoordinateTransforms Transforms,
        IMapRegistry Registry,
        ISeedSourceFetcher Fetcher,
        CancellationToken Token);

    private static async Task<IResult> SeedAsync(SeedRoute route)
    {
        try
        {
            // Unlike the upload and map routes (unmounted without a token), the
            // seed route stays mounted in Development and only enforces the
            // admin gate when a policy is configured (ADR-0078). Inside the
            // try so a failed gate maps through the shared error mapper, like
            // every other admin route.
            if (route.Admin.Enabled || route.AuthOptions.Enabled)
            {
                await AuthorizeAsync(route);
            }

            if (route.Request is null)
            {
                throw SpatialException.BadArguments("A seed document is required.");
            }

            var runner = new SeedRunner(
                route.Fetcher, route.Stores, route.Transforms, route.Registry, route.Ingest);
            return Results.Ok(await runner.RunAsync(route.Request, route.Token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task AuthorizeAsync(SeedRoute route)
    {
        var header = route.Context.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
        await AuthGuard.RequireRoleAsync(
            route.Auth, presented, route.Admin.Token, AuthGuard.AdminRole, route.Token);
    }
}
