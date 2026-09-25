using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
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

    public static void Map(RouteGroupBuilder group, OgcOptions options)
    {
        // The map is the collection: /ogc/{map}/tiles, not a second global
        // tile registry. The OGC root remains configurable with WMS/WFS.
        group.MapGet("/{name}/tiles", (string name, HttpContext context, IMapRegistry registry, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => LandingAsync(name, registry, options, context, token), cancellationToken));
        group.MapGet("/{name}/tiles/collections", (string name, HttpContext context, IMapRegistry registry, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => CollectionsAsync(name, registry, options, context, token), cancellationToken));
        group.MapGet("/{name}/tiles/collections/{collectionId}", (string name, string collectionId, HttpContext context, IMapRegistry registry, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => CollectionAsync(name, collectionId, registry, options, context, token), cancellationToken));
        group.MapGet("/{name}/tiles/collections/{collectionId}/tiles", (string name, string collectionId, HttpContext context, IMapRegistry registry, OgcVectorTileService tiles, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TilesResourceAsync(name, collectionId, registry, tiles, options, context, token), cancellationToken));

        // TileJSON is a resource of a tile matrix set. The unqualified spelling
        // is useful to clients bootstrapping from the collection landing page;
        // the matrix-set-qualified spelling is the OGC resource path.
        group.MapGet("/{name}/tiles/TileJSON", (string name, HttpContext context, IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TileJsonAsync(name, name, registry, stores, tiles, options, context, token), cancellationToken));
        group.MapGet("/{name}/tiles/collections/{collectionId}/tiles/{matrixSetId}/TileJSON", (string name, string collectionId, string matrixSetId, HttpContext context, IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TileJsonAsync(name, collectionId, registry, stores, tiles, options, context, token, matrixSetId), cancellationToken));
        group.MapGet("/{name}/tiles/{matrixSetId}/TileJSON", (string name, string matrixSetId, HttpContext context, IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options, ILoggerFactory logger, CancellationToken cancellationToken) =>
            Dispatch(context, logger, token => TileJsonAsync(name, name, registry, stores, tiles, options, context, token, matrixSetId), cancellationToken));

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
                Dispatch(context, logger, token => TileDataAsync(name, collectionId, matrixSetId, z, x, y, registry, stores, tiles, options, context, token), cancellationToken));
    }

    private static Task<IResult> Dispatch(
        HttpContext context, ILoggerFactory logger, Func<CancellationToken, Task<IResult>> handler, CancellationToken cancellationToken) =>
        OgcEndpoints.Dispatch(context, logger, _ => handler(cancellationToken), cancellationToken);

    private static async Task<IResult> LandingAsync(
        string name, IMapRegistry registry, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        RequireJson(context);
        var map = await ResolveMapAsync(name, name, registry, cancellationToken);
        var root = Path(options, name, "tiles");
        return Results.Json(new
        {
            title = $"{map.Name} vector tiles",
            description = map.Description,
            links = new[]
            {
                Link(context, "self", JsonMediaType, root),
                Link(context, "http://www.opengis.net/def/rel/ogc/1.0/collections", JsonMediaType, root + "/collections"),
            },
        });
    }

    private static async Task<IResult> CollectionsAsync(
        string name, IMapRegistry registry, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        RequireJson(context);
        var map = await ResolveMapAsync(name, name, registry, cancellationToken);
        var root = Path(options, name, "tiles", "collections");
        var collection = Path(options, name, "tiles", "collections", map.Name);
        return Results.Json(new
        {
            links = new[] { Link(context, "self", JsonMediaType, root) },
            collections = new[]
            {
                new
                {
                    id = map.Name,
                    title = map.Name,
                    links = new[] { Link(context, "item", JsonMediaType, collection) },
                },
            },
        });
    }

    private static async Task<IResult> CollectionAsync(
        string name, string collectionId, IMapRegistry registry, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        RequireJson(context);
        var map = await ResolveMapAsync(name, collectionId, registry, cancellationToken);
        var root = Path(options, name, "tiles", "collections", map.Name);
        return Results.Json(new
        {
            id = map.Name,
            title = map.Name,
            description = map.Description,
            extent = new { srid = 4326, world = new[] { -180.0, -90.0, 180.0, 90.0 } },
            links = new[]
            {
                Link(context, "self", JsonMediaType, root),
                Link(context, TilesRel, JsonMediaType, root + "/tiles"),
            },
        });
    }

    private static async Task<IResult> TilesResourceAsync(
        string name, string collectionId, IMapRegistry registry, OgcVectorTileService tiles, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        RequireJson(context);
        var map = await ResolveMapAsync(name, collectionId, registry, cancellationToken);
        var root = Path(options, name, "tiles", "collections", map.Name, "tiles");
        var matrixSetId = tiles.MatrixSetId(tiles.Resolve(null));
        return Results.Json(new
        {
            links = new[]
            {
                Link(context, "self", JsonMediaType, root),
                Link(context, TileJsonRel, JsonMediaType, root + "/" + matrixSetId + "/TileJSON"),
            },
        });
    }

    private static async Task<IResult> TileJsonAsync(
        string name, string collectionId, IMapRegistry registry, IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options,
        HttpContext context, CancellationToken cancellationToken, string? matrixSetId = null)
    {
        RequireJson(context);
        var map = await ResolveMapAsync(name, collectionId, registry, cancellationToken);
        var scheme = tiles.Resolve(matrixSetId);
        var matrixSet = tiles.MatrixSetId(scheme);
        var root = Path(options, name, "tiles", "collections", map.Name, "tiles", matrixSet);
        var vectorLayers = new List<object>();
        foreach (var layer in map.Layers.Where(layer => layer.Kind == MapLayerKind.Feature))
        {
            var store = layer.Store ?? map.Store;
            var description = await stores.Catalogue(store).DescribeAsync(layer.Dataset, cancellationToken);
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
            tiles = new[] { Absolute(context, root + "/{z}/{x}/{y}.pbf") },
            minzoom = scheme.MinZoom,
            maxzoom = scheme.MaxZoom,
            bounds,
            center = new[] { 0.0, 0.0, (double)scheme.MinZoom },
            vector_layers = vectorLayers,
            links = new[]
            {
                Link(context, "self", JsonMediaType, root + "/TileJSON"),
                Link(context, TilesRel, MvtMediaType, root + "/{z}/{x}/{y}.pbf"),
            },
        });
    }

    private static async Task<IResult> TileDataAsync(
        string name, string collectionId, string matrixSetId, string z, string x, string y, IMapRegistry registry,
        IStoreRegistry stores, OgcVectorTileService tiles, OgcOptions options, HttpContext context, CancellationToken cancellationToken)
    {
        RequireMvt(context);
        var zoom = ParseTilePart(z, "z");
        var column = ParseTilePart(x, "x");
        var row = ParseTilePart(y, "y");
        var map = await ResolveMapAsync(name, collectionId, registry, cancellationToken);
        var coordinate = new TileCoordinate(zoom, column, row);
        var scheme = tiles.Resolve(matrixSetId);
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
        var tile = await tiles.RenderAsync(layers, map, coordinate, matrixSetId, cancellationToken);
        return Results.Bytes(tile.Content, tile.MediaType);
    }

    private static async Task<Map> ResolveMapAsync(string name, string collectionId, IMapRegistry registry, CancellationToken cancellationToken)
    {
        if (!string.Equals(Uri.UnescapeDataString(name), Uri.UnescapeDataString(collectionId), StringComparison.Ordinal))
        {
            throw OgcServiceException.NotDefined($"Tiles collection '{collectionId}' was not found.");
        }

        try
        {
            var map = await registry.GetAsync(Uri.UnescapeDataString(name), cancellationToken);
            return map.Exposes(MapServiceKind.Tiles)
                ? map
                : throw OgcServiceException.NotDefined($"Tiles service '{name}' was not found.");
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
            throw OgcServiceException.NotDefined($"Tiles service '{name}' was not found.");
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

    private static void RequireJson(HttpContext context)
    {
        if (context.Request.Headers.Accept.Count != 0 && !context.Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("application/geo+json", StringComparison.OrdinalIgnoreCase) == true
            || value?.Contains("*/*", StringComparison.Ordinal) == true))
        {
            throw OgcServiceException.InvalidFormat("The OGC API Tiles resource requires application/json content negotiation.");
        }
    }

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
