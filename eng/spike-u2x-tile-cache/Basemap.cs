using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.Memory;

namespace Spatial.Spike.TileCache;

/// <summary>One published layer of the spike's map: its dataset, geometry family and the style it is saved with.</summary>
internal sealed record LayerSpec(
    string Dataset,
    GeometryFamily Family,
    string Style,
    int PerTile);

/// <summary>
/// A realistically published multi-layer map, generated into a memory store
/// through the store's own create/write faces — the same two calls
/// <c>POST /api/datasets</c> and <c>POST /api/features/write</c> make, so the
/// datasets carry real content versions and a real write moves one (ADR-0083).
///
/// The shape is a city basemap: two dense polygon layers (land use, buildings),
/// two dense line layers (roads, waterways) and one sparse point layer (places).
/// That mix is the point of the measurement. The dense layers put a feature in
/// nearly every tile, so a single-layer edit genuinely changes the pixels of
/// most tiles — the best case for per-layer composition. The sparse layer puts
/// features in a few tiles, so a single edit to it genuinely changes one tile —
/// the case where the whole-map flush throws away the most for the least
/// reason. A design that only wins the second case is not worth building.
/// </summary>
internal static class Basemap
{
    /// <summary>The metro extent the map covers (a Berlin-sized city, 13.2–13.55 E, 52.42–52.62 N).</summary>
    public const double MinLon = 13.20;

    public const double MaxLon = 13.55;

    public const double MinLat = 52.42;

    public const double MaxLat = 52.62;

    private static readonly FeatureSchema Points = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("class", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private const string Fill =
        """[{"type":"fill","layout":{"visibility":"visible"},"paint":{"fill-color":"#8bc34a","fill-opacity":0.5,"fill-outline-color":"#558b2f"}}]""";

    private const string Buildings =
        """[{"type":"fill","layout":{"visibility":"visible"},"paint":{"fill-color":"#bdbdbd","fill-opacity":0.9,"fill-outline-color":"#8d8d8d"}}]""";

    private const string Roads =
        """[{"type":"line","layout":{"visibility":"visible"},"paint":{"line-color":"#ffffff","line-width":2,"line-opacity":1.0}}]""";

    private const string Water =
        """[{"type":"line","layout":{"visibility":"visible"},"paint":{"line-color":"#4fc3f7","line-width":3,"line-opacity":0.9}}]""";

    private const string Places =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff5722","circle-radius":5,"circle-opacity":0.95,"circle-stroke-color":"#ffffff","circle-stroke-width":1}}]""";

    /// <summary>
    /// The published layers, in draw order (bottom to top), with the number of
    /// features each puts in one tile. The per-tile counts are what a dense
    /// city basemap carries at the zoom the spike renders; they are what make
    /// the re-render cost a real render rather than an empty frame.
    /// </summary>
    public static IReadOnlyList<LayerSpec> Layers(int scale) =>
    [
        new("basemap.landuse", GeometryFamily.Polygon, Fill, 120 * scale),
        new("basemap.buildings", GeometryFamily.Polygon, Buildings, 400 * scale),
        new("basemap.roads", GeometryFamily.LineString, Roads, 400 * scale),
        new("basemap.waterways", GeometryFamily.LineString, Water, 40 * scale),
        new("basemap.places", GeometryFamily.Point, Places, 30 * scale),
    ];

    /// <summary>
    /// The restyled variant of a layer, for the single-layer style save. Each
    /// layer gets a visibly different colour, so a restyled tile's bytes really
    /// differ and the "did the pixels change" comparison is meaningful.
    /// </summary>
    public static string Restyle(LayerSpec layer) => layer.Dataset switch
    {
        "basemap.landuse" => layer.Style.Replace("#8bc34a", "#ef6c00", StringComparison.Ordinal),
        "basemap.buildings" => layer.Style.Replace("#bdbdbd", "#6a1b9a", StringComparison.Ordinal),
        "basemap.roads" => layer.Style.Replace("#ffffff", "#ffeb3b", StringComparison.Ordinal),
        "basemap.waterways" => layer.Style.Replace("#4fc3f7", "#00e5ff", StringComparison.Ordinal),
        _ => layer.Style.Replace("#ff5722", "#1565c0", StringComparison.Ordinal),
    };

    /// <summary>
    /// Fills the store with <paramref name="tiles"/> tiles' worth of each
    /// layer, spread evenly over the metro extent, so a tile at any zoom holds
    /// a representative share of the map.
    /// </summary>
    public static async Task<IReadOnlyList<LayerSpec>> PublishAsync(
        MemoryStore store, int tiles, int scale, CancellationToken cancellationToken)
    {
        var layers = Layers(scale);
        var random = new Random(20260928);
        foreach (var layer in layers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var features = Generate(layer, tiles, random);
            var schema = Schema(layer.Family);
            var sample = new FeatureBatch(schema, [features[0]]);
            await store.CreateAsync(layer.Dataset, sample, 4326, cancellationToken).ConfigureAwait(false);
            await store.WriteAsync(layer.Dataset, new FeatureBatch(schema, features), null, cancellationToken).ConfigureAwait(false);
        }

        return layers;
    }

    /// <summary>The schema a generated layer's features carry.</summary>
    public static FeatureSchema Schema(GeometryFamily family) => family == GeometryFamily.Point
        ? Points
        : new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);

