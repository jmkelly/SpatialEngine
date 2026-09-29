namespace Spatial.Core.Features;

/// <summary>
/// The kind of an attribute value or field. Values are explicit so the wire
/// format of <see cref="FeatureBatchCodec"/> is stable.
/// </summary>
public enum AttributeKind : byte
{
    /// <summary>The absence of a value in a nullable field. Never a field's declared kind.</summary>
    Null = 0,

    Boolean = 1,

    Int64 = 2,

    Double = 3,

    String = 4,

    Geometry = 5,

    DateTimeOffset = 6,

    Guid = 7,

    /// <summary>
    /// A reduced bounding rectangle, reported by the envelope statistic of a
    /// grouped reduction (ADR-0120). Like <see cref="Null"/> it is never a
    /// field's declared kind: a column holds a geometry, and a rectangle is what
    /// a reduction of a column of geometries is — so the value exists only in
    /// the results of a reduction, and a schema cannot declare one.
    /// </summary>
    Envelope = 8,
}
