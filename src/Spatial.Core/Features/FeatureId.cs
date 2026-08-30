namespace Spatial.Core.Features;

/// <summary>
/// Identity of a feature within a provider: a non-empty string. Structural
/// value; equality is ordinal.
/// </summary>
public readonly record struct FeatureId
{
    public FeatureId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
