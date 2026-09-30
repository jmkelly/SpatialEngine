namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// Whether a text sort key pushed into this database's <c>ORDER BY</c> is
/// compared by code point or by the column's own collation (ADR-0098 §3,
/// ADR-0121, ADR-0124).
///
/// <para>
/// The contract compares strings <em>ordinally</em> — a byte order, in
/// .NET's case the UTF-16 code-unit order <c>string.CompareOrdinal</c>
/// gives — and SQL Server says nothing about that. A <c>varchar</c>/
/// <c>nvarchar</c> column carries the database default collation unless it
/// declares its own, and the collations SQL Server ships by default
/// (<c>SQL_Latin1_General_CP1_CI_AS</c>) are a case-insensitive locale
/// comparison: they fold <c>"A"</c> onto <c>"a"</c> and sort punctuation
/// where its letters sort, so the very same rows come back
/// <c>a, A, _c</c> where the contract says <c>A, _c, a</c>. A pushed text
/// sort key is therefore read under <see cref="ByteOrder"/> unless the
/// database already compares by code point.
/// </para>
///
/// <para>
/// Only a <c>_BIN2</c> collation counts as that. <c>_BIN</c> is the same order
/// for code points that fit the collation's single-byte code page and a
/// different one for everything else; a <c>_UTF8</c> collation sorts by UTF-8
/// code point, which puts a supplementary character after <c>U+FFFF</c> where
/// an ordinal comparison of UTF-16 code units puts it before. The two
/// remaining directions are not symmetric, and this is the one that keeps the
/// answer: a collation the probe cannot vouch for is treated as a locale
/// collation and the term is written.
/// </para>
/// </summary>
internal static class SqlServerTextCollation
{
    /// <summary>
    /// The code-point collation a text sort key is read under, as it is written
    /// into an <c>ORDER BY</c>. <c>BIN2</c> is the Unicode-aware binary
    /// collation every supported SQL Server version ships, and the one
    /// ADR-0121 named for this argument.
    /// </summary>
    public const string ByteOrder = "Latin1_General_100_BIN2";

    /// <summary>
    /// Whether a database whose default collation is <paramref name="collation"/>
    /// already compares text by code point, in which case a sort key needs no
    /// term of its own. A collation this store has not read — a probe that
    /// failed, or a database that reported none — is answered <c>false</c>,
    /// because adding the term to a sort that was already correct costs a
    /// planner step, while omitting it from one that was not costs the answer.
    /// </summary>
    public static bool IsByteOrder(string? collation) =>
        !string.IsNullOrWhiteSpace(collation)
        && collation.Trim().EndsWith("_BIN2", StringComparison.OrdinalIgnoreCase);
}
