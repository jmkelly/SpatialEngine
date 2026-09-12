namespace Spatial.Core.Features;

/// <summary>
/// Identity of a feature within a provider: a non-empty string. Structural
/// value; equality is ordinal.
/// </summary>
public readonly record struct FeatureId
{
    /// <summary>
    /// The reserved identity of a feature whose key the store is to assign on
    /// insert (ADR-0043). It is a non-empty, non-numeric, non-whitespace
    /// string that cannot collide with an OBJECTID; it is only meaningful as
    /// the input to <c>IFeatureEditStore.AddAsync</c>.
    /// </summary>
    public static FeatureId Unassigned { get; } = new("__unassigned__");

    public FeatureId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
