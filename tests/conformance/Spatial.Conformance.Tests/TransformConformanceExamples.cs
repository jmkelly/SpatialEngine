using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures of the transformation contracts (ADR-0027):
/// the geometry-carrying transform examples ship here, in the conformance
/// suite, rather than embedded in the contract descriptor — the transform
/// descriptor therefore declares no examples and every provider proves itself
/// against this same suite (plan §18). The describe examples are embedded in
/// the contract itself (string-only values) and the suite also runs those.
/// </summary>
public static class TransformConformanceExamples
{
    public static readonly (double Lon, double Lat) Berlin = (13.405, 52.52);

    public static readonly (double X, double Y) BerlinUtm32 = (798812.803, 5827999.900);

    public static IReadOnlyList<ConformanceExample> Transform { get; } =
    [
        new ConformanceExample(
            "berlin-utm32",
            "Transforming Berlin from WGS84 to UTM 32N lands on the reference control point.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(Berlin.Lon, Berlin.Lat, CoordinateReference.Epsg(4326)),
                TransformationArguments.Source, "EPSG:4326",
                TransformationArguments.Target, "EPSG:32632")),

        new ConformanceExample(
            "geometry-crs-source",
            "Omitting 'source' transforms from the geometry's own CRS identity.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(Berlin.Lon, Berlin.Lat, CoordinateReference.Epsg(4326)),
                TransformationArguments.Target, "EPSG:32632")),

        new ConformanceExample(
            "empty-input",
            "Transforming an empty geometry yields an empty result in the target CRS.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreateEmptyPoint(CoordinateReference.Epsg(4326)),
                TransformationArguments.Source, "EPSG:4326",
                TransformationArguments.Target, "EPSG:32632")),

        new ConformanceExample(
            "missing-target",
            "A transform without a target CRS is an invalid input.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(Berlin.Lon, Berlin.Lat, CoordinateReference.Epsg(4326)),
                TransformationArguments.Source, "EPSG:4326")),

        new ConformanceExample(
            "unknown-source-crs",
            "An identity outside the provider's catalogue is an invalid input.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(Berlin.Lon, Berlin.Lat, CoordinateReference.Epsg(4326)),
                TransformationArguments.Source, "EPSG:999999",
                TransformationArguments.Target, "EPSG:32632")),

        new ConformanceExample(
            "unknown-target-crs",
            "An unknown target identity is an invalid input.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(Berlin.Lon, Berlin.Lat, CoordinateReference.Epsg(4326)),
                TransformationArguments.Source, "EPSG:4326",
                TransformationArguments.Target, "EPSG:888888")),

        new ConformanceExample(
            "mismatched-source",
            "A source argument conflicting with the geometry's own CRS is an invalid input.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(Berlin.Lon, Berlin.Lat, CoordinateReference.Epsg(32632)),
                TransformationArguments.Source, "EPSG:4326",
                TransformationArguments.Target, "EPSG:3857")),

        new ConformanceExample(
            "out-of-area",
            "Coordinates outside the target CRS's valid area are an invalid input.",
            Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(0, 95, CoordinateReference.Epsg(4326)),
                TransformationArguments.Source, "EPSG:4326",
                TransformationArguments.Target, "EPSG:3857")),
    ];

    /// <summary>Builds the argument dictionary for one example.</summary>
    public static IReadOnlyDictionary<string, object?> Arguments(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var index = 0; index < pairs.Length; index += 2)
        {
            arguments[(string)pairs[index]!] = pairs[index + 1];
        }

        return arguments;
    }
}
