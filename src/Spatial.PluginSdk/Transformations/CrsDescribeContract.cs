using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Transformations;

/// <summary>
/// The <c>spatial.crs.describe@1</c> capability contract (plan §16 Phase 7,
/// ADR-0027): describes one coordinate reference system from a provider's
/// catalogue — name, kind, axis order and units, datum and ellipsoid. Input
/// is <c>crs.identity</c> — a CRS identity string such as <c>EPSG:4326</c> —
/// and the output is the structured <c>crs.description</c>
/// (<see cref="CrsDescription"/>, a <c>$crs</c> wire value). The engine's
/// geometry convention is x-first regardless of a CRS's declared axis order;
/// describe reports that order so clients know the engine's interpretation.
/// The shared conformance examples are embedded here because they carry only
/// strings (no geometry types).
/// </summary>
public static class CrsDescribeContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.crs.describe@1");

    /// <summary>The input interchange shape: a CRS identity string.</summary>
    public const string InputSchema = "crs.identity";

    /// <summary>The output interchange shape: a structured CRS description.</summary>
    public const string OutputSchema = "crs.description";

    /// <summary>
    /// The full descriptor a conforming provider registers, with the shared
    /// conformance examples (string-valued only — the examples of the
    /// geometry-carrying transform contract ship with the conformance suite,
    /// ADR-0027 §shipping the shared fixtures).
    /// </summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Describes a coordinate reference system: name, kind, axis order, units, datum and ellipsoid.",
        new SchemaDescriptor(InputSchema, "A CRS identity string (authority:code, for example EPSG:4326)."),
        new SchemaDescriptor(OutputSchema, "The structured CRS description."),
        [new ErrorVariant("invalid.arguments", "The CRS identity is missing, malformed or unknown to the provider's catalogue.")],
        [],
        CapabilityTraits.Cancellable,
        [
            new ConformanceExample(
                "wgs84",
                "Describing EPSG:4326 yields the WGS 84 geographic CRS with lon/lat axes.",
                Arguments(TransformationArguments.Crs, "EPSG:4326")),
            new ConformanceExample(
                "british-national-grid",
                "Describing EPSG:27700 yields the projected OSGB36 / British National Grid.",
                Arguments(TransformationArguments.Crs, "EPSG:27700")),
            new ConformanceExample(
                "unknown-crs",
                "An identity outside the provider's catalogue is an invalid argument.",
                Arguments(TransformationArguments.Crs, "EPSG:999999")),
            new ConformanceExample(
                "missing-crs",
                "A describe without a CRS identity is an invalid argument.",
                Arguments()),
        ]);

    /// <summary>Builds the argument dictionary for one example.</summary>
    private static Dictionary<string, object?> Arguments(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            arguments[(string)pairs[i]!] = pairs[i + 1];
        }

        return arguments;
    }
}
