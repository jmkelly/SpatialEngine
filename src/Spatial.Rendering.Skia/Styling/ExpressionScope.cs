using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Rendering.Skia.Styling;

/// <summary>
/// The evaluation context of one feature: its attributes, its geometry type,
/// the viewport zoom and any <c>let</c> bindings, plus the per-feature memo
/// that makes a compiled expression cost one evaluation per feature no matter
/// how many paint properties read it (T-u2x.19). A scope is render-invariant,
/// so the scene builder keeps one per feature for the whole style and every
/// layer's paint resolves against it.
/// </summary>
internal sealed class ExpressionScope
{
    private readonly IFeature? _feature;
    private readonly List<KeyValuePair<string, ExpressionValue>> _bindings = [];
    private Dictionary<StyleExpression, ExpressionValue>? _memo;

    public ExpressionScope(IFeature? feature, IGeometry? geometry, double zoom)
    {
        _feature = feature;
        Zoom = zoom;
        GeometryType = GeometryTypeNames.Of(geometry);
    }

    /// <summary>The viewport zoom level (<c>["zoom"]</c>).</summary>
    public double Zoom { get; }

    /// <summary>The feature geometry's MapLibre type name (<c>["geometry-type"]</c>).</summary>
    public string GeometryType { get; }

    /// <summary>The feature identity (<c>["id"]</c>), or <c>null</c> outside a feature (a background layer).</summary>
    public string? FeatureId => _feature?.Id.Value;

    /// <summary>The feature whose attributes this scope reads.</summary>
    public IFeature? Feature => _feature;

    /// <summary>How many node evaluations actually ran (memo hits excluded).</summary>
    public int Evaluations { get; private set; }

    /// <summary>How many evaluations were served from this feature's memo.</summary>
    public int MemoHits { get; private set; }

    /// <summary>
    /// Evaluates <paramref name="expression"/>, memoising nodes that do not
    /// read a <c>let</c> binding: those are a pure function of the feature, so
    /// the second property (or the second layer) that reads the same compiled
    /// node costs a dictionary probe.
    /// </summary>
    public ExpressionValue Evaluate(StyleExpression expression)
    {
        // A literal is the same value for every feature: no probe, no count.
        if (expression is LiteralExpression literal)
        {
            return literal.Value;
        }

        if (expression.UsesVariables || _feature is null)
        {
            Evaluations++;
            return expression.Evaluate(this);
        }

        _memo ??= new Dictionary<StyleExpression, ExpressionValue>(ReferenceEqualityComparer.Instance);
        if (_memo.TryGetValue(expression, out var cached))
        {
            MemoHits++;
            return cached;
        }

        Evaluations++;
        var value = expression.Evaluate(this);
        _memo[expression] = value;
        return value;
    }

    /// <summary>Reads an attribute, tolerantly: an unknown field or a null value is the fallback.</summary>
    public ExpressionValue Attribute(string field, ExpressionValue fallback)
    {
        if (_feature is null)
        {
            return fallback;
        }

        var index = _feature.Schema.IndexOf(field);
        if (index < 0)
        {
            return fallback;
        }

        var value = _feature[index];
        return value.IsNull ? fallback : FromAttribute(value);
    }

    /// <summary>The expression value of a core attribute value.</summary>
    public static ExpressionValue FromAttribute(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Boolean => ExpressionValue.OfFlag(value.BooleanValue),
        AttributeKind.Int64 => ExpressionValue.OfNumber(value.Int64Value),
        AttributeKind.Double => ExpressionValue.OfNumber(value.DoubleValue),
        AttributeKind.String => ExpressionValue.OfText(value.StringValue),
        _ => ExpressionValue.Missing,
    };

    /// <summary>Reads a <c>let</c> binding; an unknown name is a compile-time error, so this is a null check.</summary>
    public ExpressionValue Variable(string name)
    {
        for (var index = _bindings.Count - 1; index >= 0; index--)
        {
            if (string.Equals(_bindings[index].Key, name, StringComparison.Ordinal))
            {
                return _bindings[index].Value;
            }
        }

        return ExpressionValue.Missing;
    }

    /// <summary>Binds the <c>let</c> names, innermost last.</summary>
    public void PushBindings(IReadOnlyList<KeyValuePair<string, StyleExpression>> bindings)
    {
        foreach (var binding in bindings)
        {
            _bindings.Add(new KeyValuePair<string, ExpressionValue>(binding.Key, Evaluate(binding.Value)));
        }
    }

    /// <summary>Drops the <c>let</c> bindings pushed by the matching <see cref="PushBindings"/>.</summary>
    public void PopBindings(int count) => _bindings.RemoveRange(_bindings.Count - count, count);
}
