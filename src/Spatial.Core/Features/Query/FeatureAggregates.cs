namespace Spatial.Core.Features.Query;

/// <summary>
/// The statistics a group reduction can compute (ADR-0074 §6). The set is the
/// one the engine's served statistics surface already computes, plus the
/// envelope of a geometry field (ADR-0120); a store is never asked for a
/// statistic outside it, and a statistic the store's dialect cannot express is
/// evaluated over the rows it fetched, so the answer is the same either way.
/// </summary>
public enum AggregateStatistic
{
    /// <summary>Count of non-null values, or of rows when the field is <c>*</c>.</summary>
    Count = 0,

    /// <summary>Sum of the non-null numeric values.</summary>
    Sum = 1,

    /// <summary>Smallest non-null value under the value ordering.</summary>
    Minimum = 2,

    /// <summary>Largest non-null value under the value ordering.</summary>
    Maximum = 3,

    /// <summary>Arithmetic mean of the non-null numeric values.</summary>
    Average = 4,

    /// <summary>Sample variance of the non-null numeric values (0 for fewer than two values).</summary>
    Variance = 5,

    /// <summary>Sample standard deviation of the non-null numeric values.</summary>
    StdDev = 6,

    /// <summary>Linearly interpolated percentile at rank <c>f × (n − 1)</c>.</summary>
    PercentileContinuous = 7,

    /// <summary>Dataset value at rank <c>ceil(f × n)</c>.</summary>
    PercentileDiscrete = 8,

    /// <summary>
    /// The axis-aligned bounding rectangle of the field's non-null geometry
    /// values (ADR-0120). It is the one statistic whose answer is not a scalar
    /// — it is a rectangle, reported as an <see cref="AttributeKind.Envelope"/>
    /// value — and the one whose whole point is that the rows never have to
    /// cross: a layer's extent is this reduction at the store, not a union
    /// taken over every feature. A group with no non-null geometry is
    /// <c>null</c>, like every other reduction of nothing.
    /// </summary>
    Envelope = 9,
}

/// <summary>
/// One requested statistic of an <see cref="AggregateQuery"/>: the statistic,
/// the field it reduces (<c>*</c> means every row, which makes
/// <see cref="AggregateStatistic.Count"/> a row count), the name the result is
/// reported under, and the percentile parameters. A structural value.
/// </summary>
/// <param name="Statistic">The statistic to compute.</param>
/// <param name="Field">The schema field to reduce, or <c>*</c> to count rows.</param>
/// <param name="ResultName">
/// The name the value is reported under; the field name when <c>null</c>.
/// </param>
/// <param name="Percentile">The percentile fraction in [0, 1]; a percentile statistic without one is <c>0</c>.</param>
/// <param name="PercentileDescending">Whether the percentile ranks in descending order.</param>
public sealed record AggregateSpec(
    AggregateStatistic Statistic,
    string Field,
    string? ResultName = null,
    double? Percentile = null,
    bool PercentileDescending = false)
{
    /// <summary>The field name that means "every row" (a row count).</summary>
    public const string AllFields = "*";

    /// <summary>Whether the statistic reduces every row rather than one field's values.</summary>
    public bool IsRowCount => Statistic == AggregateStatistic.Count && Field == AllFields;

    /// <summary>The percentile fraction, defaulting to zero.</summary>
    public double PercentileFraction => Percentile ?? 0;

    /// <summary>The name the result is reported under.</summary>
    public string Name => ResultName ?? Field;
}

