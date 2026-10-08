using System.Globalization;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The pure row-to-page mapping for a pushed grouped reduction: every helper
/// here is dialect-free (only <see cref="PostgisRowMapper"/> names the
/// dialect), so the reduction reader stays an orchestrator.
/// </summary>
internal static class PostgisGroupPage
{
    /// <summary>
    /// The reduction over an empty row set: a paged reduction that returned
    /// nothing is past the last group, and how many groups there were is a
    /// count the cap just cut, so the total is the store's to leave uncomputed
    /// (ADR-0128). An *ungrouped* reduction is one group, and an offset that
    /// lands past it is past the last group too: the reference pages the one
    /// group and hands back nothing, so a store that answered the empty group
    /// here would be answering a page the plan did not ask for
    /// (SpatialEngine-u2x.55).
    /// </summary>
    internal static AggregatePage EmptyResult(AggregateQuery aggregate, int groupCount, string[] names) =>
        new(
            aggregate.GroupBy ?? (IReadOnlyList<string>)[],
            names,
            EmptyGroups(aggregate, groupCount),
            EmptyTotal(aggregate, groupCount));

    /// <summary>The groups an empty row set answers with: none, unless the plan asked for the one ungrouped group.</summary>
    internal static IReadOnlyList<AggregateGroup> EmptyGroups(AggregateQuery aggregate, int groupCount)
    {
        if (groupCount != 0)
        {
            return [];
        }

        return aggregate.Offset > 0 ? [] : [EmptyGroup(aggregate.Specs)];
    }

    /// <summary>The total an empty row set reports: uncomputed, except a grouped uncapped reduction whose answer is no groups.</summary>
    internal static int? EmptyTotal(AggregateQuery aggregate, int groupCount)
    {
        if (groupCount == 0 || aggregate.Limit is not null)
        {
            return null;
        }

        return 0;
    }

    /// <summary>
    /// The one group an ungrouped reduction of an empty selection answers with,
    /// when the row count says no rows were selected: every other statistic of
    /// an empty set is null either way, and only the row count can tell the
    /// empty selection from a selected row of nulls.
    /// </summary>
    internal static bool TryEmptySelection(
        AggregateQuery aggregate, int groupCount, IReadOnlyList<object?> first, string[] names, out AggregatePage empty)
    {
        if (groupCount == 0 && MatchedNothing(aggregate, first))
        {
            empty = new AggregatePage(
                aggregate.GroupBy ?? (IReadOnlyList<string>)[]!, names, [EmptyGroup(aggregate.Specs)], null);
            return true;
        }

        empty = null!;
        return false;
    }

    /// <summary>Whether the row set holds one more group than the page carries (ADR-0128).</summary>
    internal static bool HasMoreGroups(IReadOnlyList<IReadOnlyList<object?>> rows, int? limit) =>
        limit is { } cap && rows.Count > cap;

    /// <summary>The rows of the page the query asked for, without the one-past-the-page probe row.</summary>
    internal static List<IReadOnlyList<object?>> TrimPage(IReadOnlyList<IReadOnlyList<object?>> rows, int? limit)
    {
        var count = PageSize(rows.Count, limit);
        var page = new List<IReadOnlyList<object?>>(count);
        for (var at = 0; at < count; at++)
        {
            page.Add(rows[at]);
        }

        return page;
    }

    /// <summary>How many rows of the row set belong to the page: the cap, or every row when uncapped.</summary>
    internal static int PageSize(int total, int? limit) =>
        limit is { } take ? Math.Min(take, total) : total;

    /// <summary>The page's rows mapped to groups, in the order the server returned them.</summary>
    internal static List<AggregateGroup> MapPage(
        FeatureSchema schema, AggregateQuery aggregate, List<IReadOnlyList<object?>> page, int groupCount)
    {
        var groups = new List<AggregateGroup>(page.Count);
        foreach (var row in page)
        {
            groups.Add(MapOne(schema, aggregate, row, groupCount));
        }

        return groups;
    }

