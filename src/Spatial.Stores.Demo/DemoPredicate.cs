using System.Globalization;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.Demo;

/// <summary>
/// The demo store's evaluation of the engine's one predicate vocabulary
/// (ADR-0074 §2: a store without a pushdown evaluates the plan itself). The
/// demo catalogue is procedurally generated and in-process, so filtering
/// happens here rather than in SQL — with the same reference semantics the
/// SQL pushdowns and the in-memory store are held to, which the shared
/// conformance suite pins case by case.
/// </summary>
internal static class DemoPredicate
{
    /// <summary>
    /// Whether the predicate holds for the feature. A null attribute
    /// satisfies no comparison and no membership test, and a literal of
    /// another kind never matches: the comparison is false rather than a
    /// coercion, which is what every store's pushdown answers too.
    /// </summary>
    public static bool Matches(Predicate predicate, IFeature feature) => Evaluate(predicate, feature);

    private static bool Evaluate(Predicate predicate, IFeature feature) => predicate switch
    {
        Predicate.Every every => every.Terms.All(term => Evaluate(term, feature)),
        Predicate.Some some => some.Terms.Any(term => Evaluate(term, feature)),
        Predicate.Constant constant => constant.Value,
        Predicate.IsNull isNull => feature[isNull.Field.Name].IsNull != isNull.Negated,
        Predicate.IsIn isIn => In(feature, isIn),
        Predicate.Compare compare => Compare(feature[compare.Field.Name], compare.Operator, compare.Value),
        _ => false,
    };

    private static bool In(IFeature feature, Predicate.IsIn isIn)
    {
        var value = feature[isIn.Field.Name];
        if (value.IsNull)
        {
            return false;
        }

        return isIn.Values.Any(candidate => Compare(value, ComparisonOperator.Equals, candidate)) != isIn.Negated;
    }

    private static bool Compare(AttributeValue value, ComparisonOperator comparison, Literal literal)
    {
        if (value.IsNull || literal.Kind == LiteralKind.Null)
        {
            return false;
        }

        if (comparison == ComparisonOperator.Like)
        {
            return value.Kind == AttributeKind.String
                && literal.Kind == LiteralKind.String
                && LikePattern.Matches(value.StringValue, literal.Text);
        }

        return value.Kind switch
        {
            AttributeKind.Int64 => Whole(value.Int64Value, comparison, literal),
            AttributeKind.Double => Number(value.DoubleValue, comparison, literal),
            AttributeKind.DateTimeOffset => Number(value.DateTimeOffsetValue.ToUnixTimeMilliseconds(), comparison, literal),
            AttributeKind.String => Text(value.StringValue, comparison, literal),
            AttributeKind.Boolean => Flag(value.BooleanValue, comparison, literal),
            AttributeKind.Guid => Identifier(value.GuidValue, comparison, literal),
            _ => false,
        };
    }

    /// <summary>
    /// A whole number against a whole number stays whole, so a value wider
    /// than a double (past 2^53) compares exactly, as the SQL back ends do.
    /// </summary>
    private static bool Whole(long left, ComparisonOperator comparison, Literal literal)
    {
        if (literal.Kind == LiteralKind.Integer
            && long.TryParse(literal.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return comparison switch
            {
                ComparisonOperator.Equals => left == whole,
                ComparisonOperator.NotEquals => left != whole,
                ComparisonOperator.LessThan => left < whole,
                ComparisonOperator.LessOrEqual => left <= whole,
                ComparisonOperator.GreaterThan => left > whole,
                ComparisonOperator.GreaterOrEqual => left >= whole,
                _ => false,
            };
        }

        return Number(left, comparison, literal);
    }

    private static bool Number(double left, ComparisonOperator comparison, Literal literal)
    {
        if (literal.Kind == LiteralKind.DateTime)
        {
            return Ordering(left, literal.Number, comparison);
        }

        if (literal.Kind == LiteralKind.Integer)
        {
            return long.TryParse(literal.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)
                ? Ordering(left, whole, comparison)
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
}
