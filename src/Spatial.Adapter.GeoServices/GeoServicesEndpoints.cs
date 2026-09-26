using System.Collections.Frozen;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Mounts the GeoServices REST route group (ADR-0035, ADR-0041 §5): the
/// catalog, the Geometry Service and Feature Servers (query, feature resource
/// and gated editing). A Feature Server is resolved from the
/// <see cref="IMapRegistry"/> — a runtime publication created through
/// the neutral admin API is served immediately, with its persisted stable
/// layer ids — while configuration-declared services keep the whole-store
/// expansion (every dataset in the store, sorted, ids assigned once). Every
/// handler negotiates <c>f=json</c>, reads the merged request parameters and
/// maps failures to the Esri error envelope. This type is the composition
/// root of the projection: it owns the catalog resource and mounts each
/// server's routes — <see cref="GeometryServerEndpoints"/>,
/// <see cref="FeatureServerEndpoints"/>, <see cref="FeatureEditEndpoints"/>,
/// <see cref="FeatureWriteModelEndpoints"/>, <see cref="MapServerEndpoints"/>
/// and <see cref="ImageServerEndpoints"/>.
/// </summary>
public static partial class GeoServicesEndpoints
{
    /// <summary>Maps the facade at <see cref="GeoServicesOptions.Root"/>.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        GeoServicesOptions options,
        IMapRegistry registry,
        IAuthService? auth = null,
        bool authEnabled = false,
        string? legacyToken = null)
    {
        var catalog = new GeoServicesCatalog(options);
        var group = app.MapGroup(catalog.Root);

        // Spec §2.0.1: a resource is requestable with GET or POST. ArcGIS
        // REST JS (and therefore the Maps SDK) POSTs resource reads, so the
        // catalog, Geometry Server and Feature Server roots accept both.
        group.MapMethods(string.Empty, ["GET", "POST"], (HttpContext context, CancellationToken cancellationToken) =>
            Catalog(catalog, registry, context, cancellationToken));
        GeometryServerEndpoints.MapGeometryServer(group);
        FeatureServerEndpoints.MapFeatureServer(group, catalog, registry);
        FeatureEditEndpoints.MapFeatureEdit(group, catalog, registry, auth, authEnabled, legacyToken);

        // The Feature write-model operations (T-038, ADR-0058): service-level
        // query, per-layer generateRenderer, validateSQL, honest aggregation
        // rejects and the store-backed attachment surface (T-061, ADR-0066).
        // Attachment writes are admin-token-gated (ADR-0065 §3); the token is
        // the host's single admin secret, read from configuration like the
        // Esri admin projection does.
        var adminToken = app.ServiceProvider.GetService<IConfiguration>()?["Spatial:Admin:Token"];
        FeatureWriteModelEndpoints.MapFeatureOps(
            group, catalog, registry, new AttachmentWriteAuthorization(adminToken, auth, authEnabled, legacyToken));

        // The Map Service projection (spec §4, ADR-0048) and the Image
        // Service projection (spec §8, ADR-0051).
        MapServerEndpoints.MapMapServer(group, catalog, registry);
        ImageServerEndpoints.MapImageServer(group, catalog, registry, options);
    }

    private static async Task<IResult> Catalog(GeoServicesCatalog catalog, IMapRegistry registry, HttpContext context, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var services = await BuildServicesAsync(catalog, registry, cancellationToken);
            return EsriJson.Value(new CatalogResponse(10.0, [], services.ToArray()));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The catalogue entries: a deterministic Geometry-first order built from
    /// the registry's maps plus the declared FeatureServer services.
    /// Declared services are also maps (seeded at composition), and a
    /// registry that is not populated (for example in unit tests) is tolerated.
    /// Internal for the T-049 catalog-honesty tests: only served types
    /// (Feature/Map/Image) may appear — Tiles/WMS/WFS and any future
    /// GPServer-shaped service are omitted, never advertised.
    /// </summary>
    internal static async Task<List<EsriServiceEntry>> BuildServicesAsync(
        GeoServicesCatalog catalog, IMapRegistry registry, CancellationToken cancellationToken)
    {
        var services = new List<EsriServiceEntry> { new(GeoServicesCatalog.GeometryServiceName, "GeometryServer") };
        foreach (var map in await registry.ListAsync(cancellationToken))
        {
            foreach (var service in map.Services)
            {
                if (ServerType(service) is { } type)
                {
                    services.Add(new EsriServiceEntry(map.Name, type));
                }
            }
        }

        foreach (var entry in catalog.Services)
        {
            AddDeclared(services, entry);
        }

        return services;
    }

    /// <summary>
    /// The service types this catalog advertises (T-049): the served
    /// <see cref="MapServiceKind"/> members, whose Esri type name is the enum
    /// member name. Tiles/WMS/WFS and any future GPServer-shaped member are
    /// absent, so they are never advertised.
    /// </summary>
    private static readonly FrozenSet<MapServiceKind> ServedTypes =
    [
        MapServiceKind.FeatureServer,
        MapServiceKind.MapServer,
        MapServiceKind.ImageServer,
    ];

    private static string? ServerType(MapServiceKind service) =>
        ServedTypes.Contains(service) ? service.ToString() : null;

    private static void AddDeclared(List<EsriServiceEntry> services, GeoServicesServiceEntry entry)
    {
        if (IsUndeclaredFeatureServer(entry, services))
        {
            services.Add(new EsriServiceEntry(entry.Name, entry.Type));
        }
    }

    private static bool IsUndeclaredFeatureServer(GeoServicesServiceEntry entry, List<EsriServiceEntry> services) =>
        entry.Type == "FeatureServer"
        && !services.Any(service => string.Equals(service.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One layer resolved for serving (id via the publication or the whole-store order).</summary>
internal sealed record PublishedLayer(int Id, string Dataset, string Name, string? Style = null);

/// <summary>A resolved GeoServices server: its store, its explicit layers (null means whole-store),
/// and the map's authored service description, copyright and metadata document (ADR-0068).</summary>
internal sealed record ResolvedService(
    string Store,
    IReadOnlyList<MapLayer>? Layers,
    string? Description = null,
    string? Copyright = null,
    string? MetadataXml = null);

/// <summary>The GeoServices catalog resource (spec §3).</summary>
internal sealed record CatalogResponse(double CurrentVersion, IReadOnlyList<string> Folders, IReadOnlyList<EsriServiceEntry> Services);

/// <summary>One catalog service entry.</summary>
internal sealed record EsriServiceEntry(string Name, string Type);
