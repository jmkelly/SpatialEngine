using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Operations;

/// <summary>
/// The <c>spatial.geometry.buffer@1</c> capability contract (plan §16 Phase
/// 6, ADR-0026): expands or shrinks a geometry by a distance per the OGC
/// buffer semantics. Input is <c>geometry.operation</c> — a geometry plus a
/// finite distance (and an optional positive quadrant segment count) — and
/// the output is the buffered <c>geometry</c>. Adapters implementing the
/// contract must not expose third-party geometry types (ADR-0005).
/// </summary>
public static class BufferContract
{
    /// <summary>The versioned capability identity.</summary>
    public static CapabilityId Id { get; } = CapabilityId.Parse("spatial.geometry.buffer@1");

    /// <summary>The input interchange shape: a geometry plus a buffer distance.</summary>
    public const string InputSchema = "geometry.operation";

    /// <summary>The output interchange shape: the buffered geometry.</summary>
    public const string OutputSchema = "geometry";

    /// <summary>The full descriptor a conforming provider registers, with the shared conformance examples.</summary>
    public static CapabilityDescriptor Descriptor { get; } = new(
        Id,
        "Expands or shrinks a geometry by a distance (OGC buffer).",
        new SchemaDescriptor(InputSchema, "A geometry plus a finite buffer distance and an optional quadrant segment count."),
        new SchemaDescriptor(OutputSchema, "The buffered geometry."),
        [new ErrorVariant("invalid.arguments", "An argument is missing, of the wrong kind, or an input geometry the algorithm cannot process.")],
        [],
        CapabilityTraits.Cancellable,
        GeometryOperationConformanceExamples.Buffer);
}
