using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.Memory;

/// <summary>
/// The reference evaluator of the predicate vocabulary (ADR-0074 §4): it
/// answers a <see cref="Predicate"/> over an in-memory feature, and it is
/// what every store's pushdown has to agree with. The semantics are
/// deliberately SQL-shaped so the answer is the same one a pushed-down plan
/// gives:
/// <list type="bullet">
/// <item>a null attribute satisfies no comparison and no membership test, and
/// a null literal satisfies nothing (SQL's three-valued logic reads as
/// "not matched");</item>
/// <item>a numeric field compares against a number literal, a string field
/// against a string literal, a boolean only for <c>=</c>/<c>!=</c>, a
/// date-time against a date-time literal and a guid against a guid-formatted
/// string;</item>
/// <item>a literal of another kind never matches, rather than coercing;</item>
/// <item><c>LIKE</c> is a whole-value pattern match where <c>%</c> is any
/// run and <c>_</c> is one character.</item>
/// </list>
/// It is store code, never Core: evaluation is an implementation concern
/// (ADR-0074 §2).
/// </summary>
internal static class MemoryPredicate
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Whether the predicate holds for the feature; an unknown field is a typed invalid-argument failure.</summary>
    public static bool Matches(Predicate predicate, IFeature feature) => Evaluate(predicate, feature);

    private static bool Evaluate(Predicate predicate, IFeature feature) => predicate switch
    {
        Predicate.Every every => every.Terms.All(term => Evaluate(term, feature)),
        Predicate.Some some => some.Terms.Any(term => Evaluate(term, feature)),
        Predicate.Constant constant => constant.Value,
        Predicate.IsNull isNull => Value(feature, isNull.Field).IsNull != isNull.Negated,
        Predicate.IsIn isIn => MatchesIn(feature, isIn),
        Predicate.Compare compare => Compare(feature, compare),
        _ => false,
    };

    private static bool MatchesIn(IFeature feature, Predicate.IsIn isIn)
    {
        var value = Value(feature, isIn.Field);
        if (value.IsNull)
        {
            return false;
        }

        // NOT IN over a value that matches is false and NOT IN over one that
        // does not is true — the same answer SQL's `NOT IN` gives once the
        // null case above has been settled.
        return isIn.Values.Any(candidate => Equal(value, candidate)) != isIn.Negated;
    }

    private static bool Compare(IFeature feature, Predicate.Compare compare)
    {
        var value = Value(feature, compare.Field);
        if (value.IsNull || compare.Value.Kind == LiteralKind.Null)
        {
            return false;
        }

        if (compare.Operator == ComparisonOperator.Like)
        {
            return value.Kind == AttributeKind.String
                && compare.Value.Kind == LiteralKind.String
                && LikePattern.IsMatch(value.StringValue, compare.Value.Text!);
        }

        return Apply(value, compare.Operator, compare.Value);
    }

    private static bool Apply(AttributeValue value, ComparisonOperator comparison, Literal literal) => value.Kind switch
    {
        AttributeKind.Int64 => Whole(value.Int64Value, comparison, literal),
        AttributeKind.Double => Number(value.DoubleValue, comparison, literal),
        AttributeKind.DateTimeOffset => Number(value.DateTimeOffsetValue.ToUnixTimeMilliseconds(), comparison, literal),
        AttributeKind.String => Text(value.StringValue, comparison, literal),
        AttributeKind.Boolean => Flag(value.BooleanValue, comparison, literal),
        AttributeKind.Guid => Identifier(value.GuidValue, comparison, literal),
        _ => false,
    };

    /// <summary>
    /// A whole number against a whole number stays whole: a value wider than a
    /// double (past 2^53) must compare exactly, as the SQL back ends do.
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

        if (literal.Kind == LiteralKind.Integer && long.TryParse(literal.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return Ordering(left, integer, comparison);
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

    private static bool Equal(AttributeValue value, Literal literal) => Apply(value, ComparisonOperator.Equals, literal);

    private static AttributeValue Value(IFeature feature, FieldRef field)
    {
        var index = feature.Schema.IndexOf(field.Name);
        if (index < 0)
        {
            throw Spatial.Contracts.SpatialException.BadArguments(
                $"The filter column '{field.Name}' is not a field of this dataset; available fields: {Available(feature.Schema)}.");
        }

        return feature[index];
    }

    private static string Available(IFeatureSchema schema) =>
        string.Join(", ", schema.Fields.Select(field => $"'{field.Name}'"));

    /// <summary>
    /// The LIKE patterns: <c>%</c> matches any run of characters, <c>_</c>
    /// exactly one, and the pattern is anchored at both ends, so it is a
    /// whole-value test rather than a substring search.
    /// </summary>
    private static class LikePattern
    {
        public static bool IsMatch(string value, string pattern) =>
            Regex.IsMatch(value, ToRegex(pattern), RegexOptions.CultureInvariant, MatchTimeout);

        private static string ToRegex(string pattern)
        {
            var builder = new StringBuilder("^");
            foreach (var character in pattern)
            {
                builder.Append(character switch
                {
                    '%' => ".*",
                    '_' => ".",
                    _ => Regex.Escape(character.ToString()),
                });
            }

            return builder.Append('$').ToString();
        }
    }
}
