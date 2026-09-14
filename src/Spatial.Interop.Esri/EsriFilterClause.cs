using System.Globalization;
using System.Text;
using Spatial.Core.Features;

namespace Spatial.Interop.Esri;

/// <summary>
/// The safe attribute-filter grammar shared by both GeoServices directions
/// (ADR-0035): AND / OR / parentheses / comparisons / LIKE / IS [NOT] NULL
/// over a dataset's fields. The serving facade parses a client <c>where</c>
/// and evaluates it over the store's features; the consuming provider parses
/// the engine's filter grammar and renders a remote <c>where</c>. Either way
/// the grammar is closed — anything outside it is rejected, so client text
/// never becomes SQL structure.
/// </summary>
public sealed class EsriFilterClause
{
    private readonly Node _root;

    private EsriFilterClause(Node root) => _root = root;

    /// <summary>Parses a complete where clause, or reports the offending position.</summary>
    public static bool TryParse(string text, out EsriFilterClause? clause, out string error)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Lexer.TryTokenize(text, out var tokens, out error))
        {
            clause = null;
            return false;
        }

        var parser = new Parser(tokens);
        if (!parser.TryExpression(out var root, out error))
        {
            clause = null;
            return false;
        }

        if (parser.Current.Kind != TokenKind.End)
        {
            error = $"unexpected '{parser.Current.Text}' at position {parser.Current.Position} (expected the end of the where clause)";
            clause = null;
            return false;
        }

        clause = new EsriFilterClause(root);
        return true;
    }

    /// <summary>
    /// Evaluates the clause over a feature. Unknown field names and
    /// incomparable type pairs are typed invalid-argument failures.
    /// </summary>
    public bool Matches(IFeature feature) => Matches(feature, null);

    /// <summary>
    /// Evaluates the clause over a feature, resolving
    /// <paramref name="syntheticField"/> (for example a facade's
    /// <c>OBJECTID</c>) when the feature schema does not carry it.
    /// </summary>
    public bool Matches(IFeature feature, EsriSyntheticField? syntheticField)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return _root.Evaluate(feature, syntheticField);
    }

    /// <summary>Renders the clause as an Esri <c>where</c> string (for the consuming provider).</summary>
    public string ToWhere() => _root.Render();

    /// <summary>
    /// The field names the clause references, in first-appearance order
    /// (comparison and <c>IS NULL</c> operands; constant comparisons such as
    /// <c>1=1</c> reference none). The serving facade validates them against
    /// the layer schema (<c>validateSQL</c>); the provider renders them into
    /// a remote <c>where</c>.
    /// </summary>
    public IReadOnlyList<string> ReferencedFields => _root.Fields().Distinct(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// Conjoins two clauses: both must match. Adjacent conjunctions flatten
    /// so a service-level query can AND a shared <c>where</c> with a per-layer
    /// <c>layerDefs</c> clause without re-parsing rendered text.
    /// </summary>
    public EsriFilterClause And(EsriFilterClause other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (_root is AndNode left && other._root is AndNode right)
        {
            return new EsriFilterClause(new AndNode([.. left.Terms, .. right.Terms]));
        }

        if (_root is AndNode single)
        {
            return new EsriFilterClause(new AndNode([.. single.Terms, other._root]));
        }

        if (other._root is AndNode flipped)
        {
            return new EsriFilterClause(new AndNode([_root, .. flipped.Terms]));
        }

        return new EsriFilterClause(new AndNode([_root, other._root]));
    }

    private abstract record Node
    {
        public abstract bool Evaluate(IFeature feature, EsriSyntheticField? syntheticField);

        public abstract string Render();

        public virtual IEnumerable<string> Fields() => [];
    }

    private sealed record AndNode(IReadOnlyList<Node> Terms) : Node
    {
        public override bool Evaluate(IFeature feature, EsriSyntheticField? syntheticField)
        {
            foreach (var term in Terms)
            {
                if (!term.Evaluate(feature, syntheticField))
                {
                    return false;
                }
            }

            return true;
        }

        public override string Render() => "(" + string.Join(" AND ", Terms.Select(term => term.Render())) + ")";

        public override IEnumerable<string> Fields() => Terms.SelectMany(term => term.Fields());
    }

    private sealed record OrNode(IReadOnlyList<Node> Terms) : Node
    {
        public override bool Evaluate(IFeature feature, EsriSyntheticField? syntheticField)
        {
            foreach (var term in Terms)
            {
                if (term.Evaluate(feature, syntheticField))
                {
                    return true;
                }
            }

            return false;
        }

        public override string Render() => "(" + string.Join(" OR ", Terms.Select(term => term.Render())) + ")";

        public override IEnumerable<string> Fields() => Terms.SelectMany(term => term.Fields());
    }

    private sealed record IsNullNode(string Field, bool Negated) : Node
    {
        public override bool Evaluate(IFeature feature, EsriSyntheticField? syntheticField) =>
            EsriFilterLogic.FieldValue(feature, Field, syntheticField).IsNull != Negated;

        public override string Render() => $"{Field} IS {(Negated ? "NOT " : string.Empty)}NULL";

        public override IEnumerable<string> Fields() => [Field];
    }

    /// <summary>
    /// A field-independent predicate whose value is fixed when the clause is
    /// parsed, such as the Esri match-all <c>1=1</c> (and <c>1=0</c>). It
    /// references no column, so evaluating it never touches the feature.
    /// </summary>
    private sealed record ConstantNode(bool Value, string Text) : Node
    {
        public override bool Evaluate(IFeature feature, EsriSyntheticField? syntheticField) => Value;

        public override string Render() => Text;
    }

    private sealed record ComparisonNode(string Field, ComparisonOperator Operator, Literal Value) : Node
    {
        public override bool Evaluate(IFeature feature, EsriSyntheticField? syntheticField)
        {
            var attribute = EsriFilterLogic.FieldValue(feature, Field, syntheticField);
            if (attribute.IsNull)
            {
                return false;
            }

            return Operator == ComparisonOperator.Like
                ? EsriFilterLogic.MatchesLike(attribute, Value)
                : EsriFilterLogic.Compare(attribute, Operator, Value);
        }

        public override string Render() => $"{Field} {EsriFilterLogic.OperatorText(Operator)} {Value.Render()}";

        public override IEnumerable<string> Fields() => [Field];
    }

    private enum TokenKind
    {
        Identifier,
        String,
        Number,
        LeftParen,
        RightParen,
        Equals,
        NotEquals,
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual,
        Like,
        Is,
        Not,
        Null,
        And,
        Or,
        True,
        False,
        Timestamp,
        CurrentTimestamp,
        Interval,
        Plus,
        Minus,
        End,
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private static class Lexer
    {
        public static bool TryTokenize(string text, out List<Token> tokens, out string error)
        {
            tokens = [];
            var index = 0;
            while (index < text.Length)
            {
                var character = text[index];
                if (char.IsWhiteSpace(character))
                {
                    index++;
                    continue;
                }

                if (!TryToken(text, ref index, tokens, out error))
                {
                    return false;
                }
            }

            tokens.Add(new Token(TokenKind.End, string.Empty, index));
            error = string.Empty;
            return true;
        }

        private static bool TryToken(string text, ref int index, List<Token> tokens, out string error)
        {
            var start = index;
            var character = text[index];
            if (IsQuote(character))
            {
                return TryQuoted(text, ref index, tokens, out error);
            }

            if (StartsNumber(text, index))
            {
                return TryNumber(text, ref index, tokens, out error);
            }

            if (StartsWord(character))
            {
                return TryWord(text, ref index, tokens, out error);
            }

            if (!TryOperator(text, index, out var kind, out var length))
            {
                error = $"unexpected character '{character}' at position {start}";
                return false;
            }

            tokens.Add(new Token(kind, text.Substring(start, length), start));
            index += length;
            error = string.Empty;
            return true;
        }

        private static bool IsQuote(char character) => character is '\'' or '"';

        private static bool StartsWord(char character) => char.IsLetter(character) || character == '_';

        private static bool StartsNumber(string text, int index) =>
            char.IsDigit(text[index]) || (text[index] == '-' && index + 1 < text.Length && char.IsDigit(text[index + 1]));

        private static bool TryOperator(string text, int index, out TokenKind kind, out int length)
        {
            if (index + 1 < text.Length && TwoCharOperators.TryGetValue(text.Substring(index, 2), out kind))
            {
                length = 2;
                return true;
            }

            if (OneCharOperators.TryGetValue(text[index], out kind))
            {
                length = 1;
                return true;
            }

            length = 0;
            return false;
        }

        private static bool TryQuoted(string text, ref int index, List<Token> tokens, out string error)
        {
            var quote = text[index];
            var start = index;
            var builder = new StringBuilder();
            index++;
            while (index < text.Length)
            {
                if (text[index] == quote)
                {
                    if (Peek(text, index + 1) == quote)
                    {
                        builder.Append(quote);
                        index += 2;
                        continue;
                    }

                    index++;
                    tokens.Add(new Token(quote == '\'' ? TokenKind.String : TokenKind.Identifier, builder.ToString(), start));
                    error = string.Empty;
                    return true;
                }

                builder.Append(text[index]);
                index++;
            }

            error = $"unterminated quoted literal at position {start}";
            return false;
        }

        private static bool TryNumber(string text, ref int index, List<Token> tokens, out string error)
        {
            var start = index;
            if (text[index] == '-')
            {
                index++;
            }

            while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.'))
            {
                index++;
            }

            tokens.Add(new Token(TokenKind.Number, text[start..index], start));
            error = string.Empty;
            return true;
        }

        private static bool TryWord(string text, ref int index, List<Token> tokens, out string error)
        {
            var start = index;
            while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '_' or '.'))
            {
                index++;
            }

            var word = text[start..index];
            var kind = Keywords.GetValueOrDefault(word, TokenKind.Identifier);
            tokens.Add(new Token(kind, word, start));
            error = string.Empty;
            return true;
        }

        private static readonly Dictionary<string, TokenKind> TwoCharOperators = new(StringComparer.Ordinal)
        {
            ["<="] = TokenKind.LessOrEqual,
            ["<>"] = TokenKind.NotEquals,
            [">="] = TokenKind.GreaterOrEqual,
            ["!="] = TokenKind.NotEquals,
        };

        private static readonly Dictionary<char, TokenKind> OneCharOperators = new()
        {
            ['('] = TokenKind.LeftParen,
            [')'] = TokenKind.RightParen,
            ['='] = TokenKind.Equals,
            ['<'] = TokenKind.LessThan,
            ['>'] = TokenKind.GreaterThan,
            // A '-' directly before a digit lexes as a negative number
            // (see StartsNumber); otherwise it is the interval offset below.
            ['+'] = TokenKind.Plus,
            ['-'] = TokenKind.Minus,
        };

        private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AND"] = TokenKind.And,
            ["OR"] = TokenKind.Or,
            ["LIKE"] = TokenKind.Like,
            ["IS"] = TokenKind.Is,
            ["NOT"] = TokenKind.Not,
            ["NULL"] = TokenKind.Null,
            ["TRUE"] = TokenKind.True,
            ["FALSE"] = TokenKind.False,
            ["TIMESTAMP"] = TokenKind.Timestamp,
            ["CURRENT_TIMESTAMP"] = TokenKind.CurrentTimestamp,
            ["INTERVAL"] = TokenKind.Interval,
        };

        private static char Peek(string text, int index) => index < text.Length ? text[index] : '\0';
    }

    private sealed class Parser(IReadOnlyList<Token> tokens)
    {
        private int _index;

        public Token Current => tokens[_index];

        public bool TryExpression(out Node expression, out string error) => TryOr(out expression, out error);

        private bool TryOr(out Node expression, out string error)
        {
            if (!TryAnd(out var first, out error))
            {
                expression = null!;
                return false;
            }

            var terms = new List<Node> { first };
            while (Current.Kind == TokenKind.Or)
            {
                _index++;
                if (!TryAnd(out var next, out error))
                {
                    expression = null!;
                    return false;
                }

                terms.Add(next);
            }

            expression = terms.Count == 1 ? terms[0] : new OrNode(terms);
            return true;
        }

        private bool TryAnd(out Node expression, out string error)
        {
            if (!TryTerm(out var first, out error))
            {
                expression = null!;
                return false;
            }

            var terms = new List<Node> { first };
            while (Current.Kind == TokenKind.And)
            {
                _index++;
                if (!TryTerm(out var next, out error))
                {
                    expression = null!;
                    return false;
                }

                terms.Add(next);
            }

            expression = terms.Count == 1 ? terms[0] : new AndNode(terms);
            return true;
        }

        private bool TryTerm(out Node expression, out string error)
        {
            if (Current.Kind == TokenKind.LeftParen)
            {
                _index++;
                if (!TryOr(out expression, out error))
                {
                    return false;
                }

                if (Current.Kind != TokenKind.RightParen)
                {
                    error = $"expected ')' at position {Current.Position}, found '{Current.Text}'";
                    return false;
                }

                _index++;
                return true;
            }

            return TryComparison(out expression, out error);
        }

        private bool TryComparison(out Node expression, out string error)
        {
            if (Current.Kind is TokenKind.Number or TokenKind.String or TokenKind.True or TokenKind.False or TokenKind.Null
                or TokenKind.Timestamp or TokenKind.CurrentTimestamp)
            {
                return TryConstantComparison(out expression, out error);
            }

            if (Current.Kind != TokenKind.Identifier)
            {
                error = $"expected a field name at position {Current.Position}, found '{Current.Text}'";
                expression = null!;
                return false;
            }

            var field = Current.Text;
            _index++;
            if (Current.Kind == TokenKind.Is)
            {
                return TryIsNull(field, out expression, out error);
            }

            if (!TryOperator(out var comparisonOperator, out error))
            {
                expression = null!;
                return false;
            }

            if (!TryValue(out var literal, out error))
            {
                expression = null!;
                return false;
            }

            expression = new ComparisonNode(field, comparisonOperator, literal);
            return true;
        }

        /// <summary>
        /// Parses a comparison between two literals (for example the Esri
        /// match-all <c>1=1</c>). The value is constant, so it is evaluated
        /// once when parsed rather than per feature.
        /// </summary>
        private bool TryConstantComparison(out Node expression, out string error)
        {
            if (!TryValue(out var left, out error) || !TryOperator(out var comparisonOperator, out error) || !TryValue(out var right, out error))
            {
                expression = null!;
                return false;
            }

            expression = new ConstantNode(
                EsriFilterLogic.EvaluateConstant(left, comparisonOperator, right),
                $"{left.Render()} {EsriFilterLogic.OperatorText(comparisonOperator)} {right.Render()}");
            return true;
        }

        private bool TryIsNull(string field, out Node expression, out string error)
        {
            _index++;
            var negated = false;
            if (Current.Kind == TokenKind.Not)
            {
                negated = true;
                _index++;
            }

            if (Current.Kind != TokenKind.Null)
            {
                error = $"expected NULL after IS at position {Current.Position}, found '{Current.Text}'";
                expression = null!;
                return false;
            }

            _index++;
            expression = new IsNullNode(field, negated);
            error = string.Empty;
            return true;
        }

        private bool TryOperator(out ComparisonOperator comparisonOperator, out string error)
        {
            comparisonOperator = Current.Kind switch
            {
                TokenKind.Equals => ComparisonOperator.Equals,
                TokenKind.NotEquals => ComparisonOperator.NotEquals,
                TokenKind.LessThan => ComparisonOperator.LessThan,
                TokenKind.LessOrEqual => ComparisonOperator.LessOrEqual,
                TokenKind.GreaterThan => ComparisonOperator.GreaterThan,
                TokenKind.GreaterOrEqual => ComparisonOperator.GreaterOrEqual,
                TokenKind.Like => ComparisonOperator.Like,
                _ => (ComparisonOperator)(-1),
            };
            if ((int)comparisonOperator < 0)
            {
                error = $"expected a comparison operator at position {Current.Position}, found '{Current.Text}'";
                return false;
            }

            _index++;
            error = string.Empty;
            return true;
        }

        private bool TryValue(out Literal literal, out string error)
        {
            switch (Current.Kind)
            {
                case TokenKind.String:
                    literal = new Literal(LiteralKind.String, Current.Text, 0, false);
                    error = string.Empty;
                    _index++;
                    return true;
                case TokenKind.Number:
                    if (!double.TryParse(Current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                    {
                        error = $"'{Current.Text}' is not a valid numeric literal at position {Current.Position}";
                        literal = default;
                        return false;
                    }

                    var decimalPoint = Current.Text.Contains('.');
                    literal = new Literal(decimalPoint ? LiteralKind.Decimal : LiteralKind.Integer, null, number, false);
                    error = string.Empty;
                    _index++;
                    return true;
                case TokenKind.True:
                case TokenKind.False:
                    literal = new Literal(LiteralKind.Boolean, null, 0, Current.Kind == TokenKind.True);
                    error = string.Empty;
                    _index++;
                    return true;
                case TokenKind.Null:
                    literal = new Literal(LiteralKind.Null, null, 0, false);
                    error = string.Empty;
                    _index++;
                    return true;
                case TokenKind.Timestamp:
                    return TryTimestampLiteral(out literal, out error);
                case TokenKind.CurrentTimestamp:
                    return TryCurrentTimestamp(out literal, out error);
                default:
                    error = $"expected a literal value at position {Current.Position}, found '{Current.Text}'";
                    literal = default;
                    return false;
            }
        }

        /// <summary>
        /// Parses a <c>TIMESTAMP '…'</c> date-time literal (the Esri where-syntax
        /// for comparing date fields) into epoch milliseconds.
        /// </summary>
        private bool TryTimestampLiteral(out Literal literal, out string error)
        {
            var position = Current.Position;
            _index++;
            if (Current.Kind != TokenKind.String)
            {
                error = $"expected a quoted date-time after TIMESTAMP at position {position}, found '{Current.Text}'";
                literal = default;
                return false;
            }

            if (!TryParseDateTime(Current.Text, out var milliseconds))
            {
                error = $"'{Current.Text}' is not a valid date-time literal at position {Current.Position}";
                literal = default;
                return false;
            }

            literal = new Literal(LiteralKind.DateTime, null, milliseconds, false);
            error = string.Empty;
            _index++;
            return true;
        }

        /// <summary>
        /// Parses <c>CURRENT_TIMESTAMP</c> with optional <c>± INTERVAL n UNIT</c>
        /// offset, evaluated once when parsed.
        /// </summary>
        private bool TryCurrentTimestamp(out Literal literal, out string error)
        {
            var moment = DateTimeOffset.UtcNow;
            _index++;
            if (Current.Kind is TokenKind.Plus or TokenKind.Minus)
            {
                var negative = Current.Kind == TokenKind.Minus;
                _index++;
                if (!TryInterval(negative, ref moment, out error))
                {
                    literal = default;
                    return false;
                }
            }

            literal = new Literal(LiteralKind.DateTime, null, moment.ToUnixTimeMilliseconds(), false);
            error = string.Empty;
            return true;
        }

        private bool TryInterval(bool negative, ref DateTimeOffset moment, out string error)
        {
            if (Current.Kind != TokenKind.Interval)
            {
                error = $"expected INTERVAL after CURRENT_TIMESTAMP at position {Current.Position}, found '{Current.Text}'";
                return false;
            }

            _index++;
            if (Current.Kind != TokenKind.Number || !double.TryParse(Current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
            {
                error = $"expected an INTERVAL amount at position {Current.Position}, found '{Current.Text}'";
                return false;
            }

            _index++;
            if (Current.Kind != TokenKind.Identifier || !IntervalUnits.TryGetValue(Current.Text, out var unit))
            {
                error = $"expected an INTERVAL unit (SECOND, MINUTE, HOUR, DAY, WEEK, MONTH, YEAR) at position {Current.Position}, found '{Current.Text}'";
                return false;
            }

            _index++;
            var offset = unit(amount);
            moment = negative ? moment.Subtract(offset) : moment.Add(offset);
            error = string.Empty;
            return true;
        }

        private static bool TryParseDateTime(string text, out long milliseconds)
        {
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            {
                milliseconds = parsed.ToUnixTimeMilliseconds();
                return true;
            }

            milliseconds = 0;
            return false;
        }

        /// <summary>
        /// The supported <c>INTERVAL</c> units. Months and years are calendar
        /// approximations (30 and 365 days); the grammar names them so a
        /// client request parses, and the approximation is documented here.
        /// </summary>
        private static readonly Dictionary<string, Func<double, TimeSpan>> IntervalUnits = new(StringComparer.OrdinalIgnoreCase)
        {
            ["SECOND"] = TimeSpan.FromSeconds,
            ["SECONDS"] = TimeSpan.FromSeconds,
            ["MINUTE"] = TimeSpan.FromMinutes,
            ["MINUTES"] = TimeSpan.FromMinutes,
            ["HOUR"] = TimeSpan.FromHours,
            ["HOURS"] = TimeSpan.FromHours,
            ["DAY"] = TimeSpan.FromDays,
            ["DAYS"] = TimeSpan.FromDays,
            ["WEEK"] = weeks => TimeSpan.FromDays(7 * weeks),
            ["WEEKS"] = weeks => TimeSpan.FromDays(7 * weeks),
            ["MONTH"] = months => TimeSpan.FromDays(30 * months),
            ["MONTHS"] = months => TimeSpan.FromDays(30 * months),
            ["YEAR"] = years => TimeSpan.FromDays(365 * years),
            ["YEARS"] = years => TimeSpan.FromDays(365 * years),
        };
    }
}

/// <summary>
/// One value a where clause may reference that is not in the feature schema,
/// such as the GeoServices facade's synthetic <c>OBJECTID</c> (ADR-0037).
/// </summary>
public readonly record struct EsriSyntheticField(string Name, AttributeValue Value);
