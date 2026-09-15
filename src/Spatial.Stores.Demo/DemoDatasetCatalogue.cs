using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Stores.Demo;

/// <summary>
/// The demo store's in-memory dataset catalogue (ADR-0033): two
/// procedurally generated point datasets plus the GeoNames world-cities
/// snapshot (see <see cref="WorldCities"/>), served through the keyed
/// <c>demo</c> store so the
/// browser workbench can browse, map and select real features without a
/// database. Geometry is produced from <c>Spatial.Core</c> factory types
/// (points at <see cref="Wgs84"/>), stamped with an EPSG:4326 identity like
/// the PostGIS adapter does — the datasets are deliberately small (grids)
/// or committed snapshots (world cities), stable and deterministic so
/// automated tests can assert exact feature counts and shapes.
/// </summary>
internal static class DemoDatasetCatalogue
{
    /// <summary>The WGS 84 identity the demo geometries carry (SRID 4326).</summary>
    internal static readonly CoordinateReference Wgs84 = CoordinateReference.Epsg(4326);

    /// <summary>
    /// A deterministic 11×10 grid of 110 points over x∈[−5,5], y∈[−4,4] with
    /// a name and a value — the workbench's map/selection default.
    /// </summary>
    private static readonly DemoDataset PointsGrid = BuildPointsGrid();

    /// <summary>Eight named points with a population — catalogue pattern filtering practice.</summary>
    private static readonly DemoDataset Cities = BuildCities();

    /// <summary>
    /// The three datasets, ordered by id — the catalogue listing order.
    /// Touching this parses the world-cities snapshot; prefer
    /// <see cref="Summaries"/> for listings and <see cref="Find"/> for single
    /// datasets so cold layer-0 traffic stays off the CSV (T-095).
    /// </summary>
    internal static IReadOnlyList<DemoDataset> Datasets =>
    [
        Cities,
        PointsGrid,
        WorldCities.Dataset,
    ];

    /// <summary>
    /// The catalogue summaries, ordered by id — cheap: the world-cities entry
    /// reports the committed snapshot count without parsing the CSV.
    /// </summary>
    internal static IReadOnlyList<DatasetSummary> Summaries =>
    [
        Cities.ToSummary(),
        PointsGrid.ToSummary(),
        WorldCities.Summary,
    ];

    /// <summary>
    /// The dataset whose id matches, or null when the catalogue has no such dataset.
    /// The world-cities snapshot parses only when its id is requested; every
    /// other lookup stays off the CSV (T-095).
    /// </summary>
    internal static DemoDataset? Find(string id) =>
        string.Equals(id, WorldCities.DatasetId, StringComparison.Ordinal)
            ? WorldCities.Dataset
            : SmallDatasets.FirstOrDefault(dataset => dataset.Id == id);

    private static IReadOnlyList<DemoDataset> SmallDatasets => [Cities, PointsGrid];

    private static DemoDataset BuildPointsGrid()
    {
        var fields = new[]
        {
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("value", AttributeKind.Double),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        };
        var schema = new FeatureSchema(fields);
        var features = new List<Feature>();
        var nameIndex = 0;
        for (var row = 0; row < 10; row++)
        {
            for (var column = 0; column < 11; column++)
            {
                var x = -5.0 + column;
                var y = -4.0 + row;
                var name = $"point-{nameIndex++:D3}";
                var geometry = GeometryFactory.CreatePoint(x, y, Wgs84);
                features.Add(new Feature(
                    new FeatureId(name),
                    schema,
                    [AttributeValue.FromString(name), AttributeValue.FromDouble(x + y), AttributeValue.FromGeometry(geometry)]));
            }
        }

        return new DemoDataset(
            "demo.points",
            "demo",
            "points",
            "geometry",
            4326,
            "Point",
            features,
            new FeatureSchema(fields),
            BoxesFor(features));
    }

    private static DemoDataset BuildCities()
    {
        var fields = new[]
        {
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("population", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        };
        var schema = new FeatureSchema(fields);
        var cities = new (string Name, long Population, double X, double Y)[]
        {
            ("Amsterdam", 900_000, 4.9041, 52.3676),
            ("Berlin", 3_664_000, 13.4050, 52.5200),
            ("London", 8_900_000, -0.1276, 51.5072),
            ("Madrid", 3_300_000, -3.7038, 40.4168),
            ("Oslo", 700_000, 10.7522, 59.9139),
            ("Paris", 2_150_000, 2.3522, 48.8566),
            ("Rome", 2_870_000, 12.4964, 41.9028),
            ("Vienna", 1_900_000, 16.3738, 48.2082),
        };
        var features = cities
            .Select(city => new Feature(
                new FeatureId(city.Name.ToLowerInvariant()),
                schema,
                [
                    AttributeValue.FromString(city.Name),
                    AttributeValue.FromInt64(city.Population),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(city.X, city.Y, Wgs84)),
                ]))
            .ToList();

        return new DemoDataset(
            "demo.cities",
            "demo",
            "cities",
            "geometry",
            4326,
            "Point",
            features,
            new FeatureSchema(fields),
            BoxesFor(features));
    }

    /// <summary>Precomputes each feature's envelope as plain doubles (the only geometry touch in this file).</summary>
    internal static Dictionary<string, DemoBox> BoxesFor(List<Feature> features)
    {
        var boxes = new Dictionary<string, DemoBox>(features.Count);
        foreach (var feature in features)
        {
            for (var i = 0; i < feature.Schema.Count; i++)
            {
                var value = feature[i];
                if (value.Kind != AttributeKind.Geometry || value.GeometryValue.IsEmpty)
                {
                    continue;
                }

                // Mirror DemoFeatureHandler.Intersects: a non-empty geometry always has an envelope.
                if (value.GeometryValue.Envelope is not { } envelope)
                {
                    continue;
                }

                boxes[feature.Id.Value] = new DemoBox(envelope.MinX, envelope.MinY, envelope.MaxX, envelope.MaxY);
                break;
            }
        }

        return boxes;
    }
}

/// <summary>The axis-aligned envelope of one feature, as plain doubles.</summary>
internal readonly record struct DemoBox(double MinX, double MinY, double MaxX, double MaxY);

/// <summary>One immutable demo dataset: identity plus its generated features.</summary>
internal sealed record DemoDataset(
    string Id,
    string Schema,
    string Table,
    string GeometryColumn,
    int Srid,
    string GeometryType,
    IReadOnlyList<Feature> Features,
    FeatureSchema SchemaFields,
    IReadOnlyDictionary<string, DemoBox> Boxes)
{
    /// <summary>The feature's envelope from its geometry attribute, or null when it carries none.</summary>
    public DemoBox? BoxFor(FeatureId id) =>
        Boxes.TryGetValue(id.Value, out var box) ? box : null;

    public DatasetSummary ToSummary() =>
        new(Id, Schema, Table, GeometryColumn, Srid, Features.Count);

    public DatasetDescription ToDescription() =>
        new(
            Id,
            Schema,
            Table,
            GeometryColumn,
            Srid,
            GeometryType,
            Features.Count,
            [SchemaFields.Fields[0].Name],
            SchemaFields);
}
