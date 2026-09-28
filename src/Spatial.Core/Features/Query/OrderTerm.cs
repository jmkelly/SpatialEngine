namespace Spatial.Core.Features.Query;

/// <summary>
/// The direction of one <see cref="OrderTerm"/>.
/// </summary>
public enum SortDirection
{
    /// <summary>Ascending; null values sort last.</summary>
    Ascending = 0,

    /// <summary>Descending; null values sort first (the exact reverse of ascending).</summary>
    Descending = 1,
}

/// <summary>
/// One requested sort key of a feature-read plan (ADR-0074 §5): a field name
/// over the dataset schema and a direction. A structural value — no provider
/// or protocol vocabulary crosses with it.
///
/// <para>
/// The ordering a plan asks for is only a <em>preference</em> until the
/// contract's tie-break rule is applied: a store appends the feature identity
/// as a final ascending key to any <see cref="OrderTerm"/> list, so the total
/// order is deterministic and <c>Offset</c> and cursors are stable. Two
/// features whose requested keys tie are therefore always separated by
/// identity, and both the reference executor and a pushed-down
/// <c>ORDER BY</c> must agree on which one comes first (the conformance suite
/// compares the whole sequence, not the key values).
/// </para>
/// </summary>
public sealed record OrderTerm(string Field, SortDirection Direction = SortDirection.Ascending)
{
    /// <summary>Whether the term sorts descending.</summary>
    public bool IsDescending => Direction == SortDirection.Descending;
}
