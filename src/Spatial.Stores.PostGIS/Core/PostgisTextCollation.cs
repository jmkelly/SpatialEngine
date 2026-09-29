namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// Whether a text sort key pushed into this database's <c>ORDER BY</c> is
/// compared by bytes or by the database's own locale collation (ADR-0098 §3,
/// ADR-0121).
///
/// <para>
/// The contract compares strings <em>ordinally</em>, which is a byte
/// comparison: <c>"A"</c> before <c>"a"</c>, and <c>"_c"</c> between the
/// upper- and the lower-case letters. Postgres's <c>ORDER BY</c> says nothing
/// about that — a <c>text</c> column carries the database's default collation,
/// and <c>en_US.utf8</c> orders the very same rows <c>a, a, A, A, _c</c>. Both
/// are correct answers to their own question, and only one of them is the
/// contract's, so a pushed-down sort key over a text column carries an explicit
/// <c>COLLATE "C"</c> unless the database already compares by bytes.
/// </para>
///
/// <para>
/// The term is skipped when the database is already <c>C</c>/<c>POSIX</c>
/// because the sort is then the reference's sort and the term would only cost a
/// planner that can no longer use a default-collation index. When the database
/// is a locale collation the term is also what a btree index on that column
/// cannot serve, so the pushdown trades an index seek for the right answer; the
/// answer is the part that is not negotiable (principle 15).
/// </para>
/// </summary>
internal static class PostgisTextCollation
{
    /// <summary>The byte-order collation, as it is written into an <c>ORDER BY</c>.</summary>
    public const string ByteOrder = "\"C\"";

    /// <summary>
    /// Whether a database created with <paramref name="collation"/> already
    /// compares text by bytes, in which case a sort key needs no term of its
    /// own. A collation this store has not read — a probe that failed, or a
    /// database that reported none — is answered <c>false</c>, because adding
    /// <c>COLLATE "C"</c> to a sort that was already correct costs a planner
    /// step, while omitting it from one that was not costs the answer.
    /// </summary>
    public static bool IsByteOrder(string? collation)
    {
        if (string.IsNullOrWhiteSpace(collation))
        {
            return false;
        }

        var name = collation.Trim();
        return name.Equals("C", StringComparison.OrdinalIgnoreCase)
            || name.Equals("POSIX", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("C.", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("C_", StringComparison.OrdinalIgnoreCase);
    }
}
