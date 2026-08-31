using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Demo;

/// <summary>
/// The demo provider's in-memory dataset catalog (Phase 10, ADR-0031): two
/// procedurally generated point datasets served by <c>demo@1</c> so the
/// browser workbench can browse, map and select real features without a
/// database. Geometry is produced from <c>Spatial.Core</c> factory types
/// (points at <see cref="Wgs84"/>), stamped with an EPSG:4326 identity like
/// the PostGIS adapter does — the datasets are deliberately small, stable
/// and deterministic so automated tests can assert exact feature counts and
/// shapes.
/// </summary>
internal static class DemoDatasetCatalog
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

    /// <summary>The two datasets, ordered by id — the catalogue stream order.</summary>
    internal static readonly IReadOnlyList<DemoDataset> Datasets =
    [
        PointsGrid,
        Cities,
    ];

    /// <summary>The dataset whose id matches, or null when the catalog has no such dataset.</summary>
    internal static DemoDataset? Find(string id) =>
        Datasets.FirstOrDefault(dataset => dataset.Id == id);

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
            new FeatureSchema(fields));
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
            new FeatureSchema(fields));
    }
}

/// <summary>One immutable demo dataset: identity plus its generated features.</summary>
internal sealed record DemoDataset(
    string Id,
    string Schema,
    string Table,
    string GeometryColumn,
    int Srid,
    string GeometryType,
    IReadOnlyList<Feature> Features,
    FeatureSchema SchemaFields)
{
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
