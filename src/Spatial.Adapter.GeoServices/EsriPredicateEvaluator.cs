using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Resolves a parsed Esri <c>where</c> clause against a served layer
/// (ADR-0074 §7). The clause is core-typed, so the only Esri-specific part
/// left is the synthetic <c>OBJECTID</c>: it becomes a field reference to the
/// layer's identity column, which is a column the store can read, and the
/// clause is then pushed down. A layer whose <c>OBJECTID</c> is the scan
/// ordinal has no such column, so such a clause stays with the facade and is
/// evaluated per feature by <see cref="EsriPredicateEvaluator"/> — the one
/// case where an attribute filter cannot be a store pushdown.
/// </summary>
internal static class EsriWhereResolver
{
    /// <summary>
    /// The predicate a store can answer for the clause, or null when the
    /// facade has to evaluate it per feature because it names the synthetic
    /// <c>OBJECTID</c> of a layer with no integer identity column.
    /// </summary>
    public static Predicate? Pushdown(EsriWhere? where, EsriObjectIdScheme scheme, DatasetDescription dataset)
    {
        if (where?.Predicate is not { } predicate)
        {
            return null;
        }

        if (!NamesObjectId(predicate))
        {
            return predicate;
        }

        return scheme.IsIdentity && dataset.IdColumns is [var identityColumn]
            ? Rename(predicate, identityColumn)
            : null;
    }

    /// <summary>Whether the clause reads the facade's synthetic object id.</summary>
    private static bool NamesObjectId(Predicate predicate) =>
        predicate.Fields().Any(IsObjectId);

    /// <summary>
    /// Rewrites the synthetic object-id reference onto the layer's identity
    /// column, so the pushed-down plan reads a column the dataset really has.
    /// </summary>
    private static Predicate Rename(Predicate predicate, string column) => predicate switch
    {
        Predicate.Every every => new Predicate.Every([.. every.Terms.Select(term => Rename(term, column))]),
        Predicate.Some some => new Predicate.Some([.. some.Terms.Select(term => Rename(term, column))]),
        Predicate.Compare compare => Rename(compare, column),
        Predicate.IsNull isNull => new Predicate.IsNull(Rename(isNull.Field, column), isNull.Negated),
        Predicate.IsIn isIn => new Predicate.IsIn(Rename(isIn.Field, column), isIn.Values, isIn.Negated),
        _ => predicate,
    };

    private static Predicate.Compare Rename(Predicate.Compare compare, string column) =>
        IsObjectId(compare.Field)
            ? new Predicate.Compare(new FieldRef(column), compare.Operator, compare.Value)
            : compare;

    private static FieldRef Rename(FieldRef field, string column) =>
        IsObjectId(field) ? new FieldRef(column) : field;

