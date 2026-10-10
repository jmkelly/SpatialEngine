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
        var uri = RequireHttpUrl(url);
        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            RequireDownloadable(url, response);
            return await ReadCappedAsync(url, response, maxBytes, cancellationToken);
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

    private static Uri RequireHttpUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return uri;
        }

        throw SpatialException.BadArguments($"Seed source URL '{url}' must be an absolute http(s) URL.");
    }

    private static void RequireDownloadable(string url, HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw SpatialException.Missing($"Download failed (404) for {url}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw SpatialException.BadArguments($"Download failed ({(int)response.StatusCode}) for {url}.");
        }
    }

    private static async Task<byte[]> ReadCappedAsync(
        string url, HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
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
        var recipe = new LayerRecipe(
            string.IsNullOrWhiteSpace(style?.Color) ? DefaultColor : style!.Color,
            style?.Opacity ?? 1,
            style?.LineWidth ?? 2,
            style?.Radius ?? 5,
            style?.Visible == false ? "none" : "visible",
            RequireGeometry(layer),
            string.IsNullOrWhiteSpace(style?.OutlineColor) ? null : style!.OutlineColor,
            style?.OutlineOpacity ?? style?.Opacity ?? 1);
        var specs = new JsonArray();
        AddFillSpec(specs, recipe);
        AddLineSpec(specs, recipe);
        AddCircleSpec(specs, recipe);
        return specs.ToJsonString();
    }

    /// <summary>The layer's draw values with the style defaults applied.</summary>
    private sealed record LayerRecipe(string Color, double Opacity, double LineWidth, double Radius, string Visibility, string Geometry, string? OutlineColor, double OutlineOpacity);

    /// <summary>The stroke color: the explicit outline when authored, else the fill color.</summary>
    private static string Stroke(LayerRecipe recipe) => recipe.OutlineColor ?? recipe.Color;

    /// <summary>The layer's geometry as the lowering reads it: trimmed, lowered, and refused by name when unknown.</summary>
    private static string RequireGeometry(SeedMapLayer layer)
    {
        var geometry = (layer.Geometry ?? "mixed").Trim().ToLowerInvariant();
        return geometry switch
        {
            "polygon" or "mixed" or "point" or "line" => geometry,
            _ => throw SpatialException.BadArguments(
                $"Seed layer '{layer.Dataset}' has an unknown geometry '{layer.Geometry}'; expected point, line, polygon or mixed."),
        };
    }

    private static void AddFillSpec(JsonArray specs, LayerRecipe recipe)
    {
        if (recipe.Geometry is not ("polygon" or "mixed"))
        {
            return;
        }

        specs.Add(new JsonObject
        {
            ["type"] = "fill",
            ["layout"] = new JsonObject { ["visibility"] = recipe.Visibility },
            ["paint"] = new JsonObject
            {
                ["fill-color"] = recipe.Color,
                ["fill-opacity"] = recipe.Opacity,
                ["fill-outline-color"] = Stroke(recipe),
                ["fill-outline-width"] = recipe.LineWidth,
            },
        });
    }

    private static void AddLineSpec(JsonArray specs, LayerRecipe recipe)
    {
        if (recipe.Geometry is not ("line" or "polygon" or "mixed"))
        {
            return;
        }

        specs.Add(new JsonObject
        {
            ["type"] = "line",
            ["layout"] = new JsonObject { ["visibility"] = recipe.Visibility },
            ["paint"] = new JsonObject
            {
                ["line-color"] = Stroke(recipe),
                ["line-width"] = recipe.LineWidth,
                ["line-opacity"] = recipe.OutlineOpacity,
            },
        });
    }

    private static void AddCircleSpec(JsonArray specs, LayerRecipe recipe)
    {
        if (recipe.Geometry is not ("point" or "mixed"))
        {
            return;
        }

        specs.Add(new JsonObject
        {
            ["type"] = "circle",
            ["layout"] = new JsonObject { ["visibility"] = recipe.Visibility },
            ["paint"] = new JsonObject
            {
                ["circle-color"] = recipe.Color,
                ["circle-radius"] = recipe.Radius,
                ["circle-opacity"] = recipe.Opacity,
                ["circle-stroke-color"] = "#0b0f14",
                ["circle-stroke-width"] = 1,
            },
        });
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
        var counters = new SeedCounters();

        await RunSourcesAsync(store, request, only, counters, cancellationToken);
        await RunMapsAsync(store, request, only, counters, cancellationToken);

        return counters.ToResponse(store);
    }

    private async Task RunSourcesAsync(
        string store, SeedRequest request, HashSet<string>? only, SeedCounters counters, CancellationToken cancellationToken)
    {
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
                    counters.Ingested++;
                }
                else
                {
                    counters.Reused++;
                }
            }
            catch (SpatialException exception)
            {
                counters.Fail(source.Id, exception);
            }
        }
    }

    private async Task RunMapsAsync(
        string store, SeedRequest request, HashSet<string>? only, SeedCounters counters, CancellationToken cancellationToken)
    {
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
                counters.Published++;
            }
            catch (SpatialException exception)
            {
                counters.Fail(map.Name, exception);
            }
        }
    }

    /// <summary>The run's running tally: per-item counters plus the failures the run records and continues past.</summary>
    private sealed class SeedCounters
    {
        public int Ingested { get; set; }

        public int Reused { get; set; }

        public int Published { get; set; }

        private readonly List<SeedFailure> _failures = [];

        public void Fail(string target, SpatialException exception) =>
            _failures.Add(new SeedFailure(target, exception.Code, exception.Message));

        public SeedResponse ToResponse(string store) => new(store, Ingested, Reused, Published, _failures);
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
        var identity = IngestPipeline.ParseIdentity(source.Identity ?? string.Empty);
        var identityField = EmptyToNull(source.IdentityField);
        await IngestPipeline.LoadAsync(
            body,
            format,
            source.Id,
            source.Srid,
            sourceSrid,
            target,
            target as IDatasetIngestStream,
            ingest,
            identity,
            identityField,
            transforms,
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
