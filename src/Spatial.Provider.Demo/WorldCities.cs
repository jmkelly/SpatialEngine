using System.Globalization;
using System.Reflection;
using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Demo;

/// <summary>
/// The GeoNames world-cities dataset (<c>cities15000</c>, CC-BY 4.0 — see
/// <c>Data/world-cities-attribution.txt</c>): every city and town with a
/// population of roughly fifteen thousand or more, as a name, an ISO
/// country code and a population over an EPSG:4326 point. The committed
/// <c>Data/world-cities.tsv</c> snapshot is the source of truth so demos
/// and tests stay deterministic without network access; it is parsed once
/// and held for the host's lifetime.
/// </summary>
internal static class WorldCities
{
    /// <summary>The catalogue id of the world-cities dataset.</summary>
    internal const string DatasetId = "demo.world_cities";

    /// <summary>The rows in the committed snapshot — the scan test asserts this exact count.</summary>
    internal const int ExpectedCount = 34135;

    private const string ResourceName = "world-cities.tsv";

    private static readonly Lazy<DemoDataset> LazyDataset = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Whether the snapshot has parsed yet — the cold-start tests observe this.</summary>
    internal static bool IsLoaded => LazyDataset.IsValueCreated;

    /// <summary>
    /// The catalogue summary without parsing the snapshot: the committed row
    /// count is the source of truth (the loader asserts it), so cold List
    /// calls — including the Esri layer-id resolution behind every
    /// FeatureServer query — stay off the CSV until demo.world_cities is
    /// actually described, scanned or queried.
    /// </summary>
    internal static DatasetSummary Summary { get; } = new(DatasetId, "demo", "world_cities", "geometry", 4326, ExpectedCount);

    /// <summary>The parsed world-cities dataset, loaded once from the embedded snapshot.</summary>
    internal static DemoDataset Dataset => LazyDataset.Value;

    /// <summary>
    /// Builds the dataset from <c>geonameid, name, country, population,
    /// latitude, longitude</c> tab-separated rows. Blank lines are skipped;
    /// any other malformed row fails fast with its line number.
    /// </summary>
    internal static DemoDataset BuildFromReader(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var fields = new[]
        {
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("country", AttributeKind.String),
            new FieldDefinition("population", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        };
        var schema = new FeatureSchema(fields);
        var features = new List<Feature>(ExpectedCount);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            features.Add(ParseRow(line, lineNumber, schema));
        }

        return new DemoDataset(
            DatasetId,
            "demo",
            "world_cities",
            "geometry",
            4326,
            "Point",
            features,
            schema,
            DemoDatasetCatalog.BoxesFor(features));
    }

    private static DemoDataset Load()
    {
        var assembly = typeof(WorldCities).Assembly;
        var qualified = assembly.GetManifestResourceNames().FirstOrDefault(name => name.EndsWith(ResourceName, StringComparison.Ordinal));
        if (qualified is null)
        {
            throw new InvalidOperationException($"The embedded '{ResourceName}' snapshot is missing from {assembly.GetName().Name}.");
        }

        using var stream = assembly.GetManifestResourceStream(qualified);
        if (stream is null)
        {
            throw new InvalidOperationException($"The embedded '{ResourceName}' snapshot could not be opened.");
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        var dataset = BuildFromReader(reader);
        if (dataset.Features.Count != ExpectedCount)
        {
            throw new InvalidOperationException(
                $"The embedded '{ResourceName}' snapshot holds {dataset.Features.Count} cities, expected {ExpectedCount}.");
        }

        return dataset;
    }

    private static Feature ParseRow(string line, int lineNumber, FeatureSchema schema)
    {
        var parts = line.Split('\t');
        if (!TryReadRow(parts, out var row))
        {
            throw new InvalidOperationException(
                $"The embedded '{ResourceName}' snapshot has a malformed row {lineNumber}; expected 'geonameid, name, country, population, latitude, longitude'.");
        }

        var geometry = GeometryFactory.CreatePoint(row.Longitude, row.Latitude, DemoDatasetCatalog.Wgs84);
        return new Feature(
            new FeatureId($"wd-{row.Id}"),
            schema,
            [
                AttributeValue.FromString(row.Name),
                AttributeValue.FromString(row.Country),
                AttributeValue.FromInt64(row.Population),
                AttributeValue.FromGeometry(geometry),
            ]);
    }

    private static bool TryReadRow(string[] parts, out CityRow row)
    {
        row = default;
        if (parts.Length != 6 || !HasRequiredText(parts))
        {
            return false;
        }

        if (!long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var population)
            || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude))
        {
            return false;
        }

        if (!InRange(latitude, -90, 90) || !InRange(longitude, -180, 180))
        {
            return false;
        }

        row = new CityRow(parts[0], parts[1], parts[2], population, latitude, longitude);
        return true;
    }

    private static bool HasRequiredText(string[] parts) =>
        !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]) && !string.IsNullOrWhiteSpace(parts[2]);

    private static bool InRange(double value, double minimum, double maximum) => value >= minimum && value <= maximum;

    private readonly record struct CityRow(string Id, string Name, string Country, long Population, double Latitude, double Longitude);
}
