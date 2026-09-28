using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Shapes Map Service resources (spec §4) from a publication's layers and the
/// engine's dataset metadata. It computes each layer's extent by scanning the
/// store (the engine has no extent verb — ADR-0048 records the follow-up),
/// maps the map SRID to Esri units and projects the persisted style onto a
/// simple <c>drawingInfo</c>. No spatial algorithm lives here; the adapter
/// delegates geometry work to the engine verbs.
/// </summary>
internal static class MapServerResources
{
    public const double CurrentVersion = 10.0;

    /// <summary>The advertised capabilities: export and tiles are served, so Map is included.</summary>
    public const string Capabilities = "Map,Query,Data";

    private const string SupportedImageFormatTypes = "PNG32,PNG24,PNG,JPG";
    private const int MaxImageDimension = 4096;
    private const double TileDpi = 96;

    /// <summary>Describes and extents one published layer (scanning its features).</summary>
    public static async Task<MapLayerInfo> ReadLayerAsync(
        IFeatureStore store, IDataCatalogue catalogue, PublishedLayer layer, CancellationToken cancellationToken)
    {
        var dataset = await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
        var extent = await ExtentAsync(store, layer.Dataset, cancellationToken);
        return new MapLayerInfo(layer, dataset, extent);
    }

    /// <summary>Describes and extents every published layer, in stable id order.</summary>
    public static async Task<IReadOnlyList<MapLayerInfo>> ReadLayersAsync(
        IFeatureStore store, IDataCatalogue catalogue, IReadOnlyList<PublishedLayer> layers, CancellationToken cancellationToken)
    {
        var infos = new List<MapLayerInfo>(layers.Count);
        foreach (var layer in layers.OrderBy(layer => layer.Id))
        {
            cancellationToken.ThrowIfCancellationRequested();
            infos.Add(await ReadLayerAsync(store, catalogue, layer, cancellationToken));
        }

        return infos;
    }

    /// <summary>
    /// Builds the MapServer root (spec §4.0, T-040): the service advertises
    /// exactly what export honours — dynamic layers, and the time window
    /// under the overlaps relation only (<c>supportsTimeRelation:false</c>,
    /// ADR-0100) — and the fused-cache fields follow the served scheme
    /// (tiles are live-rendered per scheme, so <c>exportTilesAllowed</c>
    /// stays false; offline packaging is T-041's scope).
    /// </summary>
    /// <remarks>
    /// A fused-cache root is a tile-matrix document: the spatial reference,
    /// extents and units are served in the tiling SR, exactly as Esri's
    /// cached services do. Tile clients derive indices from these members,
    /// so advertising the data CRS here while <c>tileInfo</c> is 3857 makes
    /// QGIS fetch Null-Island tiles for an Australia canvas (every request
    /// 200, every tile blank ocean). Without a scheme the map SRID stands.
    /// </remarks>
    public static EsriMapServerRoot Root(
        string mapName, IReadOnlyList<MapLayerInfo> layers, ITileScheme? scheme, string? description, string? copyright,
        ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        var mapSrid = MapSrid(layers);
        var tileSrid = scheme is null ? 0 : SridOf(scheme.Crs);
        var (srid, extent) = tileSrid > 0
            ? (tileSrid, TileExtent(layers, tileSrid, scheme!.Crs, transforms, cancellationToken))
            : (mapSrid, FullExtent(layers, mapSrid));
        return new EsriMapServerRoot(
            CurrentVersion,
            "SpatialEngine Map Service",
            mapName,
            description,
            copyright,
            EsriLayerModel.SpatialReference(srid),
            scheme is not null,
            TileInfo(scheme),
            extent,
            extent,
            Units(srid),
            Capabilities,
            SupportedImageFormatTypes,
            [.. layers.Select(Reference)],
            [],
            SupportsDynamicLayers: true,
            SupportsTimeRelation: false,
            ExportTilesAllowed: false);
    }

    /// <summary>Builds the all-layers resource (spec §4.8).</summary>
    public static EsriMapLayersResponse AllLayers(IReadOnlyList<PublishedLayer> layers) =>
        new(CurrentVersion, [.. layers.Select(layer => new EsriMapLayerRef(layer.Id, layer.Name, -1, true, null, 0, 0))], []);

    /// <summary>Builds one layer's metadata (spec §4.2), including its projected <c>drawingInfo</c>, labels and domains.</summary>
    public static EsriMapLayer Layer(MapLayerInfo info)
    {
        var dataset = info.Dataset;
        var drawing = MapStyleProjection.Project(info.Layer.Style, dataset);
        var domains = MapStyleProjection.Domains(drawing, dataset);
        return new EsriMapLayer(
            CurrentVersion,
            info.Layer.Id,
            info.Layer.Name,
            "Feature Layer",
            EsriLayerModel.GeometryType(dataset.GeometryType),
            EsriLayerModel.ObjectIdField,
            DisplayField(dataset),
            EsriLayerModel.Fields(dataset, editable: false, domains),
            EsriLayerModel.ReadOnlyCapabilities,
            EsriLayerModel.MaxRecordCount,
            EsriLayerModel.SpatialReference(dataset.Srid),
            Extent(info.Extent, dataset.Srid),
            drawing,
            -1,
            true,
            false,
            "esriServerHTMLPopupTypeNone",
            domains);
    }

    private static EsriMapLayerRef Reference(MapLayerInfo info) =>
        new(info.Layer.Id, info.Layer.Name, -1, true, null, 0, 0);

    /// <summary>The map CRS: the first layer's SRID, or 4326 when no layer names one.</summary>
    public static int MapSrid(IReadOnlyList<MapLayerInfo> layers)
    {
        foreach (var layer in layers)
        {
            if (layer.Dataset.Srid > 0)
            {
                return layer.Dataset.Srid;
            }
        }

        return 4326;
    }

