namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The collation a pushed-down statement compares a <em>particular</em> text
/// column under (ADR-0098 §3, ADR-0121 §2, ADR-0136): the column's own where it
/// declares one, and the database's where it declares none.
///
/// <para>
/// The database's collation alone answers the question for every column that
/// inherits it — the case for every table this store creates and every one the
/// conformance suite seeds — and getting that case wrong in the other direction
/// costs only a planner step, which is why the answer is read once per store
/// rather than per query. It is not enough for a hand-authored table:
/// <c>"label" text COLLATE "de-x-icu"</c> sorts by the <em>column's</em>
/// collation whatever the database's, so a <c>C</c> database does not by itself
/// make the <c>COLLATE "C"</c> term unnecessary over it. The per-column
/// declarations come from the same catalogue read that discovers the schema
/// (ADR-0122), so they cost nothing per query, and they are held with the
/// description rather than beside it: a collation read for a table whose schema
/// has since changed is an answer to a question nobody asked.
/// </para>
///
/// <para>
/// A column that declares nothing is answered by <see cref="Database"/>, and a
/// database this store could not ask is answered by neither — which is the
/// direction that states the order, because the term is what makes the pushdown
/// the reference's answer (principle 15).
/// </para>
/// </summary>
/// <param name="Database">
/// The collation the database compares text under, as
/// <c>SELECT datcollate</c> reports it, or <c>null</c> when the catalog has none
/// to report.
/// </param>
/// <param name="Columns">
/// The collations the dataset's columns declare themselves, keyed by column
/// name. Only the columns that declare one appear; everything else is the
/// database's.
/// </param>
internal readonly record struct PostgisTextOrder(string? Database, IReadOnlyDictionary<string, string>? Columns)
{
    /// <summary>
    /// The answer a store writes when it knows no collation at all: every text
    /// comparison states the byte order it wants.
    /// </summary>
    public static PostgisTextOrder Locale => new(null, null);

    /// <summary>The answer for a database that already compares text by bytes.</summary>
    public static PostgisTextOrder ByteOrder => new("C", null);

    /// <summary>
    /// Whether a column that compares by bytes still needs the statement to say
    /// so — the column's own collation when it declares one, the database's
    /// when it does not.
    /// </summary>
    public bool NeedsByteOrder(string column) =>
        !PostgisTextCollation.IsByteOrder(Declared(column));

    /// <summary>The collation this column compares under, as far as the store knows it.</summary>
    private string? Declared(string column) =>
        Columns is not null && Columns.TryGetValue(column, out var declared) ? declared : Database;
}
