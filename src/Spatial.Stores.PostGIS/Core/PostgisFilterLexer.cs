using System.Globalization;
using System.Text;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The tokenizer of the attribute filter language (ADR-0028): identifiers,
/// numbers (integers and decimals, signed, with exponents), single-quoted
/// strings (<c>''</c> escapes a quote), the symbols
/// <c>( ) = != &lt;&gt; &lt; &lt;= &gt; &gt;=</c> and the keywords
/// <c>AND OR IS NOT NULL LIKE TRUE FALSE</c> (case-insensitive). Every token
/// carries its position so parser errors can name the exact offset.
/// </summary>
internal static class PostgisFilterLexer
{
    /// <summary>Tokenizes the whole text, or fails with an actionable message.</summary>
    public static bool TryTokenize(string text, out IReadOnlyList<FilterToken> tokens, out string error)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokenizer = new Tokenizer(text);
        return tokenizer.Run(out tokens, out error);
    }

    private sealed class Tokenizer(string text)
    {
        private readonly List<FilterToken> _tokens = [];
        private int _position;

        public bool Run(out IReadOnlyList<FilterToken> tokens, out string error)
        {
            while (_position < text.Length)
            {
                var c = text[_position];
                if (char.IsWhiteSpace(c))
                {
                    _position++;
                    continue;
                }

                if (!TrySymbol() && !TryWord() && !TryString() && !TryNumber())
                {
                    error = $"unexpected character '{c}' at position {_position}";
                    tokens = [];
                    return false;
                }
            }

            _tokens.Add(new FilterToken(FilterTokenKind.End, string.Empty, _position));
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

            _position += kind.Value is FilterTokenKind.NotEquals or FilterTokenKind.LessOrEqual or FilterTokenKind.GreaterOrEqual ? 2 : 1;
            _tokens.Add(new FilterToken(kind.Value, text[start.._position], start));
            return true;
        }

        /// <summary>Reads the longest two-char symbol match at the front of the span, or null when none starts here.</summary>
        private static FilterTokenKind? SymbolKind(ReadOnlySpan<char> span) => span switch
        {
            ['(', ..] => FilterTokenKind.LeftParen,
            [')', ..] => FilterTokenKind.RightParen,
            ['=', ..] => FilterTokenKind.Equals,
            ['<', '>', ..] or ['!', '=', ..] => FilterTokenKind.NotEquals,
            ['<', '=', ..] => FilterTokenKind.LessOrEqual,
            ['>', '=', ..] => FilterTokenKind.GreaterOrEqual,
            ['<', ..] => FilterTokenKind.LessThan,
            ['>', ..] => FilterTokenKind.GreaterThan,
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
            _tokens.Add(new FilterToken(KeywordKind(word), word, start));
            return true;
        }

        /// <summary>Maps a keyword (case-insensitive) to its token kind; anything else is an identifier.</summary>
        private static FilterTokenKind KeywordKind(string word) => word.ToUpperInvariant() switch
        {
            "AND" => FilterTokenKind.And,
            "OR" => FilterTokenKind.Or,
            "IS" => FilterTokenKind.Is,
            "NOT" => FilterTokenKind.Not,
            "NULL" => FilterTokenKind.Null,
            "LIKE" => FilterTokenKind.Like,
            "TRUE" => FilterTokenKind.True,
            "FALSE" => FilterTokenKind.False,
            _ => FilterTokenKind.Identifier,
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
                var c = text[_position];
                if (c == '\'')
                {
                    if (_position + 1 < text.Length && text[_position + 1] == '\'')
                    {
                        builder.Append('\'');
                        _position += 2;
                        continue;
                    }

                    _position++;
                    _tokens.Add(new FilterToken(FilterTokenKind.String, builder.ToString(), start));
                    return true;
                }

                builder.Append(c);
                _position++;
            }

            _tokens.Add(new FilterToken(FilterTokenKind.UnterminatedString, string.Empty, start));
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
            _tokens.Add(new FilterToken(KindOf(number), number, start));
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

        /// <summary>Consumes an optional leading <c>+</c>/<c>-</c> sign.</summary>
        private void ConsumeSign()
        {
            if (text[_position] is '+' or '-')
            {
                _position++;
            }
        }

        /// <summary>Consumes consecutive ASCII digits and returns how many were consumed.</summary>
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

        /// <summary>Consumes an optional <c>e</c>/<c>E</c> exponent with a sign and digits when present.</summary>
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

        /// <summary>Classifies a consumed number literal as integer, decimal or invalid.</summary>
        private static FilterTokenKind KindOf(string number) =>
            long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? FilterTokenKind.Integer
                : double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
                    ? FilterTokenKind.Decimal
                    : FilterTokenKind.InvalidNumber;

        private static bool IsIdentStart(char c) => char.IsAsciiLetter(c) || c == '_';

        private static bool IsIdentPart(char c) => char.IsAsciiLetter(c) || char.IsAsciiDigit(c) || c == '_';
    }
}

internal readonly record struct FilterToken(FilterTokenKind Kind, string Text, int Position);

internal enum FilterTokenKind
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
    True,
    False,
    LeftParen,
    RightParen,
    Equals,
    NotEquals,
    LessThan,
    LessOrEqual,
    GreaterThan,
    GreaterOrEqual,
    End,
}
