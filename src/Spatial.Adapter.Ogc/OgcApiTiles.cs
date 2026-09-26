using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The OGC API Tiles - Part 1 projection (ADR-0070). The map is the
/// collection, and the collection's tile matrix set is served by the shared
/// vector-tile contract. This class owns only REST paths, TileJSON, links,
/// media negotiation and OGC exception mapping.
/// </summary>
internal static class OgcApiTiles
{
    private const string MvtMediaType = "application/vnd.mapbox-vector-tile";
    private const string JsonMediaType = "application/json";
    private const string TileJsonRel = "http://www.opengis.net/def/rel/ogc/1.0/tilejson";
    private const string TilesRel = "http://www.opengis.net/def/rel/ogc/1.0/tiles";

    /// <summary>
    /// The seams every tiles resource needs: the map and the collection it is
    /// addressed as, the registry that resolves it, the OGC base path, the
    /// caller's context and its cancellation. Grouping them keeps each
    /// resource's signature to the one thing it actually varies on.
    /// </summary>
    private sealed record TilesRequest(
        string Name,
        string CollectionId,
        IMapRegistry Registry,
        OgcOptions Options,
        HttpContext Context,
        CancellationToken CancellationToken);

    /// <summary>One tile's place in the matrix set: the matrix set and the three path parts.</summary>
    private sealed record TilePath(string MatrixSetId, string Z, string X, string Y);

    private static TilesRequest Request(
        string name,
        string collectionId,
        HttpContext context,
        IMapRegistry registry,
        OgcOptions options,
        CancellationToken cancellationToken) =>
        new(name, collectionId, registry, options, context, cancellationToken);

    public static void Map(RouteGroupBuilder group, OgcOptions options)
    {
        // The map is the collection: /ogc/{map}/tiles, not a second global
        // tile registry. The OGC root remains configurable with WMS/WFS.
        group.MapGet("/{name}/tiles", (string name, HttpContext context, IMapRegistry registry, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => LandingAsync(Request(name, name, context, registry, options, token)), cancellationToken));
        group.MapGet("/{name}/tiles/collections", (string name, HttpContext context, IMapRegistry registry, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => CollectionsAsync(Request(name, name, context, registry, options, token)), cancellationToken));
        group.MapGet("/{name}/tiles/collections/{collectionId}", (string name, string collectionId, HttpContext context, IMapRegistry registry, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => CollectionAsync(Request(name, collectionId, context, registry, options, token)), cancellationToken));
        group.MapGet("/{name}/tiles/collections/{collectionId}/tiles", (string name, string collectionId, HttpContext context, IMapRegistry registry, OgcVectorTileService tiles, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TilesResourceAsync(Request(name, collectionId, context, registry, options, token), tiles), cancellationToken));

        // TileJSON is a resource of a tile matrix set. The unqualified spelling
        // is useful to clients bootstrapping from the collection landing page;
        // the matrix-set-qualified spelling is the OGC resource path.
        group.MapGet("/{name}/tiles/TileJSON", (string name, HttpContext context, IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TileJsonAsync(Request(name, name, context, registry, options, token), stores, tiles), cancellationToken));
        group.MapGet("/{name}/tiles/collections/{collectionId}/tiles/{matrixSetId}/TileJSON", (string name, string collectionId, string matrixSetId, HttpContext context, IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TileJsonAsync(Request(name, collectionId, context, registry, options, token), stores, tiles, matrixSetId), cancellationToken));
        group.MapGet("/{name}/tiles/{matrixSetId}/TileJSON", (string name, string matrixSetId, HttpContext context, IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TileJsonAsync(Request(name, name, context, registry, options, token), stores, tiles, matrixSetId), cancellationToken));

        MapTileData(group, "pbf");
        MapTileData(group, string.Empty);
    }

    private static void MapTileData(RouteGroupBuilder group, string extension)
    {
        var suffix = extension.Length == 0 ? string.Empty : "." + extension;
        group.MapGet(
            "/{name}/tiles/collections/{collectionId}/tiles/{matrixSetId}/{z:int}/{x:int}/{y:int}" + suffix,
            (string name, string collectionId, string matrixSetId, string z, string x, string y, HttpContext context,
                IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options,
                ILoggerFactory logger, CancellationToken cancellationToken) =>
                Dispatch(context, logger, token => TileDataAsync(
                    Request(name, collectionId, context, registry, options, token), stores, tiles,
                    new TilePath(matrixSetId, z, x, y)), cancellationToken));
    }

