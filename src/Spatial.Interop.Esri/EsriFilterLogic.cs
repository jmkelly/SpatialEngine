using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Spatial.Core.Features;

namespace Spatial.Interop.Esri;

/// <summary>
/// The evaluation half of the filter grammar (ADR-0035): comparing an
/// attribute value against a literal, evaluating literal-to-literal
/// constants, resolving field references, rendering comparison operators and
/// matching LIKE patterns. These are stateless primitives; they live here so
/// <see cref="EsriFilterClause"/> stays the parser/AST surface.
/// </summary>
internal static class EsriFilterLogic
{
    /// <summary>
    /// Resolves a field reference against a synthetic field (when its name
    /// matches) then the feature schema, or fails as invalid arguments.
    /// </summary>
    public static AttributeValue FieldValue(IFeature feature, string field, EsriSyntheticField? syntheticField = null)
    {
        if (syntheticField is { } synthetic
            && string.Equals(field, synthetic.Name, StringComparison.OrdinalIgnoreCase))
        {
            return synthetic.Value;
        }

        var index = feature.Schema.IndexOf(field);
        if (index < 0)
        {
            throw EsriInteropException.Invalid($"The where clause names unknown field '{field}'.");
        }

        return feature[index];
    }

    /// <summary>Compares an attribute value against a literal under the given operator.</summary>
    public static bool Compare(AttributeValue attribute, ComparisonOperator comparisonOperator, Literal literal)
    {
        if (literal.Kind == LiteralKind.Null)
        {
            return false;
        }

        return CompareByKind(attribute, comparisonOperator, literal);
    }

    private static readonly Dictionary<AttributeKind, Func<AttributeValue, ComparisonOperator, Literal, bool>> KindComparers = new()
    {
        [AttributeKind.Int64] = (attribute, comparisonOperator, literal) => CompareNumber(attribute.Int64Value, literal, comparisonOperator),
        [AttributeKind.Double] = (attribute, comparisonOperator, literal) => CompareNumber(attribute.DoubleValue, literal, comparisonOperator),
        [AttributeKind.String] = (attribute, comparisonOperator, literal) => CompareStrings(attribute.StringValue, literal, comparisonOperator),
        [AttributeKind.Boolean] = (attribute, comparisonOperator, literal) => CompareBoolean(attribute.BooleanValue, literal, comparisonOperator),
        [AttributeKind.DateTimeOffset] = (attribute, comparisonOperator, literal) => CompareDate(attribute.DateTimeOffsetValue.ToUnixTimeMilliseconds(), literal, comparisonOperator),
        [AttributeKind.Guid] = (attribute, comparisonOperator, literal) => CompareGuid(attribute.GuidValue, literal, comparisonOperator),
    };

    private static bool CompareByKind(AttributeValue attribute, ComparisonOperator comparisonOperator, Literal literal) =>
        KindComparers.TryGetValue(attribute.Kind, out var compare) ? compare(attribute, comparisonOperator, literal) : false;

    private static readonly Func<double, double, bool>[] NumberComparisonPredicates =
    [
        (l, r) => l == r,
        (l, r) => l != r,
        (l, r) => l < r,
        (l, r) => l <= r,
        (l, r) => l > r,
        (l, r) => l >= r,
        (_, _) => false,
    ];

    private static bool CompareDate(long milliseconds, Literal literal, ComparisonOperator comparisonOperator)
    {
        if (literal.Kind == LiteralKind.DateTime)
        {
            return ApplyNumberComparison(milliseconds, literal.Number, comparisonOperator);
        }

        return CompareNumber(milliseconds, literal, comparisonOperator);
    }

    private static bool CompareNumber(double left, Literal literal, ComparisonOperator comparisonOperator)
    {
        if (literal.Kind is not (LiteralKind.Integer or LiteralKind.Decimal))
        {
            return false;
        }

        return ApplyNumberComparison(left, literal.Number, comparisonOperator);
    }

    private static bool ApplyNumberComparison(double left, double right, ComparisonOperator op)
    {
        var index = (int)op;
        if ((uint)index >= (uint)NumberComparisonPredicates.Length)
            return false;
        return NumberComparisonPredicates[index](left, right);
    }

    private static bool CompareStrings(string left, Literal literal, ComparisonOperator comparisonOperator)
    {
        if (literal.Kind != LiteralKind.String)
        {
            return false;
        }

        var comparison = StringComparer.Ordinal.Compare(left, literal.Text);
        return SatisfiesComparison(comparison, comparisonOperator);
    }

    private static readonly Func<int, bool>[] ComparisonPredicates =
    [
        c => c == 0,
        c => c != 0,
        c => c < 0,
        c => c <= 0,
        c => c > 0,
        c => c >= 0,
        _ => false,
    ];

    private static bool SatisfiesComparison(int comparison, ComparisonOperator op)
    {
        var index = (int)op;
        if ((uint)index >= (uint)ComparisonPredicates.Length)
            return false;
        return ComparisonPredicates[index](comparison);
    }

    private static bool CompareBoolean(bool left, Literal literal, ComparisonOperator comparisonOperator)
    {
        if (literal.Kind != LiteralKind.Boolean)
        {
            return false;
        }

        return comparisonOperator switch
        {
            ComparisonOperator.Equals => left == literal.Boolean,
            ComparisonOperator.NotEquals => left != literal.Boolean,
            _ => false,
        };
    }

