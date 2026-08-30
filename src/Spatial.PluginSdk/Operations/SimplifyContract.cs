using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Operations;

/// <summary>
/// The <c>spatial.geometry.simplify@1</c> capability contract (plan §16
/// Phase 6, ADR-0026): reduces a geometry's detail with the
/// Douglas-Peucker algorithm at a non-negative tolerance. Input is
/// <c>geometry.operation</c> — a geometry plus a finite tolerance — and the
/// output is the simplified <c>geometry</c>. A tolerance of zero returns the
/// geometry unchanged.
/// </summary>
public static class SimplifyContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.geometry.simplify@1");

    /// <summary>The input interchange shape: a geometry plus a tolerance.</summary>
    public const string InputSchema = "geometry.operation";

    /// <summary>The output interchange shape: the simplified geometry.</summary>
    public const string OutputSchema = "geometry";

    /// <summary>The full descriptor a conforming provider registers, with the shared conformance examples.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Reduces a geometry's detail with the Douglas-Peucker algorithm at a tolerance.",
        new SchemaDescriptor(InputSchema, "A geometry plus a finite, non-negative simplification tolerance."),
        new SchemaDescriptor(OutputSchema, "The simplified geometry."),
        [new ErrorVariant("invalid.arguments", "An argument is missing, of the wrong kind, or an input geometry the algorithm cannot process.")],
        [],
        CapabilityTraits.Cancellable,
        []);
}