    /// <summary>Whether a field reference names the facade's synthetic object id, whatever its case.</summary>
    private static bool IsObjectId(FieldRef field) =>
        string.Equals(field.Name, EsriLayerModel.ObjectIdField, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// One value a clause may reference that is not in the feature schema, such
/// as the facade's synthetic <c>OBJECTID</c> (ADR-0037). The core predicate
/// vocabulary has no synthetic fields — a store reads columns — so the
/// overlay is the facade's own, applied only where a clause cannot be pushed
/// down (see <see cref="EsriWhereResolver"/>).
/// </summary>
internal readonly record struct EsriFieldOverlay(string Name, AttributeValue Value);

/// <summary>
/// The facade's per-feature evaluation of the core predicate vocabulary, for
/// the paths that filter a set of features the facade already holds: the
/// ordinal-<c>OBJECTID</c> residual, the <c>layerDefs</c> identify filters,
/// the renderer's classification filter, the edit engine's id resolution and
/// the statistics <c>having</c> test. Its semantics are the reference
/// semantics the stores' pushdown has to agree with (ADR-0074 §4), so the
/// conformance suite pins it against the in-memory store and both SQL
/// providers; the home for the shared evaluator is tracked separately.
/// </summary>
internal static class EsriPredicateEvaluator
{
    /// <summary>
    /// Whether the predicate holds for the feature, resolving
    /// <paramref name="overlay"/> (the facade's synthetic <c>OBJECTID</c>)
    /// before the feature schema. An unknown field name is a typed
    /// invalid-argument failure.
    /// </summary>
    public static bool Matches(Predicate? predicate, IFeature feature, EsriFieldOverlay? overlay = null) =>
        predicate is null || Evaluate(predicate, feature, overlay);

    private static bool Evaluate(Predicate predicate, IFeature feature, EsriFieldOverlay? overlay) => predicate switch
    {
        Predicate.Every every => every.Terms.All(term => Evaluate(term, feature, overlay)),
        Predicate.Some some => some.Terms.Any(term => Evaluate(term, feature, overlay)),
        Predicate.Constant constant => constant.Value,
        Predicate.IsNull isNull => Value(feature, isNull.Field, overlay).IsNull != isNull.Negated,
        Predicate.IsIn isIn => In(feature, isIn, overlay),
        Predicate.Compare compare => Compare(feature, compare, overlay),
        _ => false,
    };

    private static bool In(IFeature feature, Predicate.IsIn isIn, EsriFieldOverlay? overlay)
    {
        var value = Value(feature, isIn.Field, overlay);
        if (value.IsNull)
        {
            return false;
        }

        return isIn.Values.Any(candidate => Test(value, ComparisonOperator.Equals, candidate)) != isIn.Negated;
    }

    private static bool Compare(IFeature feature, Predicate.Compare compare, EsriFieldOverlay? overlay)
    {
        var value = Value(feature, compare.Field, overlay);
        return !value.IsNull
            && (compare.Operator == ComparisonOperator.Like
                ? Like(value, compare.Value)
                : Test(value, compare.Operator, compare.Value));
    }

    private static bool Like(AttributeValue value, Literal literal)
    {
        if (value.Kind != AttributeKind.String || literal.Kind != LiteralKind.String)
        {
            return false;
        }

        return EsriLikePattern.IsMatch(value.StringValue, literal.Text ?? string.Empty);
    }

    /// <summary>
    /// Compares an attribute value against a literal. A null literal and a
    /// literal of another kind never match: the comparison is false rather
    /// than a coercion, which is what every store's pushdown answers too.
    /// </summary>
    private static bool Test(AttributeValue value, ComparisonOperator comparison, Literal literal)
    {
        if (literal.Kind == LiteralKind.Null || value.IsNull)
        {
            return false;
        }

        return value.Kind switch
        {
            AttributeKind.Int64 => Number(value.Int64Value, comparison, literal),
            AttributeKind.Double => Number(value.DoubleValue, comparison, literal),
            AttributeKind.DateTimeOffset => Number(value.DateTimeOffsetValue.ToUnixTimeMilliseconds(), comparison, literal),
            AttributeKind.String => Text(value.StringValue, comparison, literal),
            AttributeKind.Boolean => Flag(value.BooleanValue, comparison, literal),
            AttributeKind.Guid => Identifier(value.GuidValue, comparison, literal),
            _ => false,
        };
    }

    private static bool Number(double left, ComparisonOperator comparison, Literal literal)
    {
        if (literal.Kind == LiteralKind.DateTime)
        {
            return Ordering(left, literal.Number, comparison);
        }

        if (literal.Kind == LiteralKind.Integer)
        {
            return long.TryParse(literal.Text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var integer)
                ? Ordering(left, integer, comparison)
                : Ordering(left, literal.Number, comparison);
        }

        return literal.Kind == LiteralKind.Decimal && Ordering(left, literal.Number, comparison);
    }

    private static bool Ordering(double left, double right, ComparisonOperator comparison) => comparison switch
    {
        ComparisonOperator.Equals => left == right,
        ComparisonOperator.NotEquals => left != right,
        ComparisonOperator.LessThan => left < right,
        ComparisonOperator.LessOrEqual => left <= right,
        ComparisonOperator.GreaterThan => left > right,
        ComparisonOperator.GreaterOrEqual => left >= right,
        _ => false,
    };

    private static bool Text(string left, ComparisonOperator comparison, Literal literal) =>
        literal.Kind == LiteralKind.String
        && Satisfies(StringComparer.Ordinal.Compare(left, literal.Text), comparison);

    private static bool Satisfies(int comparison, ComparisonOperator comparison2) => comparison2 switch
    {
        ComparisonOperator.Equals => comparison == 0,
        ComparisonOperator.NotEquals => comparison != 0,
        ComparisonOperator.LessThan => comparison < 0,
        ComparisonOperator.LessOrEqual => comparison <= 0,
        ComparisonOperator.GreaterThan => comparison > 0,
        ComparisonOperator.GreaterOrEqual => comparison >= 0,
        _ => false,
    };

    private static bool Flag(bool left, ComparisonOperator comparison, Literal literal)
    {
        if (literal.Kind != LiteralKind.Boolean)
        {
            return false;
        }

        return comparison switch
        {
            ComparisonOperator.Equals => left == literal.Boolean,
            ComparisonOperator.NotEquals => left != literal.Boolean,
            _ => false,
        };
    }

    private static bool Identifier(Guid left, ComparisonOperator comparison, Literal literal)
    {
        if (literal.Kind != LiteralKind.String || !Guid.TryParse(literal.Text, out var right))
        {
            return false;
        }

        return comparison switch
        {
            ComparisonOperator.Equals => left == right,
            ComparisonOperator.NotEquals => left != right,
            _ => false,
        };
    }

    private static AttributeValue Value(IFeature feature, FieldRef field, EsriFieldOverlay? overlay)
    {
        if (overlay is { } synthetic
            && string.Equals(field.Name, synthetic.Name, StringComparison.OrdinalIgnoreCase))
        {
            return synthetic.Value;
        }

        var index = feature.Schema.IndexOf(field.Name);
        if (index < 0)
        {
            throw EsriInteropException.Invalid($"The where clause names unknown field '{field.Name}'.");
        }

        return feature[index];
    }
}