    private static Task<IResult> Dispatch(
        HttpContext context, ILoggerFactory logger, Func<CancellationToken, Task<IResult>> handler, CancellationToken cancellationToken) =>
        OgcEndpoints.Dispatch(context, logger, _ => handler(cancellationToken), cancellationToken);

    private static async Task<IResult> LandingAsync(TilesRequest request)
    {
        RequireJson(request.Context);
        var map = await ResolveMapAsync(request);
        var root = Path(request.Options, request.Name, "tiles");
        return Results.Json(new
        {
            title = $"{map.Name} vector tiles",
            description = map.Description,
            links = new[]
            {
                Link(request.Context, "self", JsonMediaType, root),
                Link(request.Context, "http://www.opengis.net/def/rel/ogc/1.0/collections", JsonMediaType, root + "/collections"),
            },
        });
    }

    private static async Task<IResult> CollectionsAsync(TilesRequest request)
    {
        RequireJson(request.Context);
        var map = await ResolveMapAsync(request);
        var root = Path(request.Options, request.Name, "tiles", "collections");
        var collection = Path(request.Options, request.Name, "tiles", "collections", map.Name);
        return Results.Json(new
        {
            links = new[] { Link(request.Context, "self", JsonMediaType, root) },
            collections = new[]
            {
                new
                {
                    id = map.Name,
                    title = map.Name,
                    links = new[] { Link(request.Context, "item", JsonMediaType, collection) },
                },
            },
        });
    }

    private static async Task<IResult> CollectionAsync(TilesRequest request)
    {
        RequireJson(request.Context);
        var map = await ResolveMapAsync(request);
        var root = Path(request.Options, request.Name, "tiles", "collections", map.Name);
        return Results.Json(new
        {
            id = map.Name,
            title = map.Name,
            description = map.Description,
            extent = new { srid = 4326, world = new[] { -180.0, -90.0, 180.0, 90.0 } },
            links = new[]
            {
                Link(request.Context, "self", JsonMediaType, root),
                Link(request.Context, TilesRel, JsonMediaType, root + "/tiles"),
            },
        });
    }

    private static async Task<IResult> TilesResourceAsync(TilesRequest request, OgcVectorTileService tiles)
    {
        RequireJson(request.Context);
        var map = await ResolveMapAsync(request);
        var root = Path(request.Options, request.Name, "tiles", "collections", map.Name, "tiles");
        var matrixSetId = tiles.MatrixSetId(tiles.Resolve(null));
        return Results.Json(new
        {
            links = new[]
            {
                Link(request.Context, "self", JsonMediaType, root),
                Link(request.Context, TileJsonRel, JsonMediaType, root + "/" + matrixSetId + "/TileJSON"),
            },
        });
    }

    private static async Task<IResult> TileJsonAsync(
        TilesRequest request, IStoreRegistry stores, OgcVectorTileService tiles, string? matrixSetId = null)
    {
        RequireJson(request.Context);
        var map = await ResolveMapAsync(request);
        var scheme = tiles.Resolve(matrixSetId);
        var matrixSet = tiles.MatrixSetId(scheme);
        var root = Path(request.Options, request.Name, "tiles", "collections", map.Name, "tiles", matrixSet);
        var vectorLayers = new List<object>();
        foreach (var layer in map.Layers.Where(layer => layer.Kind == MapLayerKind.Feature))
        {
            var store = layer.Store ?? map.Store;
            var description = await stores.Catalogue(store).DescribeAsync(layer.Dataset, request.CancellationToken);
            var fields = description.Schema.Fields
                .Where(field => field.Kind.ToString() != "Geometry")
                .ToDictionary(field => field.Name, field => field.Kind.ToString());
            vectorLayers.Add(new { id = layer.Name ?? layer.Dataset, description = string.Empty, minzoom = scheme.MinZoom, maxzoom = scheme.MaxZoom, fields });
        }

        var bounds = string.Equals(scheme.Crs, "EPSG:3857", StringComparison.OrdinalIgnoreCase)
            ? new[] { -180.0, -85.051129, 180.0, 85.051129 }
            : ToArray(scheme.Bounds(new TileCoordinate(scheme.MinZoom, 0, 0)));
        return Results.Json(new
        {
            tilejson = "2.2.0",
            name = map.Name,
            description = map.Description,
            version = "1.0.0",
            attribution = map.Copyright ?? string.Empty,
            scheme = "xyz",
            tiles = new[] { Absolute(request.Context, root + "/{z}/{x}/{y}.pbf") },
            minzoom = scheme.MinZoom,
            maxzoom = scheme.MaxZoom,
            bounds,
            center = new[] { 0.0, 0.0, (double)scheme.MinZoom },
            vector_layers = vectorLayers,
            links = new[]
            {
                Link(request.Context, "self", JsonMediaType, root + "/TileJSON"),
                Link(request.Context, TilesRel, MvtMediaType, root + "/{z}/{x}/{y}.pbf"),
            },
        });
    }

