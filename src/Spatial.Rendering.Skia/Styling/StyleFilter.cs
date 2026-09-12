using Spatial.Core.Features;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// A compiled attribute predicate: the MapLibre <c>filter</c> expression
/// subset lowered onto core <see cref="IFeature"/> attributes. It never
/// becomes SQL — the store-level filter stays in the provider's safe grammar.
/// </summary>
internal abstract record StyleFilter
{
    /// <summary>The filter that accepts every feature.</summary>
    public static StyleFilter Always { get; } = new AlwaysFilter();

    public abstract bool Matches(IFeature feature);

    /// <summary>Reads a field tolerantly: an unknown field yields <see cref="AttributeValue.Null"/>.</summary>
    protected static AttributeValue Read(IFeature feature, string field)
    {
        var index = feature.Schema.IndexOf(field);
        return index < 0 ? AttributeValue.Null : feature[index];
    }

    /// <summary>Numeric equality across int/double kinds; structural equality otherwise.</summary>
    protected static bool ValueEquals(AttributeValue left, AttributeValue right)
    {
        if (IsNumeric(left) && IsNumeric(right))
        {
            return AsDouble(left) == AsDouble(right);
        }

        return left.Equals(right);
    }

    private static bool IsNumeric(AttributeValue value) =>
        value.Kind is AttributeKind.Int64 or AttributeKind.Double;

    private static double AsDouble(AttributeValue value) =>
        value.Kind == AttributeKind.Int64 ? value.Int64Value : value.DoubleValue;
}

internal sealed record AlwaysFilter : StyleFilter
{
    public override bool Matches(IFeature feature) => true;
}

internal sealed record HasFilter(string Field, bool Negated) : StyleFilter
{
    public override bool Matches(IFeature feature) => (!Read(feature, Field).IsNull) != Negated;
}

internal sealed record EqualsFilter(string Field, AttributeValue Value) : StyleFilter
{
    public override bool Matches(IFeature feature) => ValueEquals(Read(feature, Field), Value);
}

internal sealed record NotEqualsFilter(string Field, AttributeValue Value) : StyleFilter
{
    public override bool Matches(IFeature feature) => !ValueEquals(Read(feature, Field), Value);
}

internal sealed record InFilter(string Field, IReadOnlyList<AttributeValue> Values) : StyleFilter
{
    public override bool Matches(IFeature feature)
    {
        var value = Read(feature, Field);
        foreach (var candidate in Values)
        {
            if (ValueEquals(value, candidate))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record AllFilter(IReadOnlyList<StyleFilter> Filters) : StyleFilter
{
    public override bool Matches(IFeature feature)
    {
        foreach (var filter in Filters)
        {
            if (!filter.Matches(feature))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record AnyFilter(IReadOnlyList<StyleFilter> Filters) : StyleFilter
{
    public override bool Matches(IFeature feature)
    {
        foreach (var filter in Filters)
        {
            if (filter.Matches(feature))
            {
                return true;
            }
        }

        return false;
    }
}

internal sealed record NotFilter(StyleFilter Filter) : StyleFilter
{
    public override bool Matches(IFeature feature) => !Filter.Matches(feature);
}
