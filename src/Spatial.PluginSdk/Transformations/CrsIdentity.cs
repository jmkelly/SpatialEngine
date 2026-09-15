namespace Spatial.PluginSdk.Transformations;

/// <summary>
/// Identity of a coordinate reference system as carried by the
/// transformation contracts (ADR-0027): an authority plus a code, written
/// as <c>EPSG:4326</c>. Same shape as the core value model's
/// <c>CoordinateReference</c> identity (ADR-0009) but expressed as
/// wire-legal contract values — the CRS describe and coordinate transform verbs take identity
/// strings, parsed by this type.
/// </summary>
public readonly record struct CrsIdentity(string Authority, string Code)
{
    /// <summary>
    /// Parses an identity string (<c>EPSG:4326</c>). Returns false for null,
    /// empty, malformed or unbounded input; the provider then fails with
    /// <c>invalid.arguments</c> naming the value.
    /// </summary>
    public static bool TryParse(string? text, out CrsIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var separator = text.IndexOf(':');
        if (!IsValidSeparator(separator, text.Length))
        {
            return false;
        }

        var authority = text.AsSpan(0, separator);
        var code = text.AsSpan(separator + 1);
        if (!IsValidToken(authority) || !IsValidToken(code))
        {
            return false;
        }

        identity = new CrsIdentity(authority.ToString(), code.ToString());
        return true;
    }

    /// <summary>Parses an identity string, throwing <see cref="FormatException"/> when invalid.</summary>
    public static CrsIdentity Parse(string text)
    {
        if (!TryParse(text, out var identity))
        {
            throw new FormatException($"'{text}' is not a valid CRS identity; expected authority:code, for example EPSG:4326.");
        }

        return identity;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Authority}:{Code}";

    private static bool IsValidSeparator(int separator, int length) =>
        separator > 0 && separator < length - 1;

    private static bool IsValidToken(ReadOnlySpan<char> token) =>
        token.Length is > 0 and <= 64 && IsTokenText(token);

    private static bool IsTokenText(ReadOnlySpan<char> token)
    {
        foreach (var character in token)
        {
            if (!IsTokenCharacter(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTokenCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-';
}