    /// <summary>The total a mapped page reports: a grouped uncapped reduction counted its groups; anything else leaves it uncomputed.</summary>
    internal static int? PageTotal(AggregateQuery aggregate, int count)
    {
        if (aggregate.IsGrouped && aggregate.Limit is null)
        {
            return count;
        }

        return null;
    }

    /// <summary>One pushed-down group row as a reduction: the group key with one coerced value per statistic.</summary>
    internal static AggregateGroup MapOne(
        FeatureSchema schema, AggregateQuery aggregate, IReadOnlyList<object?> row, int groupCount) =>
        new(MapKey(schema, aggregate, row, groupCount), MapValues(schema, aggregate, row, groupCount));

    /// <summary>One group row's key, with each group column read back under its own kind.</summary>
    internal static AttributeValue[] MapKey(
        FeatureSchema schema, AggregateQuery aggregate, IReadOnlyList<object?> row, int groupCount)
    {
        var key = new AttributeValue[groupCount];
        for (var i = 0; i < groupCount; i++)
        {
            key[i] = PostgisRowMapper.MapValue(schema[schema.IndexOf(aggregate.GroupBy![i])].Kind, row[i]);
        }

        return key;
    }

    /// <summary>One group row's statistic values, each coerced to the kind the reference reports.</summary>
    internal static AttributeValue[] MapValues(
        FeatureSchema schema, AggregateQuery aggregate, IReadOnlyList<object?> row, int groupCount)
    {
        var values = new AttributeValue[aggregate.Specs.Count];
        for (var i = 0; i < aggregate.Specs.Count; i++)
        {
            values[i] = MapValue(schema, aggregate.Specs[i], row[groupCount + i]);
        }

        return values;
    }

    /// <summary>
    /// One pushed-down statistic value as the reference reports it: a row count
    /// is a long, a <c>COUNT(field)</c> counts the non-null values (a zero of
    /// which is a null, the empty group's answer), and every other statistic is
    /// read back under the result kind.
    /// </summary>
    internal static AttributeValue MapValue(FeatureSchema schema, AggregateSpec spec, object? value) =>
        spec.IsRowCount
            ? PostgisRowMapper.MapValue(AttributeKind.Int64, value)
            : spec.Statistic == AggregateStatistic.Count
                ? FeatureReduction.Counted(Convert.ToInt64(value, CultureInfo.InvariantCulture))
                : PostgisRowMapper.MapValue(
                    FeatureReduction.ResultKind(spec, schema[schema.IndexOf(spec.Field)].Kind), value);

    /// <summary>
    /// The one group an ungrouped reduction of an empty selection answers with
    /// (ADR-0098 §3). Every statistic of an empty set is a null, and the row
    /// count is not a statistic of the set's <em>values</em>: it counts the rows,
    /// and a count of no rows is a zero (the same record's "a count is zero").
    /// SQL's ungrouped <c>COUNT(*)</c> already says zero — reading it as a null
    /// would make the pushed answer differ from the reference's for every plan
    /// that selects nothing (SpatialEngine-u2x.55).
    /// </summary>
    internal static AggregateGroup EmptyGroup(IReadOnlyList<AggregateSpec> specs) =>
        new([], [.. specs.Select(spec => spec.IsRowCount ? AttributeValue.FromInt64(0) : AttributeValue.Null)]);

    /// <summary>
    /// Whether the single row an ungrouped reduction returned reduced no rows at
    /// all, which only the row count can say: every other statistic of an empty
    /// set is null either way, and a null is the answer for those whatever this
    /// returns.
    /// </summary>
    internal static bool MatchedNothing(AggregateQuery aggregate, IReadOnlyList<object?> row)
    {
        var groupCount = aggregate.GroupBy?.Count ?? 0;
        for (var i = 0; i < aggregate.Specs.Count; i++)
        {
            if (aggregate.Specs[i].IsRowCount)
            {
                return Convert.ToInt64(row[groupCount + i], CultureInfo.InvariantCulture) == 0;
            }
        }

        return false;
    }
}
