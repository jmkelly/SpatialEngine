using System.Security.Cryptography;
using System.Text;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The name of an index this provider creates (ADR-0081). It is derived from
/// the dataset table and the column — never from client text — so a created
/// dataset's indexes are predictable, and it is shortened deterministically
/// when the pair would exceed PostgreSQL's 63-byte identifier limit: the tail
/// is cut and a digest of the full name appended, so two long columns that
/// share a prefix still get distinct, repeatable names instead of a silent
/// server-side truncation collision.
/// </summary>
internal static class PostgisIndexName
{
    /// <summary>PostgreSQL truncates identifiers to <c>NAMEDATALEN - 1</c> bytes.</summary>
    private const int MaxBytes = 63;

    private const int DigestBytes = 4;

    /// <summary>The index name for one column of one dataset.</summary>
    public static string For(string table, string column) => Shorten($"ix_{table}_{column}");

    private static string Shorten(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxBytes)
        {
            return name;
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..(DigestBytes * 2)].ToLowerInvariant();
        var suffix = $"_{digest}";
        return Truncate(name, MaxBytes - suffix.Length) + suffix;
    }

    /// <summary>Cuts a name to a byte budget, never splitting a UTF-8 sequence.</summary>
    private static string Truncate(string name, int bytes)
    {
        var length = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var size = Encoding.UTF8.GetByteCount(name[i].ToString());
            if (length + size > bytes)
            {
                return name[..i];
            }

            length += size;
        }

        return name;
    }
}
