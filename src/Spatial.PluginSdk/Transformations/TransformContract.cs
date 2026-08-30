using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Transformations;

/// <summary>
/// The <c>spatial.coordinate.transform@1</c> capability contract (ADR-0009,
/// plan §16 Phase 7, ADR-0027): transforms a geometry between two coordinate
/// reference systems. Input is <c>geometry.transform</c> — a geometry, an
/// optional source CRS identity (defaulting to the geometry's own CRS) and a
/// required target CRS identity — and the output is the transformed
/// <c>geometry</c> stamped with the target CRS.
///
/// The engine's coordinate convention is x-first for every CRS: x is the
/// first axis (longitude for geographic, easting for projected) and y the
/// second (latitude / northing). The adapter maps to and from this
/// convention regardless of a CRS's declared axis order; the provider's
/// CrsDescriptions report the native order.
///
/// The shared conformance fixtures of this contract ship with the conformance
/// suite (tests/conformance) rather than embedded in the descriptor, because
/// their geometry-valued inputs would put the CRS description and
/// transformation contracts' examples on the SDK's geometry surface; the
/// declaration cost is documented in ADR-0027 §conformance fixtures.
/// </summary>
public static class TransformContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.coordinate.transform@1");

    /// <summary>The input interchange shape: a geometry plus CRS identities.</summary>
    public const string InputSchema = "geometry.transform";

    /// <summary>The output interchange shape: the transformed geometry.</summary>
    public const string OutputSchema = "geometry";

    /// <summary>The full descriptor a conforming provider registers.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Transforms a geometry from a source to a target coordinate reference system (x-axis first convention).",
        new SchemaDescriptor(
            InputSchema,
            "A geometry (canonical $geometry interchange), plus the source CRS identity (defaults to the geometry's own CRS) and the required target CRS identity."),
        new SchemaDescriptor(OutputSchema, "The transformed geometry, stamped with the target CRS."),
        [new ErrorVariant("invalid.arguments", "A CRS identity or geometry the provider cannot process, or a mismatch between them.")],
        [],
        CapabilityTraits.Cancellable,
        []);
}