    private static bool CompareGuid(Guid left, Literal literal, ComparisonOperator comparisonOperator)
    {
        if (literal.Kind != LiteralKind.String || !Guid.TryParse(literal.Text, out var right))
        {
            return false;
        }

        return comparisonOperator switch
        {
            ComparisonOperator.Equals => left == right,
            ComparisonOperator.NotEquals => left != right,
            _ => false,
        };
    }

    /// <summary>Evaluates a literal-to-literal comparison (no field involved).</summary>
    public static bool EvaluateConstant(Literal left, ComparisonOperator comparisonOperator, Literal right)
    {
        if (HasNullOperand(left, right))
        {
            return false;
        }

        if (IsDateOperand(left, right))
        {
            return BothDateTime(left, right) && ApplyNumberComparison(left.Number, right.Number, comparisonOperator);
        }

        if (IsBooleanOperand(left, right))
        {
            return EvaluateBooleanConstant(left, comparisonOperator, right);
        }

        if (IsStringOperand(left, right))
        {
            return EvaluateStringConstant(left, comparisonOperator, right);
        }

        return BothNumeric(left, right) && ApplyNumberComparison(left.Number, right.Number, comparisonOperator);
    }

    private static bool HasNullOperand(Literal left, Literal right) =>
        left.Kind is LiteralKind.Null || right.Kind is LiteralKind.Null;

    private static bool IsDateOperand(Literal left, Literal right) =>
        left.Kind == LiteralKind.DateTime || right.Kind == LiteralKind.DateTime;

    private static bool BothDateTime(Literal left, Literal right) =>
        left.Kind == LiteralKind.DateTime && right.Kind == LiteralKind.DateTime;

    private static bool IsBooleanOperand(Literal left, Literal right) =>
        left.Kind == LiteralKind.Boolean || right.Kind == LiteralKind.Boolean;

    private static bool IsStringOperand(Literal left, Literal right) =>
        left.Kind == LiteralKind.String && right.Kind == LiteralKind.String;

    private static bool BothNumeric(Literal left, Literal right) =>
        left.Kind is LiteralKind.Integer or LiteralKind.Decimal
        && right.Kind is LiteralKind.Integer or LiteralKind.Decimal;

    private static bool EvaluateBooleanConstant(Literal left, ComparisonOperator comparisonOperator, Literal right)
    {
        if (!BothBoolean(left, right))
        {
            return false;
        }

        if (comparisonOperator == ComparisonOperator.Equals)
        {
            return left.Boolean == right.Boolean;
        }

        if (comparisonOperator == ComparisonOperator.NotEquals)
        {
            return left.Boolean != right.Boolean;
        }

        return false;
    }

    private static bool BothBoolean(Literal left, Literal right) =>
        left.Kind == LiteralKind.Boolean && right.Kind == LiteralKind.Boolean;

    private static bool EvaluateStringConstant(Literal left, ComparisonOperator comparisonOperator, Literal right) =>
        comparisonOperator == ComparisonOperator.Like
            ? Regex.IsMatch(left.Text ?? string.Empty, ToRegex(right.Text ?? string.Empty), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
            : SatisfiesComparison(StringComparer.Ordinal.Compare(left.Text, right.Text), comparisonOperator);

    /// <summary>Matches an attribute value against a LIKE pattern, or fails as invalid arguments.</summary>
    public static bool MatchesLike(AttributeValue attribute, Literal literal)
    {
        if (attribute.Kind != AttributeKind.String || literal.Kind != LiteralKind.String)
        {
            throw EsriInteropException.Invalid("LIKE compares string fields to string patterns.");
        }

        return Regex.IsMatch(
            attribute.StringValue,
            ToRegex(literal.Text!),
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    /// <summary>The Esri where-clause rendering of a comparison operator.</summary>
    public static string OperatorText(ComparisonOperator comparisonOperator) => comparisonOperator switch
    {
        ComparisonOperator.Equals => "=",
        ComparisonOperator.NotEquals => "<>",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterOrEqual => ">=",
        ComparisonOperator.Like => "LIKE",
        _ => throw EsriInteropException.Invalid($"Unknown comparison operator {comparisonOperator}."),
    };

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

/// <summary>The comparison operators the grammar supports.</summary>
internal enum ComparisonOperator
{
    Equals,
    NotEquals,
    LessThan,
    LessOrEqual,
    GreaterThan,
    GreaterOrEqual,
    Like,
}

/// <summary>The literal kinds the grammar supports.</summary>
internal enum LiteralKind
{
    String,
    Integer,
    Decimal,
    Boolean,
    Null,
    /// <summary>
    /// A date-time literal (<c>TIMESTAMP '…'</c> or <c>CURRENT_TIMESTAMP …</c>):
    /// epoch milliseconds in <see cref="Literal.Number"/>, rendered back as a
    /// <c>TIMESTAMP</c> literal so the where-clause round-trips.
    /// </summary>
    DateTime,
}

/// <summary>One parsed literal with its wire-renderable form.</summary>
internal readonly record struct Literal(LiteralKind Kind, string? Text, double Number, bool Boolean)
{
    public string Render() => Kind switch
    {
        LiteralKind.String => "'" + (Text ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
        LiteralKind.Integer => ((long)Number).ToString(CultureInfo.InvariantCulture),
        LiteralKind.Decimal => Number.ToString("R", CultureInfo.InvariantCulture),
        LiteralKind.Boolean => Boolean ? "TRUE" : "FALSE",
        LiteralKind.DateTime => "TIMESTAMP '" + DateTimeOffset.FromUnixTimeMilliseconds((long)Number).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "'",
        _ => "NULL",
    };
}
