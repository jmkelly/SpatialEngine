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
}