    private static async Task<IResult> TileDataAsync(
        TilesRequest request, IStoreRegistry stores, OgcVectorTileService tiles, TilePath path)
    {
        RequireMvt(request.Context);
        var zoom = ParseTilePart(path.Z, "z");
        var column = ParseTilePart(path.X, "x");
        var row = ParseTilePart(path.Y, "y");
        var map = await ResolveMapAsync(request);
        var coordinate = new TileCoordinate(zoom, column, row);
        var scheme = tiles.Resolve(path.MatrixSetId);
        if (!scheme.IsValid(coordinate))
        {
            throw SpatialException.BadArguments(
                FormattableString.Invariant($"Tile {zoom}/{column}/{row} is outside the {tiles.MatrixSetId(scheme)} tile matrix set."));
        }

        var layers = map.Layers
            .Where(layer => layer.Kind == MapLayerKind.Feature)
            .Select(layer => new VectorTileLayer(
                layer.Name ?? layer.Dataset,
                layer.Dataset,
                stores.Features(layer.Store ?? map.Store),
                stores.Catalogue(layer.Store ?? map.Store)))
            .ToArray();
        var tile = await tiles.RenderAsync(layers, map, coordinate, path.MatrixSetId, request.CancellationToken);
        return Results.Bytes(tile.Content, tile.MediaType);
    }

    private static async Task<Map> ResolveMapAsync(TilesRequest request)
    {
        if (!string.Equals(Uri.UnescapeDataString(request.Name), Uri.UnescapeDataString(request.CollectionId), StringComparison.Ordinal))
        {
            throw OgcServiceException.NotDefined($"Tiles collection '{request.CollectionId}' was not found.");
        }

        try
        {
            var map = await request.Registry.GetAsync(Uri.UnescapeDataString(request.Name), request.CancellationToken);
            return map.Exposes(MapServiceKind.Tiles)
                ? map
                : throw OgcServiceException.NotDefined($"Tiles service '{request.Name}' was not found.");
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
            throw OgcServiceException.NotDefined($"Tiles service '{request.Name}' was not found.");
        }
    }

    private static int ParseTilePart(string value, string name) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw OgcServiceException.Invalid($"The tile {name} must be an integer, got '{value}'.");

    private static Dictionary<string, string> Link(HttpContext context, string rel, string type, string path) =>
        new()
        {
            ["rel"] = rel,
            ["type"] = type,
            ["href"] = Absolute(context, path),
        };

    private static string Path(OgcOptions options, string name, params string[] rest) =>
        string.Join('/', new[] { options.Root.TrimEnd('/'), Uri.EscapeDataString(Uri.UnescapeDataString(name)) }
            .Concat(rest));

    private static string Absolute(HttpContext context, string path) =>
        $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/{path.TrimStart('/')}";

    /// <summary>The media types that satisfy the JSON content negotiation, wildcards included.</summary>
    private static readonly string[] JsonMediaTypes = ["application/json", "application/geo+json", "*/*"];

    private static void RequireJson(HttpContext context)
    {
        if (!Allows(context.Request.Headers.Accept, IsJsonMediaType))
        {
            throw OgcServiceException.InvalidFormat("The OGC API Tiles resource requires application/json content negotiation.");
        }
    }

    /// <summary>An absent Accept header allows anything; otherwise one entry must match the predicate.</summary>
    private static bool Allows(StringValues accept, Func<string?, bool> isAllowed) =>
        accept.Count == 0 || accept.Any(value => isAllowed(value));

    private static bool IsJsonMediaType(string? value) =>
        JsonMediaTypes.Any(mediaType => value?.Contains(mediaType, StringComparison.OrdinalIgnoreCase) == true);

    private static void RequireMvt(HttpContext context)
    {
        if (context.Request.Headers.Accept.Count != 0 && !context.Request.Headers.Accept.Any(value => value?.Contains(MvtMediaType, StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("application/x-protobuf", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("*/*", StringComparison.Ordinal) == true))
        {
            throw OgcServiceException.InvalidFormat("The OGC vector tile requires application/vnd.mapbox-vector-tile content negotiation.");
        }
    }

    private static double[] ToArray(Envelope envelope) => [envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY];
}
