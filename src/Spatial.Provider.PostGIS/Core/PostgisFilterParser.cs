
namespace Spatial.Provider.PostGIS.Core;

/// <summary>
/// Recursive-descent parser of the attribute filter language
/// (feature queries, ADR-0028):
///
/// <code>
/// filter      := orExpr  EOF
/// orExpr      := andExpr (OR andExpr)*
/// andExpr     := term (AND term)*
/// term        := '(' orExpr ')' | comparison
/// comparison  := column op value | column IS [NOT] NULL
/// op          := = != &lt;&gt; &lt; &lt;= &gt; &gt;= LIKE
/// column      := identifier
/// value       := string | number | TRUE | FALSE
/// </code>
///
/// The grammar is deliberately tiny and closed: unknown columns are rejected
/// later by the schema resolution (PostgisFilterSql), and every literal is a
/// parameter — this is the parameterised-filtering guarantee of ADR-0028.
/// </summary>
internal static class PostgisFilterParser
{
    /// <summary>Parses a complete filter expression, or reports the offending position.</summary>
    public static bool TryParse(string text, out FilterExpression expression, out string error)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!PostgisFilterLexer.TryTokenize(text, out var tokens, out error))
        {
            expression = null!;
            return false;
        }

        var parser = new Parser(tokens);
        if (!parser.TryExpression(out expression, out error))
        {
            return false;
        }

        if (parser.Current.Kind != FilterTokenKind.End)
        {
            error = $"unexpected '{parser.Current.Text}' at position {parser.Current.Position} (expected the end of the filter)";
            return false;
        }

        return true;
    }

    private sealed class Parser(IReadOnlyList<FilterToken> tokens)
    {
        private int _index;

        public FilterToken Current => tokens[_index];

        public bool TryExpression(out FilterExpression expression, out string error) => TryOr(out expression, out error);

        private bool TryOr(out FilterExpression expression, out string error)
        {
            if (!TryAnd(out var first, out error))
            {
                expression = null!;
                return false;
            }

            var terms = new List<FilterExpression> { first };
            while (Current.Kind == FilterTokenKind.Or)
            {
                _index++;
                if (!TryAnd(out var next, out error))
                {
                    expression = null!;
                    return false;
                }

                terms.Add(next);
            }

            expression = terms.Count == 1 ? terms[0] : new FilterExpression.Or(terms);
            return true;
        }

        private bool TryAnd(out FilterExpression expression, out string error)
        {
            if (!TryTerm(out var first, out error))
            {
                expression = null!;
                return false;
            }

            var terms = new List<FilterExpression> { first };
            while (Current.Kind == FilterTokenKind.And)
            {
                _index++;
                if (!TryTerm(out var next, out error))
                {
                    expression = null!;
                    return false;
                }

                terms.Add(next);
            }

            expression = terms.Count == 1 ? terms[0] : new FilterExpression.And(terms);
            return true;
        }

        private bool TryTerm(out FilterExpression expression, out string error)
        {
            if (Current.Kind == FilterTokenKind.LeftParen)
            {
                _index++;
                if (!TryOr(out expression, out error))
                {
                    return false;
                }

                if (Current.Kind != FilterTokenKind.RightParen)
                {
                    error = $"expected ')' at position {Current.Position}, found '{Current.Text}'";
                    return false;
                }

                _index++;
                return true;
            }

            if (Current.Kind == FilterTokenKind.Identifier)
            {
                return TryComparison(out expression, out error);
            }

            error = $"expected a filter term at position {Current.Position}, found '{Current.Text}'";
            expression = null!;
            return false;
        }

        private bool TryComparison(out FilterExpression expression, out string error)
        {
            var column = Current.Text;
            var columnPosition = Current.Position;
            _index++;

            if (Current.Kind == FilterTokenKind.Is)
            {
                _index++;
                var negated = false;
                if (Current.Kind == FilterTokenKind.Not)
                {
                    negated = true;
                    _index++;
                }

                if (Current.Kind != FilterTokenKind.Null)
                {
                    error = $"expected NULL after IS at position {Current.Position}, found '{Current.Text}'";
                    expression = null!;
                    return false;
                }

                _index++;
                expression = new FilterExpression.IsNull(column, negated);
                error = string.Empty;
                return true;
            }

            if (!TryOperator(out var filterOperator, out error))
            {
                expression = null!;
                return false;
            }

            if (Current.Kind is FilterTokenKind.End or FilterTokenKind.RightParen or FilterTokenKind.And or FilterTokenKind.Or)
            {
                error = $"expected a value after '{Current.Text}' for column '{column}' at position {columnPosition}";
                expression = null!;
                return false;
            }

            if (!TryValue(out var value, out error))
            {
                expression = null!;
                return false;
            }

            expression = new FilterExpression.Comparison(column, filterOperator, value);
            return true;
        }

        private bool TryOperator(out FilterOperator filterOperator, out string error)
        {
            filterOperator = Current.Kind switch
            {
                FilterTokenKind.Equals => FilterOperator.Equals,
                FilterTokenKind.NotEquals => FilterOperator.NotEquals,
                FilterTokenKind.LessThan => FilterOperator.LessThan,
                FilterTokenKind.LessOrEqual => FilterOperator.LessOrEqual,
                FilterTokenKind.GreaterThan => FilterOperator.GreaterThan,
                FilterTokenKind.GreaterOrEqual => FilterOperator.GreaterOrEqual,
                FilterTokenKind.Like => FilterOperator.Like,
                _ => (FilterOperator)(-1),
            };
            if ((int)filterOperator < 0)
            {
                error = $"expected a comparison operator at position {Current.Position}, found '{Current.Text}'";
                return false;
            }

            _index++;
            error = string.Empty;
            return true;
        }

        private bool TryValue(out FilterValue value, out string error)
        {
            switch (Current.Kind)
            {
                case FilterTokenKind.Integer:
                case FilterTokenKind.Decimal:
                    value = new FilterValue(kindOf(Current.Kind), Current.Text);
                    break;
                case FilterTokenKind.String:
                    value = new FilterValue(FilterValueKind.String, Current.Text);
                    break;
                case FilterTokenKind.True:
                case FilterTokenKind.False:
                    value = new FilterValue(FilterValueKind.Boolean, Current.Text);
                    break;
                case FilterTokenKind.InvalidNumber:
                    error = $"'{Current.Text}' is not a valid filter number at position {Current.Position}";
                    value = default;
                    return false;
                default:
                    error = $"expected a filter value at position {Current.Position}, found '{Current.Text}'";
                    value = default;
                    return false;
            }

            _index++;
            error = string.Empty;
            return true;

            static FilterValueKind kindOf(FilterTokenKind kind) =>
                kind == FilterTokenKind.Integer ? FilterValueKind.Integer : FilterValueKind.Decimal;
        }
    }
}
