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
        if (parts.Length != 6
            || string.IsNullOrWhiteSpace(parts[0])
            || string.IsNullOrWhiteSpace(parts[1])
            || string.IsNullOrWhiteSpace(parts[2])
            || !long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var population)
            || !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var latitude)
            || !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var longitude)
            || latitude is < -90 or > 90
            || longitude is < -180 or > 180)
        {
            throw new InvalidOperationException(
                $"The embedded '{ResourceName}' snapshot has a malformed row {lineNumber}; expected 'geonameid, name, country, population, latitude, longitude'.");
        }

        var geometry = GeometryFactory.CreatePoint(longitude, latitude, DemoDatasetCatalog.Wgs84);
        return new Feature(
            new FeatureId($"wd-{parts[0]}"),
            schema,
            [
                AttributeValue.FromString(parts[1]),
                AttributeValue.FromString(parts[2]),
                AttributeValue.FromInt64(population),
                AttributeValue.FromGeometry(geometry),
            ]);
    }
}
