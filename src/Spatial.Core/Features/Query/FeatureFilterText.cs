using System.Globalization;
using System.Text;

namespace Spatial.Core.Features.Query;

/// <summary>
/// The attribute-filter text of a feature query, parsed once at the boundary
/// into a <see cref="Predicate"/> (ADR-0074 §3). The text a published surface
/// already sends — <c>GET /api/features/query?filter=</c>, the render
/// pipeline's filter, the ArcGIS REST store's translation — keeps its syntax;
/// what changes is that no store parses it any more, and none of it reaches
/// SQL as text. This is the one parser of the grammar:
///
/// <code>
/// filter      := or EOF
/// or          := and (OR and)*
/// and         := term (AND term)*
/// term        := '(' or ')' | test
/// test        := field op value | field IS [NOT] NULL | field [NOT] IN '(' value (',' value)* ')'
/// op          := = != &lt;&gt; &lt; &lt;= &gt; &gt;= LIKE | ILIKE
/// value       := string | number | TRUE | FALSE | NULL | TIMESTAMP '…'
/// </code>
///
/// <para>
/// <c>ILIKE</c> is the vocabulary's case-folding text comparison (ADR-0132): the
/// same whole-value pattern test as <c>LIKE</c>, over values and patterns
/// folded with the ASCII alphabet. It is spelled here rather than inherited from
/// a server's own case folding, so a pushed filter's answer does not depend on
/// the collation the store's database was created with.
/// </para>
///
/// The grammar is deliberately tiny and closed: an unknown field is rejected
/// later by the store's schema resolution, and every literal becomes a bound
/// parameter, so client text can never become SQL structure (ADR-0028). The
/// Esri <c>where</c> grammar and the OGC CQL subset are separate front-end
/// syntaxes over the same tree (ADR-0074 §7).
/// </summary>
public static class FeatureFilterText
{
    /// <summary>
    /// Parses a complete filter expression into a predicate, or reports the
    /// offending position. Returns false — never a partially built tree — for
    /// any text outside the grammar above.
    /// </summary>
    public static bool TryParse(string text, out Predicate? predicate, out string error)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryTokenize(text, out var tokens, out error))
        {
            predicate = null;
            return false;
        }

        var parser = new Parser(tokens);
        if (!parser.TryExpression(out var parsed, out error))
        {
            predicate = null;
            return false;
        }

        if (parser.Current.Kind != TokenKind.End)
        {
            error = $"unexpected '{parser.Current.Text}' at position {parser.Current.Position} (expected the end of the filter)";
            predicate = null;
            return false;
        }

        predicate = parsed;
        error = string.Empty;
        return true;
    }

    private static bool TryTokenize(string text, out IReadOnlyList<Token> tokens, out string error)
    {
        var tokenizer = new Tokenizer(text);
        return tokenizer.Run(out tokens, out error);
    }

    private sealed class Tokenizer(string text)
    {
        private readonly List<Token> _tokens = [];
        private int _position;

        public bool Run(out IReadOnlyList<Token> tokens, out string error)
        {
            while (_position < text.Length)
            {
                var character = text[_position];
                if (char.IsWhiteSpace(character))
                {
                    _position++;
                    continue;
                }

                if (!TrySymbol() && !TryWord() && !TryString() && !TryNumber())
                {
                    error = $"unexpected character '{character}' at position {_position}";
                    tokens = [];
                    return false;
                }
            }

            _tokens.Add(new Token(TokenKind.End, string.Empty, _position));
            tokens = _tokens;
            error = string.Empty;
            return true;
        }

        private bool TrySymbol()
        {
            var start = _position;
            var kind = SymbolKind(text.AsSpan(_position));
            if (kind is null)
            {
                return false;
            }

            _position += kind.Value is TokenKind.NotEquals or TokenKind.LessOrEqual or TokenKind.GreaterOrEqual ? 2 : 1;
            _tokens.Add(new Token(kind.Value, text[start.._position], start));
            return true;
        }

        /// <summary>Reads the longest two-char symbol match at the front of the span, or null when none starts here.</summary>
        private static TokenKind? SymbolKind(ReadOnlySpan<char> span) =>
            span.IsEmpty ? null : TwoCharSymbol(span) ?? SingleCharSymbol(span);

        private static TokenKind? TwoCharSymbol(ReadOnlySpan<char> span)
        {
            if (span.Length < 2)
            {
                return null;
            }

            return (span[0], span[1]) switch
            {
                ('<', '>') or ('!', '=') => TokenKind.NotEquals,
                ('<', '=') => TokenKind.LessOrEqual,
                ('>', '=') => TokenKind.GreaterOrEqual,
                _ => null,
            };
        }

        private static TokenKind? SingleCharSymbol(ReadOnlySpan<char> span) => span[0] switch
        {
            '(' => TokenKind.LeftParen,
            ')' => TokenKind.RightParen,
            ',' => TokenKind.Comma,
            _ => SingleComparison(span[0]),
        };

        private static TokenKind? SingleComparison(char first) => first switch
        {
            '=' => TokenKind.Equals,
            '<' => TokenKind.LessThan,
            '>' => TokenKind.GreaterThan,
            _ => null,
        };

        private bool TryWord()
        {
            if (!IsIdentStart(text[_position]))
            {
                return false;
            }

            var start = _position;
            _position++;
            while (_position < text.Length && IsIdentPart(text[_position]))
            {
                _position++;
            }

            var word = text[start.._position];
            _tokens.Add(new Token(KeywordKind(word), word, start));
            return true;
        }

        /// <summary>Maps a keyword (case-insensitive) to its token kind; anything else is a field reference.</summary>
        private static TokenKind KeywordKind(string word) =>
            Keywords.TryGetValue(word, out var kind) ? kind : TokenKind.Identifier;

        private static readonly Dictionary<string, TokenKind> Keywords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["AND"] = TokenKind.And,
            ["OR"] = TokenKind.Or,
            ["IS"] = TokenKind.Is,
            ["NOT"] = TokenKind.Not,
            ["NULL"] = TokenKind.Null,
            ["LIKE"] = TokenKind.Like,
            ["ILIKE"] = TokenKind.LikeFolded,
            ["TRUE"] = TokenKind.True,
            ["FALSE"] = TokenKind.False,
            ["IN"] = TokenKind.In,
            ["TIMESTAMP"] = TokenKind.Timestamp,
        };

        private bool TryString()
        {
            if (text[_position] != '\'')
            {
                return false;
            }

            var start = _position;
            var builder = new StringBuilder();
            _position++;
            while (_position < text.Length)
            {
                var character = text[_position];
                if (character == '\'')
                {
                    if (_position + 1 < text.Length && text[_position + 1] == '\'')
                    {
                        builder.Append('\'');
                        _position += 2;
                        continue;
                    }

                    _position++;
                    _tokens.Add(new Token(TokenKind.String, builder.ToString(), start));
                    return true;
                }

                builder.Append(character);
                _position++;
            }

            _tokens.Add(new Token(TokenKind.UnterminatedString, string.Empty, start));
            return true;
        }

        private bool TryNumber()
        {
            var start = _position;
            if (!TryConsumeNumberBody(out _))
            {
                // Nothing numeric here (a bare sign is not a number); reset and let Run report it.
                _position = start;
                return false;
            }

            ConsumeExponent();
            var number = text[start.._position];
            _tokens.Add(new Token(KindOf(number), number, start));
            return true;
        }

        /// <summary>Consumes an optional sign, the integer digits and a decimal fraction; reports whether any digit was seen.</summary>
        private bool TryConsumeNumberBody(out int digitsConsumed)
        {
            ConsumeSign();
            digitsConsumed = ConsumeDigits();
            TryConsumeFraction();
            return digitsConsumed > 0;
        }

        private void ConsumeSign()
        {
            if (text[_position] is '+' or '-')
            {
                _position++;
            }
        }

        private int ConsumeDigits()
        {
            var digits = 0;
            while (_position < text.Length && char.IsAsciiDigit(text[_position]))
            {
                _position++;
                digits++;
            }

            return digits;
        }

        /// <summary>Consumes a decimal fraction when the next character is a digit-led <c>.</c>; consumes nothing otherwise.</summary>
        private bool TryConsumeFraction()
        {
            if (_position + 1 < text.Length && text[_position] == '.' && char.IsAsciiDigit(text[_position + 1]))
            {
                _position++;
                ConsumeDigits();
                return true;
            }

            return false;
        }

        private void ConsumeExponent()
        {
            if (_position >= text.Length || text[_position] is not ('e' or 'E'))
            {
                return;
            }

            var exponentPosition = _position + 1;
            if (exponentPosition < text.Length && text[exponentPosition] is '+' or '-')
            {
                exponentPosition++;
            }

            if (exponentPosition < text.Length && char.IsAsciiDigit(text[exponentPosition]))
            {
                _position = exponentPosition + 1;
                while (_position < text.Length && char.IsAsciiDigit(text[_position]))
                {
                    _position++;
                }
            }
        }

        /// <summary>
        /// Classifies a consumed number literal as a whole number, a fraction
        /// or an invalid number (a value that does not fit a finite double, so
        /// no back end could bind it).
        /// </summary>
        private static TokenKind KindOf(string number) =>
            long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? TokenKind.Integer
                : double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
                    ? TokenKind.Decimal
                    : TokenKind.InvalidNumber;

        private static bool IsIdentStart(char character) => char.IsAsciiLetter(character) || character == '_';

        private static bool IsIdentPart(char character) => char.IsAsciiLetter(character) || character == '_' || char.IsAsciiDigit(character);
    }

    private sealed class Parser(IReadOnlyList<Token> tokens)
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

            if (Current.Kind != TokenKind.Identifier)
            {
                error = $"expected a filter term at position {Current.Position}, found '{Current.Text}'";
                expression = null!;
                return false;
            }

            return TryTest(out expression, out error);
        }

        private bool TryTest(out Predicate expression, out string error)
        {
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

            if (Current.Kind is TokenKind.End or TokenKind.RightParen or TokenKind.And or TokenKind.Or)
            {
                error = $"expected a value after '{Current.Text}' for field '{field.Name}' at position {Current.Position}";
                expression = null!;
                return false;
            }

            if (!TryValue(out var value, out error))
            {
                expression = null!;
                return false;
            }

            expression = new Predicate.Compare(field, comparison, value);
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
            var values = new List<Literal>();
            if (!TryValue(out var first, out error))
            {
                expression = null!;
                return false;
            }

            values.Add(first);
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

            // A membership test containing NULL is unanswerable in SQL
            // (three-valued logic), so the grammar refuses it by name rather
            // than handing a plan no back end can answer the same way.
            if (values.Any(value => value.Kind == LiteralKind.Null))
            {
                error = $"NULL is not a value for IN at position {Current.Position}; test the field with IS NULL instead";
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
            if (Operators.TryGetValue(Current.Kind, out comparison))
            {
                _index++;
                error = string.Empty;
                return true;
            }

            comparison = (ComparisonOperator)(-1);
            error = $"expected a comparison operator at position {Current.Position}, found '{Current.Text}'";
            return false;
        }

        private static readonly Dictionary<TokenKind, ComparisonOperator> Operators = new()
        {
            [TokenKind.Equals] = ComparisonOperator.Equals,
            [TokenKind.NotEquals] = ComparisonOperator.NotEquals,
            [TokenKind.LessThan] = ComparisonOperator.LessThan,
            [TokenKind.LessOrEqual] = ComparisonOperator.LessOrEqual,
            [TokenKind.GreaterThan] = ComparisonOperator.GreaterThan,
            [TokenKind.GreaterOrEqual] = ComparisonOperator.GreaterOrEqual,
            [TokenKind.Like] = ComparisonOperator.Like,
            [TokenKind.LikeFolded] = ComparisonOperator.LikeFolded,
        };

        private bool TryValue(out Literal literal, out string error)
        {
            if (Current.Kind == TokenKind.Timestamp)
            {
                return TryTimestamp(out literal, out error);
            }

            if (Current.Kind == TokenKind.InvalidNumber)
            {
                error = $"'{Current.Text}' is not a valid filter number at position {Current.Position}";
                literal = default;
                return false;
            }

            return TrySimpleValue(out literal, out error);
        }

        private bool TrySimpleValue(out Literal literal, out string error)
        {
            switch (Current.Kind)
            {
                case TokenKind.String:
                    literal = Literal.FromText(Current.Text);
                    break;
                case TokenKind.Integer:
                    literal = Literal.FromInteger(Current.Text);
                    break;
                case TokenKind.Decimal:
                    literal = Literal.FromNumber(double.Parse(Current.Text, NumberStyles.Float, CultureInfo.InvariantCulture));
                    break;
                default:
                    return TryFlagOrNull(out literal, out error);
            }

            _index++;
            error = string.Empty;
            return true;
        }

        private bool TryFlagOrNull(out Literal literal, out string error)
        {
            switch (Current.Kind)
            {
                case TokenKind.True:
                case TokenKind.False:
                    literal = Literal.FromBoolean(Current.Kind == TokenKind.True);
                    break;
                case TokenKind.Null:
                    literal = Literal.Null;
                    break;
                default:
                    error = $"expected a filter value at position {Current.Position}, found '{Current.Text}'";
                    literal = default;
                    return false;
            }

            _index++;
            error = string.Empty;
            return true;
        }

        /// <summary>Reads a <c>TIMESTAMP '…'</c> literal, the date-time form of the filter grammar.</summary>
        private bool TryTimestamp(out Literal literal, out string error)
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
                    out var moment))
            {
                error = $"'{Current.Text}' is not a valid date-time literal at position {Current.Position}";
                literal = default;
                return false;
            }

            literal = Literal.FromMilliseconds(moment.ToUnixTimeMilliseconds());
            error = string.Empty;
            _index++;
            return true;
        }
    }

    private readonly record struct Token(TokenKind Kind, string Text, int Position);

    private enum TokenKind
    {
        Identifier,
        Integer,
        Decimal,
        InvalidNumber,
        String,
        UnterminatedString,
        And,
        Or,
        Is,
        Not,
        Null,
        Like,
        LikeFolded,
        In,
        True,
        False,
        Timestamp,
        LeftParen,
        RightParen,
        Comma,
        Equals,
        NotEquals,
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual,
        End,
    }
}