    /// <summary>Generates the features for one layer, given the tile count the map is viewed at.</summary>
    private static List<Feature> Generate(LayerSpec layer, int tiles, Random random)
    {
        var schema = Schema(layer.Family);
        var count = Math.Max(1, layer.PerTile * tiles);
        var features = new List<Feature>(count);
        for (var i = 0; i < count; i++)
        {
            var lon = MinLon + (random.NextDouble() * (MaxLon - MinLon));
            var lat = MinLat + (random.NextDouble() * (MaxLat - MinLat));
            var geometry = layer.Family switch
            {
                GeometryFamily.Point => (IGeometry)GeometryFactory.CreatePoint(lon, lat, CoordinateReference.Epsg(4326)),
                GeometryFamily.LineString => Segment(lon, lat, random),
                _ => Block(lon, lat, random),
            };
            features.Add(new Feature(
                FeatureId.Unassigned,
                schema,
                layer.Family == GeometryFamily.Point
                    ? [AttributeValue.FromString($"{layer.Dataset}-{i}"), AttributeValue.FromString("place"), AttributeValue.FromGeometry(geometry)]
                    : [AttributeValue.FromString($"{layer.Dataset}-{i}"), AttributeValue.FromGeometry(geometry)]));
        }

        return features;
    }

    /// <summary>A short two-vertex segment, a road or watercourse fragment.</summary>
    private static LineString Segment(double lon, double lat, Random random)
    {
        var length = 0.002 + (random.NextDouble() * 0.006);
        var bearing = random.NextDouble() * Math.PI * 2;
        return GeometryFactory.CreateLineString(
            [Point(lon, lat), Point(lon + (length * Math.Cos(bearing)), lat + (length * Math.Sin(bearing)))],
            CoordinateReference.Epsg(4326));
    }

    /// <summary>A small quad, a building or a land-use parcel.</summary>
    private static Polygon Block(double lon, double lat, Random random)
    {
        var size = 0.0008 + (random.NextDouble() * 0.0016);
        var ring = new[]
        {
            Point(lon, lat),
            Point(lon + size, lat),
            Point(lon + size, lat + size),
            Point(lon, lat + size),
            Point(lon, lat),
        };
        return GeometryFactory.CreatePolygon(ring, CoordinateReference.Epsg(4326));
    }

    private static Coordinate Point(double lon, double lat) => new(lon, lat);

    /// <summary>
    /// A geometry of <paramref name="family"/>'s own kind, anchored at the
    /// supplied point. The invalidation measurement adds one feature to one
    /// layer and needs that feature to actually draw: a point written into a
    /// polygon or line layer is filtered out by the renderer, which would make
    /// the edit a no-op and the measured fan-out meaningless.
    /// </summary>
    public static IGeometry GeometryFor(GeometryFamily family, Point at, Random random) => family switch
    {
        GeometryFamily.Point => at,
        GeometryFamily.LineString => Segment(at.X!.Value, at.Y!.Value, random),
        _ => Block(at.X!.Value, at.Y!.Value, random),
    };
}

/// <summary>The geometry family a generated layer draws as.</summary>
internal enum GeometryFamily
{
    Point,
    LineString,
    Polygon,
}