    private static EsriExtent? FullExtent(IReadOnlyList<MapLayerInfo> layers, int mapSrid)
    {
        var union = Envelope.Empty;
        foreach (var layer in layers)
        {
            if (layer.Dataset.Srid == mapSrid || layer.Dataset.Srid <= 0)
            {
                union = union.Union(layer.Extent);
            }
        }

        return Extent(union, mapSrid);
    }

    /// <summary>
    /// The fused-cache service extent: every layer's extent reprojected into
    /// the tiling SR and unioned, so the advertised full extent names the
    /// same geography the tiles render.
    /// </summary>
    private static EsriExtent? TileExtent(
        IReadOnlyList<MapLayerInfo> layers, int tileSrid, string tileCrs,
        ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        var union = Envelope.Empty;
        foreach (var layer in layers)
        {
            if (layer.Extent.IsEmpty)
            {
                continue;
            }

            var projected = layer.Dataset.Srid == tileSrid || layer.Dataset.Srid <= 0
                ? layer.Extent
                : ProjectExtent(layer.Extent, layer.Dataset.Srid, tileSrid, tileCrs, transforms, cancellationToken);
            union = union.Union(projected);
        }

        return Extent(union, tileSrid);
    }

    /// <summary>The Web-Mercator valid latitude range (spec §4.0 tiling).</summary>
    private const double WebMercatorMaxLatitude = 85.05112878;

    /// <summary>Geographic SRIDs whose ordinates are degrees (the <see cref="Units"/> set).</summary>
    private static readonly HashSet<int> GeographicSrids = [4326, 4258, 4269, 4277, 4171];

    /// <summary>
    /// Reprojects an envelope by transforming its ring. A geographic source
    /// reprojected to Web-Mercator is clamped to validity first, so a world
    /// extent (Antarctica reaches -90) projects instead of failing.
    /// </summary>
    private static Envelope ProjectExtent(
        Envelope extent, int sourceSrid, int targetSrid, string targetCrs,
        ICoordinateTransforms transforms, CancellationToken cancellationToken)
    {
        var source = CoordinateReference.Epsg(sourceSrid);
        var ring = new[]
        {
            new Coordinate(extent.MinX, ClampLatitude(extent.MinY, sourceSrid, targetSrid)),
            new Coordinate(extent.MinX, ClampLatitude(extent.MaxY, sourceSrid, targetSrid)),
            new Coordinate(extent.MaxX, ClampLatitude(extent.MaxY, sourceSrid, targetSrid)),
            new Coordinate(extent.MaxX, ClampLatitude(extent.MinY, sourceSrid, targetSrid)),
            new Coordinate(extent.MinX, ClampLatitude(extent.MinY, sourceSrid, targetSrid)),
        };
        var polygon = GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString(ring, source), null, source);
        return transforms.Transform(polygon, source.ToString(), targetCrs, cancellationToken).Envelope ?? Envelope.Empty;
    }

    private static double ClampLatitude(double y, int sourceSrid, int targetSrid) =>
        targetSrid == 3857 && GeographicSrids.Contains(sourceSrid)
            ? Math.Clamp(y, -WebMercatorMaxLatitude, WebMercatorMaxLatitude)
            : y;

    private static EsriExtent? Extent(Envelope extent, int srid) =>
        extent.IsEmpty ? null : new EsriExtent(extent.MinX, extent.MinY, extent.MaxX, extent.MaxY, EsriLayerModel.SpatialReference(srid));

    private static EsriTileInfo? TileInfo(ITileScheme? scheme)
    {
        if (scheme is null)
        {
            return null;
        }

        var origin = scheme.Bounds(new TileCoordinate(0, 0, 0));
        return new EsriTileInfo(
            scheme.TileSize,
            scheme.TileSize,
            TileDpi,
            new EsriPoint(origin.MinX, origin.MaxY),
            EsriLayerModel.SpatialReference(SridOf(scheme.Crs)),
            [.. scheme.Levels.Select(level => new EsriLod(level.Zoom, level.Resolution, level.ScaleDenominator))]);
    }

    /// <summary>Parses an <c>EPSG:&lt;code&gt;</c> identity to its numeric SRID, or 0.</summary>
    public static int SridOf(string crs)
    {
        var colon = crs.IndexOf(':');
        return colon >= 0
            && int.TryParse(crs[(colon + 1)..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var srid)
                ? srid
                : 0;
    }

    private static string Units(int srid) => srid switch
    {
        4326 or 4258 or 4269 or 4277 or 4171 => "esriDecimalDegrees",
        3857 or 32610 or 32612 or 32632 or 32633 or 25832 or 25833 or 26910 or 27700 or 2154 => "esriMeters",
        _ => "esriUnknownUnits",
    };

    private static async Task<Envelope> ExtentAsync(IFeatureStore store, string dataset, CancellationToken cancellationToken)
    {
        var extent = Envelope.Empty;
        var batches = await store.ScanAsync(dataset, cancellationToken);
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FeatureGeometry.Find(feature)?.Envelope is { } envelope)
            {
                extent = extent.Union(envelope);
            }
        }

        return extent;
    }

    private static string DisplayField(DatasetDescription dataset)
    {
        foreach (var field in dataset.Schema.Fields)
        {
            if (field.Kind == AttributeKind.String)
            {
                return field.Name;
            }
        }

        return string.Empty;
    }
}

/// <summary>One layer described for serving: its published identity, dataset metadata and computed extent.</summary>
internal sealed record MapLayerInfo(PublishedLayer Layer, DatasetDescription Dataset, Envelope Extent);