/// <summary>
/// A grouped reduction of the features a plan selects (ADR-0074 §6): the
/// statistics to compute over each group, the fields the groups are keyed
/// by (no grouping is one group over the whole match set), the <c>having</c>
/// clause over the reduced groups and the page over the groups that survive it
/// (ADR-0128).
/// </summary>
/// <param name="Specs">The statistics to compute, in the order they are reported.</param>
/// <param name="GroupBy">The schema fields to group by, or <c>null</c> for no grouping.</param>
/// <param name="Having">
/// The clause the reduced groups must satisfy, or <c>null</c> for all of them.
/// A filter over the <em>group row</em> — the group key's values followed by
/// one value per statistic, under the name each result is reported under — and
/// not over the features the groups were reduced from: it runs <em>after</em>
/// the reduction, which is what makes <c>SUM(population) &gt; 150</c> a
/// question about a group rather than a filter the rows must pass. The clause
/// is the one predicate vocabulary, so every name in it is validated against
/// this query's group fields and result names before a store is asked.
/// </param>
/// <param name="Limit">
/// The maximum number of groups to return, or <c>null</c> for every group that
/// satisfies <paramref name="Having"/>. This is a page over <em>groups</em>,
/// and it is a member of the reduction rather than of the plan for the same
/// reason the statistic is: a cap the plan carried would cut rows the store
/// never grouped (ADR-0098 §7 as amended by SpatialEngine-u2x.9.2, ADR-0128).
/// </param>
/// <param name="Offset">The number of surviving groups to skip, or <c>null</c> for the first.</param>
public sealed record AggregateQuery(
    IReadOnlyList<AggregateSpec> Specs,
    IReadOnlyList<string>? GroupBy = null,
    Predicate? Having = null,
    int? Limit = null,
    int? Offset = null)
{
    /// <summary>Whether the reduction groups at all.</summary>
    public bool IsGrouped => GroupBy is { Count: > 0 };

    /// <summary>Whether the request asks for a page of the groups rather than all of them.</summary>
    public bool IsPaged => Limit is not null || Offset is not null;

    /// <summary>
    /// The names a <see cref="Having"/> clause may use, ordinal: the group
    /// fields and then the result names, in the order the group row carries
    /// them. The clause is a predicate over the reduced group, so this is the
    /// only vocabulary it has.
    /// </summary>
    public IReadOnlyList<string> GroupRowFields =>
        [.. (GroupBy ?? (IReadOnlyList<string>)[]), .. Specs.Select(spec => spec.Name)];
}

/// <summary>
/// One reduced group: the group key's values (empty when the query does not
/// group) followed by one value per requested statistic, in
/// <see cref="AggregateQuery.Specs"/> order. Nulls are ordinary values, and
/// the reference semantics for a statistic with no non-null input is a null —
/// never a zero.
/// </summary>
public sealed record AggregateGroup(IReadOnlyList<AttributeValue> Key, IReadOnlyList<AttributeValue> Values);

/// <summary>
/// The result of an <see cref="AggregateQuery"/>: the group field names, the
/// result names of the statistics, the reduced groups in the order the plan
/// asked for, the total group count when it is computed and whether more
/// groups remain beyond this page (ADR-0128).
/// </summary>
/// <param name="GroupFields">The group key field names, or empty when the query does not group.</param>
/// <param name="ValueNames">The result name of each statistic, in specification order.</param>
/// <param name="Groups">
/// The reduced groups — the page of them, when the query asked for one. A
/// group set cut by <see cref="AggregateQuery.Limit"/> and
/// <see cref="AggregateQuery.Offset"/> is reported here, and a store that
/// pushed the page down is answering the same question as one that cut the
/// page over every group it returned.
/// </param>
/// <param name="TotalCount">
/// The number of groups the query produces, before the page, or <c>null</c>
/// when not computed. It is the <em>whole</em> group set, so a paged
/// reduction reports the groups it did not return too; paging itself is
/// reported by <paramref name="HasMore"/>, which a store can answer from the
/// one row past the page it asked for.
/// </param>
/// <param name="HasMore">
/// Whether groups remain beyond the ones returned. An unpaged reduction is
/// never paged and so is never <c>true</c>, whatever it does not report.
/// </param>
public sealed record AggregatePage(
    IReadOnlyList<string> GroupFields,
    IReadOnlyList<string> ValueNames,
    IReadOnlyList<AggregateGroup> Groups,
    int? TotalCount = null,
    bool HasMore = false);

/// <summary>
/// The deduplicated field combinations a plan selects (ADR-0074 §6): which
/// fields make up a combination. A field list of one is a value domain; more
/// than one is a combination domain.
/// </summary>
/// <param name="Fields">The schema fields a row is made of, in order.</param>
public sealed record DistinctQuery(IReadOnlyList<string> Fields)
{
    /// <summary>Whether the request deduplicates combinations of several fields.</summary>
    public bool IsCombination => Fields.Count > 1;
}

/// <summary>
/// The result of a <see cref="DistinctQuery"/>: the field names each row is
/// made of, the deduplicated rows in first-seen order, and the row count when
/// it is known.
/// </summary>
/// <param name="Fields">The field names each row is made of, in order.</param>
/// <param name="Rows">The deduplicated rows, each one value per requested field.</param>
/// <param name="TotalCount">The number of distinct rows, or <c>null</c> when not computed.</param>
public sealed record DistinctPage(
    IReadOnlyList<string> Fields,
    IReadOnlyList<IReadOnlyList<AttributeValue>> Rows,
    int? TotalCount = null);
