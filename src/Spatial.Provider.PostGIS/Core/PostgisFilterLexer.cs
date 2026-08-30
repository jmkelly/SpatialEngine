using System.Globalization;
using System.Text;

namespace Spatial.Provider.PostGIS.Core;

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
            var span = text.AsSpan(_position);
            var kind = span switch
            {
                ['(', ..] => FilterTokenKind.LeftParen,
                [')', ..] => FilterTokenKind.RightParen,
                ['=', ..] => FilterTokenKind.Equals,
                ['<', '>', ..] => FilterTokenKind.NotEquals,
                ['!', '=', ..] => FilterTokenKind.NotEquals,
                ['<', '=', ..] => FilterTokenKind.LessOrEqual,
                ['>', '=', ..] => FilterTokenKind.GreaterOrEqual,
                ['<', ..] => FilterTokenKind.LessThan,
                ['>', ..] => FilterTokenKind.GreaterThan,
                _ => (FilterTokenKind?)null,
            };
            if (kind is null)
            {
                return false;
            }

            _position += kind is FilterTokenKind.NotEquals or FilterTokenKind.LessOrEqual or FilterTokenKind.GreaterOrEqual ? 2 : 1;
            _tokens.Add(new FilterToken(kind.Value, text[start.._position], start));
            return true;
        }

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
            var kind = word.ToUpperInvariant() switch
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
            _tokens.Add(new FilterToken(kind, word, start));
            return true;
        }

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
            if (text[_position] is '+' or '-')
            {
                _position++;
            }

            var digitsConsumed = 0;
            while (_position < text.Length && char.IsAsciiDigit(text[_position]))
            {
                _position++;
                digitsConsumed++;
            }

            if (_position < text.Length && text[_position] == '.'
                && _position + 1 < text.Length && char.IsAsciiDigit(text[_position + 1]))
            {
                _position++;
                while (_position < text.Length && char.IsAsciiDigit(text[_position]))
                {
                    _position++;
                }
            }

            if (digitsConsumed == 0)
            {
                // Nothing numeric here (a bare sign is not a number); reset and let Run report it.
                _position = start;
                return false;
            }

            if (_position < text.Length && text[_position] is 'e' or 'E')
            {
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

            var number = text[start.._position];
            var kind = long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
                ? FilterTokenKind.Integer
                : double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
                    ? FilterTokenKind.Decimal
                    : FilterTokenKind.InvalidNumber;
            _tokens.Add(new FilterToken(kind, number, start));
            return true;
        }

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
