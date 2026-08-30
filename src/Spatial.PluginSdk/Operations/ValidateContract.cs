using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Operations;

/// <summary>
/// The <c>spatial.geometry.validate@1</c> capability contract (plan §16
/// Phase 6, ADR-0026): reports whether a geometry is structurally valid per
/// the OGC simple-feature rules (closed rings, no self-intersections, and so
/// on). Input is <c>geometry.single</c> — one geometry — and the output is
/// the <c>validation.flag</c> boolean. An invalid geometry is a successful
/// <c>false</c>, not a failure; only arguments the algorithm cannot read are
/// <c>invalid.arguments</c>.
/// </summary>
public static class ValidateContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.geometry.validate@1");

    /// <summary>The input interchange shape: one geometry.</summary>
    public const string InputSchema = "geometry.single";

    /// <summary>The output interchange shape: a validity flag.</summary>
    public const string OutputSchema = "validation.flag";

    /// <summary>The full descriptor a conforming provider registers, with the shared conformance examples.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Reports whether a geometry is structurally valid per the OGC simple-feature rules.",
        new SchemaDescriptor(InputSchema, "One input geometry."),
        new SchemaDescriptor(OutputSchema, "True when the geometry is valid, false when it is not."),
        [new ErrorVariant("invalid.arguments", "An argument is missing, of the wrong kind, or an input geometry the algorithm cannot process.")],
        [],
        CapabilityTraits.Cancellable,
        []);
}
