using System.Security.Cryptography;
using System.Text;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The name of an index this provider creates (ADR-0092). It is derived from
/// the dataset table and the column — never from client text — so a created
/// dataset's indexes are predictable, and it is shortened deterministically
/// when the pair would exceed SQL Server's 128-character identifier limit: the
/// tail is cut and a digest of the full name appended, so two long columns
/// that share a prefix still get distinct, repeatable names instead of a
/// silent server-side truncation collision.
/// </summary>
internal static class SqlServerIndexName
{
    /// <summary>SQL Server truncates identifiers to 128 characters.</summary>
    private const int MaxLength = 128;

    private const int DigestLength = 8;

    /// <summary>The index name for one column of one dataset.</summary>
    public static string For(string table, string column) => Shorten($"ix_{table}_{column}");

    /// <summary>
    /// The deterministic shortener, also used for the engine key's own
    /// constraint name (ADR-0147) — the same 128-character limit, the same
    /// digest.
    /// </summary>
    public static string Shorten(string name)
    {
        if (name.Length <= MaxLength)
        {
            return name;
        }

        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..DigestLength];
        var suffix = $"_{digest}";
        return name[..(MaxLength - suffix.Length)] + suffix;
    }
}
