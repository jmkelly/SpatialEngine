namespace Spatial.Core.Features;

/// <summary>
/// The contract face of one feature value: a stable identity, the schema it
/// was validated against and its attributes (ADR-0029). Consumers that only
/// inspect or transport features may bind this interface; construction stays
/// concrete (<see cref="Feature"/> is the immutable implementation, ADR-0004).
/// </summary>
public interface IFeature
{
    /// <summary>The feature identity within its provider.</summary>
    FeatureId Id { get; }

    /// <summary>The schema the attributes are validated against.</summary>
    IFeatureSchema Schema { get; }

    /// <summary>The attributes, in schema field order.</summary>
    IReadOnlyList<AttributeValue> Attributes { get; }

    /// <summary>The attribute at a schema position.</summary>
    AttributeValue this[int index] { get; }

    /// <summary>The attribute for a named field.</summary>
    AttributeValue this[string name] { get; }
}
