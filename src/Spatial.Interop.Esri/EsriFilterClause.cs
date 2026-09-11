using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    public bool Matches(IFeature feature)
    {
        ArgumentNullException.ThrowIfNull(feature);
        return _root.Evaluate(feature);
    }

    /// <summary>Renders the clause as an Esri <c>where</c> string (for the consuming provider).</summary>
    public string ToWhere() => _root.Render();

    private abstract record Node
    {
        public abstract bool Evaluate(IFeature feature);

        public abstract string Render();
    }

    private sealed record AndNode(IReadOnlyList<Node> Terms) : Node
    {
        public override bool Evaluate(IFeature feature)
        {
            foreach (var term in Terms)
            {
                if (!term.Evaluate(feature))
                {
                    return false;
                }
            }

            return true;
        }

        public override string Render() => "(" + string.Join(" AND ", Terms.Select(term => term.Render())) + ")";
    }

    private sealed record OrNode(IReadOnlyList<Node> Terms) : Node
    {
        public override bool Evaluate(IFeature feature)
        {
            foreach (var term in Terms)
            {
                if (term.Evaluate(feature))
                {
                    return true;
                }
            }

            return false;
        }

        public override string Render() => "(" + string.Join(" OR ", Terms.Select(term => term.Render())) + ")";
    }

    private sealed record IsNullNode(string Field, bool Negated) : Node
    {
        public override bool Evaluate(IFeature feature) => Attribute(feature, Field).IsNull != Negated;

        public override string Render() => $"{Field} IS {(Negated ? "NOT " : string.Empty)}NULL";
    }

    private sealed record ComparisonNode(string Field, ComparisonOperator Operator, Literal Value) : Node
    {
        public override bool Evaluate(IFeature feature)
        {
            var attribute = Attribute(feature, Field);
            if (attribute.IsNull)
            {
                return false;
            }

            return Operator == ComparisonOperator.Like
                ? MatchesLike(attribute, Value)
                : Compare(attribute, Operator, Value);
        }

        public override string Render() => $"{Field} {OperatorText(Operator)} {Value.Render()}";
    }

    private static string OperatorText(ComparisonOperator comparisonOperator) => comparisonOperator switch
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

    private static AttributeValue Attribute(IFeature feature, string field)
    {
        var index = feature.Schema.IndexOf(field);
        if (index < 0)
        {
            throw EsriInteropException.Invalid($"The where clause names unknown field '{field}'.");
        }

        return feature[index];
    }

    private static bool Compare(AttributeValue attribute, ComparisonOperator comparisonOperator, Literal literal)
    {
        if (literal.Kind == LiteralKind.Null)
        {
            return false;
        }

        return attribute.Kind switch
        {
            AttributeKind.Int64 => CompareNumber(attribute.Int64Value, literal, comparisonOperator),
            AttributeKind.Double => CompareNumber(attribute.DoubleValue, literal, comparisonOperator),
            AttributeKind.String => CompareStrings(attribute.StringValue, literal, comparisonOperator),
            AttributeKind.Boolean => CompareBoolean(attribute.BooleanValue, literal, comparisonOperator),
            AttributeKind.DateTimeOffset => CompareNumber(attribute.DateTimeOffsetValue.ToUnixTimeMilliseconds(), literal, comparisonOperator),
            AttributeKind.Guid => CompareGuid(attribute.GuidValue, literal, comparisonOperator),
            _ => false,
        };
    }

    private static bool CompareNumber(double left, Literal literal, ComparisonOperator comparisonOperator)
    {
        if (literal.Kind is not (LiteralKind.Integer or LiteralKind.Decimal))
        {
            return false;
        }

        var right = literal.Number;
        return comparisonOperator switch
        {
            ComparisonOperator.Equals => left == right,
            ComparisonOperator.NotEquals => left != right,
            ComparisonOperator.LessThan => left < right,
            ComparisonOperator.LessOrEqual => left <= right,
            ComparisonOperator.GreaterThan => left > right,
            ComparisonOperator.GreaterOrEqual => left >= right,
            _ => false,
        };
    }

    private static bool CompareStrings(string left, Literal literal, ComparisonOperator comparisonOperator)
    {
        if (literal.Kind != LiteralKind.String)
        {
            return false;
        }

        var comparison = StringComparer.Ordinal.Compare(left, literal.Text);
        return comparisonOperator switch
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

    private static bool MatchesLike(AttributeValue attribute, Literal literal)
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

    private enum ComparisonOperator
    {
        Equals,
        NotEquals,
        LessThan,
        LessOrEqual,
        GreaterThan,
        GreaterOrEqual,
        Like,
    }

    private enum LiteralKind
    {
        String,
        Integer,
        Decimal,
        Boolean,
        Null,
    }

    private readonly record struct Literal(LiteralKind Kind, string? Text, double Number, bool Boolean)
    {
        public string Render() => Kind switch
        {
            LiteralKind.String => "'" + (Text ?? string.Empty).Replace("'", "''", StringComparison.Ordinal) + "'",
            LiteralKind.Integer => ((long)Number).ToString(CultureInfo.InvariantCulture),
            LiteralKind.Decimal => Number.ToString("R", CultureInfo.InvariantCulture),
            LiteralKind.Boolean => Boolean ? "TRUE" : "FALSE",
            _ => "NULL",
        };
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
                default:
                    error = $"expected a literal value at position {Current.Position}, found '{Current.Text}'";
                    literal = default;
                    return false;
            }
        }
    }
}
