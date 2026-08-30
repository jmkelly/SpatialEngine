using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Operations;

/// <summary>
/// The <c>spatial.geometry.intersection@1</c> capability contract (plan §16
/// Phase 6, ADR-0026): the set-theoretic intersection of two geometries.
/// Input is <c>geometry.pair</c> — two geometries (callers intersect
/// geometries in the same coordinate reference; the left geometry's CRS is
/// carried onto the result) — and the output is the intersecting
/// <c>geometry</c>, empty when the inputs do not overlap.
/// </summary>
public static class IntersectionContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.geometry.intersection@1");

    /// <summary>The input interchange shape: two geometries.</summary>
    public const string InputSchema = "geometry.pair";

    /// <summary>The output interchange shape: the intersecting geometry.</summary>
    public const string OutputSchema = "geometry";

    /// <summary>The full descriptor a conforming provider registers, with the shared conformance examples.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Computes the set-theoretic intersection of two geometries.",
        new SchemaDescriptor(InputSchema, "The left and right input geometries."),
        new SchemaDescriptor(OutputSchema, "The intersecting geometry (empty when the inputs are disjoint)."),
        [new ErrorVariant("invalid.arguments", "An argument is missing, of the wrong kind, or an input geometry the algorithm cannot process.")],
        [],
        CapabilityTraits.Cancellable,
        GeometryOperationConformanceExamples.Intersection);
}
