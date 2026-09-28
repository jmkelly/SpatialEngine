using System.Globalization;
using System.Text;
using Spatial.Core.Features.Query;

namespace Spatial.Esri.Codec;

/// <summary>
/// One parsed Esri <c>where</c> clause (ADR-0035, ADR-0074 §7): the
/// core-typed <see cref="Predicate"/> the clause compiles to plus the fields it
/// references. The Esri grammar is a front-end syntax over the engine's one
/// predicate vocabulary, not a second language and not a feature evaluator —
/// the clause is compiled here and answered by a store, which is what lets the
/// GeoServices paths push an attribute filter down instead of testing every
/// feature in the adapter.
/// </summary>
public sealed record EsriWhere(Predicate? Predicate, IReadOnlyList<string> ReferencedFields)
{
    private static readonly IReadOnlyList<string> NoFields = [];

    /// <summary>The clause that matches every feature (no <c>where</c> sent).</summary>
    public static EsriWhere None { get; } = new(null, NoFields);

    /// <summary>Parses a complete where clause, or reports the offending position.</summary>
    public static bool TryParse(string text, out EsriWhere? where, out string error)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!EsriWhereText.TryCompile(text, out var predicate, out error))
        {
            where = null;
            return false;
        }

        where = new EsriWhere(predicate, predicate!.Fields().Select(field => field.Name).ToArray());
        return true;
    }

    /// <summary>
    /// Conjoins two clauses: both must match. Adjacent conjunctions flatten,
    /// so a service-level query can AND a shared <c>where</c> with a per-layer
    /// <c>layerDefs</c> clause without re-parsing rendered text.
    /// </summary>
    public EsriWhere And(EsriWhere other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (Predicate is null)
        {
            return other;
        }

        if (other.Predicate is null)
        {
            return this;
        }

        var terms = new List<Predicate>();
        terms.AddRange(Predicate is Predicate.Every every ? every.Terms : [Predicate]);
        terms.AddRange(other.Predicate is Predicate.Every otherAnd ? otherAnd.Terms : [other.Predicate]);
        return new EsriWhere(new Predicate.Every(terms), ReferencedFields.Concat(other.ReferencedFields).Distinct(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Renders the clause as an Esri <c>where</c> string (for the consuming provider).</summary>
    public string ToWhere() => Predicate is null ? string.Empty : EsriWhereText.Render(Predicate);
}

/// <summary>
/// The Esri <c>where</c> grammar: the lexer, the parser that compiles the
/// text to a <see cref="Predicate"/>, and the renderer that turns a predicate
/// back into Esri text for the consuming provider. The Esri-only parts of the
/// grammar fold to core values at parse time rather than entering the engine's
/// vocabulary (ADR-0074 §7): <c>TIMESTAMP</c> and
/// <c>CURRENT_TIMESTAMP ± INTERVAL</c> become date-time literals, and a
/// literal-to-literal comparison such as <c>1=1</c> becomes a
/// <see cref="Predicate.Constant"/>.
/// </summary>
public static class EsriWhereText
{
    /// <summary>
    /// Compiles Esri <c>where</c> text to the engine's one predicate
    /// vocabulary, or reports the offending position.
    /// </summary>
    internal static bool TryCompile(string text, out Predicate? predicate, out string error)
    {
        if (!Lexer.TryTokenize(text, out var tokens, out error))
        {
            predicate = null;
            return false;
        }

        var parser = new Parser(tokens);
        if (!parser.TryExpression(out var compiled, out error))
        {
            predicate = null;
            return false;
        }

        if (parser.Current.Kind != TokenKind.End)
        {
            error = $"unexpected '{parser.Current.Text}' at position {parser.Current.Position} (expected the end of the where clause)";
            predicate = null;
            return false;
        }

        predicate = compiled;
        return true;
    }

    /// <summary>Renders a predicate as an Esri <c>where</c> string.</summary>
    public static string Render(Predicate predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);
        return predicate switch
        {
            Predicate.Every every => Group("AND", every.Terms),
            Predicate.Some some => Group("OR", some.Terms),
            Predicate.Constant constant => constant.Value ? "1 = 1" : "1 = 0",
            Predicate.IsNull isNull => $"{isNull.Field.Name} IS {(isNull.Negated ? "NOT " : string.Empty)}NULL",
            Predicate.IsIn isIn => $"{isIn.Field.Name} {(isIn.Negated ? "NOT IN" : "IN")} ({string.Join(", ", isIn.Values.Select(Render))})",
            Predicate.Compare compare => $"{compare.Field.Name} {OperatorText(compare.Operator)} {Render(compare.Value)}",
            // Unreachable while the vocabulary is closed: the base record has a
            // private constructor, so no node outside these six can exist. It
            // reports a future node rather than emitting a broken clause.
            _ => throw EsriInteropException.Invalid($"A {predicate.GetType().Name} has no Esri where-clause shape."),
        };
    }

    /// <summary>The Esri where-clause rendering of a comparison operator.</summary>
    public static string OperatorText(ComparisonOperator comparison) => comparison switch
    {
        ComparisonOperator.Equals => "=",
        ComparisonOperator.NotEquals => "<>",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessOrEqual => "<=",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterOrEqual => ">=",
        ComparisonOperator.Like => "LIKE",
        _ => throw EsriInteropException.Invalid($"Unknown comparison operator {comparison}."),
    };

    /// <summary>The Esri where-clause rendering of a literal.</summary>
    public static string Render(Literal literal) => literal.Kind switch
    {
        LiteralKind.String => "'" + (literal.Text ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
        LiteralKind.Integer => literal.Text ?? string.Empty,
        LiteralKind.Decimal => literal.Number.ToString("R", CultureInfo.InvariantCulture),
        LiteralKind.Boolean => literal.Boolean ? "TRUE" : "FALSE",
        LiteralKind.DateTime => "TIMESTAMP '" + DateTimeOffset
            .FromUnixTimeMilliseconds((long)literal.Number)
            .UtcDateTime
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture) + "'",
        _ => "NULL",
    };

    /// <summary>Parenthesises a group so a nested conjunction or disjunction keeps its precedence.</summary>
    private static string Group(string conjunction, IReadOnlyList<Predicate> terms) =>
        "(" + string.Join($" {conjunction} ", terms.Select(Render)) + ")";

    internal enum TokenKind
    {
        Identifier,
        String,
        Number,
        LeftParen,
        RightParen,
        Comma,
        Equals,
        NotEquals,
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual,
        Like,
        In,
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

    internal readonly record struct Token(TokenKind Kind, string Text, int Position);

    internal static class Lexer
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
            [','] = TokenKind.Comma,
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
            ["IN"] = TokenKind.In,
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

    internal sealed class Parser(IReadOnlyList<Token> tokens)
    {
        private int _index;

        public Token Current => tokens[_index];

        public bool TryExpression(out Predicate expression, out string error) => TryOr(out expression, out error);

        private bool TryOr(out Predicate expression, out string error)
        {
            if (!TryAnd(out var first, out error))
            {
                expression = null!;
                return false;
            }

            var terms = new List<Predicate> { first };
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

            expression = terms.Count == 1 ? terms[0] : new Predicate.Some(terms);
            return true;
        }

        private bool TryAnd(out Predicate expression, out string error)
        {
            if (!TryTerm(out var first, out error))
            {
                expression = null!;
                return false;
            }

            var terms = new List<Predicate> { first };
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

            expression = terms.Count == 1 ? terms[0] : new Predicate.Every(terms);
            return true;
        }

        private bool TryTerm(out Predicate expression, out string error)
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
                    expression = null!;
                    return false;
                }

                _index++;
                return true;
            }

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

            var field = new FieldRef(Current.Text);
            _index++;
            if (Current.Kind == TokenKind.Is)
            {
                return TryIsNull(field, out expression, out error);
            }

            if (IsInAhead())
            {
                return TryIsIn(field, out expression, out error);
            }

            if (!TryOperator(out var comparison, out error))
            {
                expression = null!;
                return false;
            }

            if (!TryValue(out var literal, out error))
            {
                expression = null!;
                return false;
            }

            expression = new Predicate.Compare(field, comparison, literal);
            return true;
        }

        /// <summary>
        /// Parses a comparison between two literals (for example the Esri
        /// match-all <c>1=1</c>). It references no field, so it becomes the
        /// plan's own constant rather than a test of a column.
        /// </summary>
        private bool TryConstantComparison(out Predicate expression, out string error)
        {
            if (!TryValue(out var left, out error)
                || !TryOperator(out var comparison, out error)
                || !TryValue(out var right, out error))
            {
                expression = null!;
                return false;
            }

            expression = new Predicate.Constant(ConstantTruth(left, comparison, right));
            return true;
        }

        /// <summary>Whether the test after the field is a membership test (<c>IN</c> or <c>NOT IN</c>) rather than a comparison.</summary>
        private bool IsInAhead() =>
            Current.Kind == TokenKind.In
            || (Current.Kind == TokenKind.Not && _index + 1 < tokens.Count && tokens[_index + 1].Kind == TokenKind.In);

        private bool TryIsNull(FieldRef field, out Predicate expression, out string error)
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
            expression = new Predicate.IsNull(field, negated);
            error = string.Empty;
            return true;
        }

        private bool TryIsIn(FieldRef field, out Predicate expression, out string error)
        {
            var negated = false;
            if (Current.Kind == TokenKind.Not)
            {
                negated = true;
                _index++;
            }

            _index++;
            if (Current.Kind != TokenKind.LeftParen)
            {
                error = $"expected '(' after IN at position {Current.Position}, found '{Current.Text}'";
                expression = null!;
                return false;
            }

            _index++;
            if (!TryValue(out var first, out error))
            {
                expression = null!;
                return false;
            }

            var values = new List<Literal> { first };
            while (Current.Kind == TokenKind.Comma)
            {
                _index++;
                if (!TryValue(out var next, out error))
                {
                    expression = null!;
                    return false;
                }

                values.Add(next);
            }

            if (Current.Kind != TokenKind.RightParen)
            {
                error = $"expected ')' to close IN at position {Current.Position}, found '{Current.Text}'";
                expression = null!;
                return false;
            }

            _index++;
            expression = new Predicate.IsIn(field, values, negated);
            error = string.Empty;
            return true;
        }

        private bool TryOperator(out ComparisonOperator comparison, out string error)
        {
            comparison = Current.Kind switch
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
            if ((int)comparison < 0)
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
            if (Current.Kind == TokenKind.String)
            {
                literal = Literal.FromText(Current.Text);
                error = string.Empty;
                _index++;
                return true;
            }

            if (Current.Kind == TokenKind.Number)
            {
                return TryNumberLiteral(out literal, out error);
            }

            if (Current.Kind is TokenKind.True or TokenKind.False)
            {
                literal = Literal.FromBoolean(Current.Kind == TokenKind.True);
                error = string.Empty;
                _index++;
                return true;
            }

            if (Current.Kind == TokenKind.Null)
            {
                literal = Literal.Null;
                error = string.Empty;
                _index++;
                return true;
            }

            if (Current.Kind == TokenKind.Timestamp)
            {
                return TryTimestampLiteral(out literal, out error);
            }

            if (Current.Kind == TokenKind.CurrentTimestamp)
            {
                return TryCurrentTimestamp(out literal, out error);
            }

            error = $"expected a literal value at position {Current.Position}, found '{Current.Text}'";
            literal = default;
            return false;
        }

        private bool TryNumberLiteral(out Literal literal, out string error)
        {
            if (!double.TryParse(Current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                error = $"'{Current.Text}' is not a valid numeric literal at position {Current.Position}";
                literal = default;
                return false;
            }

            // A whole number is carried verbatim, so a value wider than a
            // double keeps every digit when it is bound to a store parameter.
            literal = Current.Text.Contains('.') ? Literal.FromNumber(number) : Literal.FromInteger(Current.Text);
            error = string.Empty;
            _index++;
            return true;
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

            if (!DateTimeOffset.TryParse(
                    Current.Text,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                error = $"'{Current.Text}' is not a valid date-time literal at position {Current.Position}";
                literal = default;
                return false;
            }

            literal = Literal.FromMilliseconds(parsed.ToUnixTimeMilliseconds());
            error = string.Empty;
            _index++;
            return true;
        }

        /// <summary>
        /// Parses <c>CURRENT_TIMESTAMP</c> with an optional
        /// <c>± INTERVAL n UNIT</c> offset, folded to one instant when parsed.
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

            literal = Literal.FromMilliseconds(moment.ToUnixTimeMilliseconds());
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
            if (Current.Kind != TokenKind.Number
                || !double.TryParse(Current.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
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

    /// <summary>
    /// The truth of a literal-to-literal comparison. A null operand, a
    /// mismatch of literal kinds, or an operator the kind does not support is
    /// false — the same reading the evaluator and the SQL back ends give.
    /// </summary>
    private static bool ConstantTruth(Literal left, ComparisonOperator comparison, Literal right)
    {
        if (left.Kind == LiteralKind.Null || right.Kind == LiteralKind.Null)
        {
            return false;
        }

        if (left.Kind == LiteralKind.Boolean && right.Kind == LiteralKind.Boolean)
        {
            return comparison switch
            {
                ComparisonOperator.Equals => left.Boolean == right.Boolean,
                ComparisonOperator.NotEquals => left.Boolean != right.Boolean,
                _ => false,
            };
        }

        if (left.Kind == LiteralKind.String && right.Kind == LiteralKind.String)
        {
            return comparison == ComparisonOperator.Like
                ? EsriLikePattern.IsMatch(left.Text ?? string.Empty, right.Text ?? string.Empty)
                : Satisfies(StringComparer.Ordinal.Compare(left.Text, right.Text), comparison);
        }

        return Number(left) is { } first
            && Number(right) is { } second
            && Satisfies(first.CompareTo(second), comparison);
    }

    /// <summary>The numeric value of a literal, when it has one.</summary>
    private static double? Number(Literal literal) => literal.Kind switch
    {
        LiteralKind.Decimal or LiteralKind.DateTime => literal.Number,
        LiteralKind.Integer when double.TryParse(literal.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var integer) => integer,
        _ => null,
    };

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
}

/// <summary>The whole-value <c>LIKE</c> patterns of the where grammar.</summary>
public static class EsriLikePattern
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Whether the value matches the pattern, where <c>%</c> is any run of
    /// characters and <c>_</c> is exactly one. The pattern is anchored at both
    /// ends, so it is a whole-value test rather than a substring search.
    /// </summary>
    public static bool IsMatch(string value, string pattern) =>
        System.Text.RegularExpressions.Regex.IsMatch(value, ToRegex(pattern), System.Text.RegularExpressions.RegexOptions.CultureInvariant, MatchTimeout);

    private static string ToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        foreach (var character in pattern)
        {
            builder.Append(character switch
            {
                '%' => ".*",
                '_' => ".",
                _ => System.Text.RegularExpressions.Regex.Escape(character.ToString()),
            });
        }

        return builder.Append('$').ToString();
    }
}
