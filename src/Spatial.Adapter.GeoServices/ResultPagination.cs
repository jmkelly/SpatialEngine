using System.Text.Json;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The opaque <c>resultPaginationToken</c> workflow (spec §9.1.4, S3): a
/// page that fills up returns a token for the next page; the client repeats
/// the identical query with the token instead of <c>resultOffset</c>. The
/// engine materialises the deterministic ordered match set per request, so
/// the token is a versioned offset cursor into that set — behaviourally the
/// keyset continuation S3 describes, without server-side paging state. A
/// token is only valid with the query that minted it; anything else is a
/// typed invalid-argument failure that restarts paging from the first page.
/// </summary>
internal static class ResultPagination
{
    private const int Version = 1;

    private sealed record Token(int V, int Offset);

    /// <summary>Encodes the next page's start offset as an opaque URL-safe token.</summary>
    public static string Encode(int offset)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new Token(Version, offset));
        return Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Decodes a token back to its start offset, or a typed invalid-argument failure.</summary>
    public static int Decode(string token)
    {
        if (TryDecode(token, out var offset))
        {
            return offset;
        }

        throw EsriInteropException.Invalid(
            "The 'resultPaginationToken' is invalid or expired; re-run the query without it to restart paging.");
    }

    private static bool TryDecode(string token, out int offset)
    {
        offset = 0;
        try
        {
            var restored = token.Replace('-', '+').Replace('_', '/');
            var padding = restored.Length % 4;
            if (padding == 1)
            {
                return false;
            }

            if (padding > 0)
            {
                restored = restored.PadRight(restored.Length + (4 - padding), '=');
            }

            var decoded = JsonSerializer.Deserialize<Token>(Convert.FromBase64String(restored));
            if (decoded is { V: Version, Offset: >= 0 })
            {
                offset = decoded.Offset;
                return true;
            }
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            return false;
        }

        return false;
    }
}
