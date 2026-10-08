using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The SQL Server plan reductions (ADR-0133 §2, §4, §6): the count of the rows
/// a plan selects, the deduplicated field combinations, and the grouped
/// reduction — each pushed to T-SQL when the dialect can state the reference's
/// answer, and answered <c>null</c> when it cannot, so the caller reduces the
/// rows it read with the shared reference instead. A reduction is a value and
/// names no feature, so the restriction is safe to push even for a table with
/// no identity column.
/// </summary>
internal sealed class SqlServerReductionReader(SqlServerStorage storage, SqlServerCatalogue catalogue)
{
    /// <summary>
    /// The count of the rows a plan selects, counted by the database over the
    /// restriction it pushed (ADR-0133 §2). A count is a number and names no
    /// feature, so the restriction is safe to push even for a table with no
    /// identity column — the ordinal a row would have carried is a number this
    /// answer does not report.
    /// </summary>
    public async Task<int> CountAsync(
        SqlServerDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        FeatureQueryValidation.Validate((FeatureSchema)description.Schema, query);
        var parameters = new List<object?>();
        var where = SqlServerRestrictionBuilder.Predicate(description, query, parameters);
        return (int)await ScalarAsync(
            name, SqlServerPlanQueries.Count(name, where), parameters, cancellationToken);
    }

    /// <summary>
    /// The deduplicated field combinations a plan selects, pushed as a
    /// <c>SELECT DISTINCT</c> when the plan's order is total over them, and
    /// answered <c>null</c> when it is not — the caller reduces the rows it read
    /// with the shared reference, whose first-seen order is the contract's
    /// (ADR-0133 §6).
    /// </summary>
    public async Task<DistinctPage?> DistinctAsync(
        SqlServerDatasetName name, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(distinct);
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        var schema = (FeatureSchema)description.Schema;
        FeatureQueryValidation.ValidateDistinct(schema, distinct);
        var parameters = new List<object?>();
        var where = SqlServerRestrictionBuilder.Predicate(description, query, parameters);
        var sql = SqlServerPlanQueries.Distinct(
            name,
            distinct.Fields,
            query.Order ?? [],
            schema,
            await ByteOrderTextAsync(schema, distinct.Fields, query.Order ?? [], cancellationToken),
            where,
            parameters);
        if (sql is null)
        {
            return null;
        }

        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await SqlServerDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        var reduced = new List<IReadOnlyList<AttributeValue>>(rows.Count);
        foreach (var row in rows)
        {
            var values = new AttributeValue[distinct.Fields.Count];
            for (var i = 0; i < values.Length; i++)
            {
                var field = distinct.Fields[i];
                values[i] = SqlServerRowMapper.MapValue(schema[schema.IndexOf(field)].Kind, row[i]);
            }

            reduced.Add(values);
        }

        return new DistinctPage(distinct.Fields, reduced, reduced.Count);
    }

    /// <summary>
    /// The grouped reduction a plan selects, pushed as a <c>GROUP BY</c> when
    /// T-SQL can return the reference's group order and answer every statistic
    /// the request names, and answered <c>null</c> when it cannot — the caller
    /// then reduces the rows it read, still handing the reduction the plan's
    /// order (ADR-0128 §8, ADR-0133 §4).
    /// </summary>
    public async Task<AggregatePage?> AggregateAsync(
        SqlServerDatasetName name, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(aggregate);
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        var schema = (FeatureSchema)description.Schema;
        FeatureQueryValidation.ValidateAggregate(schema, aggregate);
        var parameters = new List<object?>();
        var where = SqlServerRestrictionBuilder.Predicate(description, query, parameters);
        // An ungrouped reduction is one row, so the plan's order has nothing to
        // order: it is not written into the SQL at all.
        var order = aggregate.IsGrouped ? query.Order ?? [] : [];
        // Every column whose reduction is a text comparison: the group key, each
        // statistic's field, and the plan's own order terms.
        var text = new List<string>(aggregate.GroupBy ?? []);
        text.AddRange(aggregate.Specs.Where(spec => !spec.IsRowCount).Select(spec => spec.Field));
        // One row past the page is how a store answers "are there more groups?"
        // without counting them (ADR-0128).
        var sql = SqlServerPlanQueries.Aggregate(
            name,
            where,
            aggregate.GroupBy?.ToArray() ?? [],
            aggregate.Specs,
            order,
            schema,
            await ByteOrderTextAsync(schema, text, order, cancellationToken),
            parameters,
            aggregate.Having,
            new SqlServerPlanQueries.Paging(
                aggregate.Limit is { } limit ? limit + 1 : null,
                aggregate.Offset ?? 0));
        if (sql is null)
        {
            return null;
        }

        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await SqlServerDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        return Groups(schema, aggregate, rows);
    }

    /// <summary>
    /// The pushed-down group rows as a reduction: the group key, then one
    /// coerced value per statistic, each mapped to the kind the reference reports
    /// it as, so a sum over an integer field is an integer on both sides.
    ///
    /// <para>
    /// An <em>ungrouped</em> reduction over no rows is <em>one row of nulls</em>
    /// (ADR-0098 §3), which SQL does not say: an ungrouped aggregate always
    /// returns a row, and its <c>COUNT(*)</c> of that row is a zero where the
    /// contract's answer is a null — so the row is replaced by the null row when
    /// the row count says no rows were selected, and a <c>COUNT(field)</c> of
    /// zero is translated to null for the same reason. A grouped reduction over
    /// an empty set returns no row at all, which is the same answer as no groups.
    /// </para>
    /// </summary>
    private static AggregatePage Groups(
        FeatureSchema schema, AggregateQuery aggregate, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var (groupCount, groupBy, names) = GroupShape(aggregate);
        if (rows.Count == 0)
        {
            return EmptyResult(aggregate, groupCount, names, groupBy);
        }

        if (TryEmptySelection(groupCount, aggregate, rows[0], names, groupBy, out var empty))
        {
            return empty;
        }

        // The row past the page is the store's "one more group" answer, not a
        // group of the page (ADR-0128 §3).
        var (count, hasMore) = TrimPage(rows.Count, aggregate.Limit);
        var groups = new List<AggregateGroup>(count);
        for (var at = 0; at < count; at++)
        {
            groups.Add(new AggregateGroup(
                MapKey(schema, aggregate, rows[at], groupCount),
                MapValues(schema, aggregate, rows[at], groupCount)));
        }

        return new AggregatePage(
            groupBy,
            names,
            groups,
            PagedTotal(aggregate, groups.Count),
            hasMore);
    }

    /// <summary>
    /// The shape of the reduction being read: how many group columns it has,
    /// the group key, and one result name per statistic.
    /// </summary>
    private static (int Count, IReadOnlyList<string> By, string[] Names) GroupShape(AggregateQuery aggregate) =>
        (aggregate.GroupBy?.Count ?? 0,
            aggregate.GroupBy ?? [],
            aggregate.Specs.Select(spec => spec.Name).ToArray());

    /// <summary>
    /// The answer when the pushed reduction returned no row at all: a paged
    /// reduction that returned nothing is past the last group, and an offset
    /// past the only group of an ungrouped reduction is past the last group
    /// too — the reference hands back nothing there (SpatialEngine-u2x.55) —
    /// while an ungrouped reduction over an empty selection answers the one
    /// group of nulls (ADR-0098 §3).
    /// </summary>
    private static AggregatePage EmptyResult(
        AggregateQuery aggregate, int groupCount, string[] names, IReadOnlyList<string> groupBy)
    {
        if (groupCount != 0)
        {
            return new AggregatePage(groupBy, names, [], aggregate.Limit is null ? 0 : null);
        }

        if (aggregate.Offset > 0)
        {
            return new AggregatePage(groupBy, names, [], null);
        }

        return new AggregatePage(groupBy, names, [EmptyGroup(aggregate.Specs)], null);
    }

    /// <summary>
    /// Whether the single row an ungrouped reduction returned reduced no rows
    /// at all — only the row count can say so — in which case the answer is
    /// the one group of nulls rather than the row the database returned.
    /// </summary>
    private static bool TryEmptySelection(
        int groupCount,
        AggregateQuery aggregate,
        IReadOnlyList<object?> row,
        string[] names,
        IReadOnlyList<string> groupBy,
        [NotNullWhen(true)] out AggregatePage? page)
    {
        if (groupCount != 0 || !MatchedNothing(aggregate, row))
        {
            page = null;
            return false;
        }

        page = new AggregatePage(groupBy, names, [EmptyGroup(aggregate.Specs)], null);
        return true;
    }

    /// <summary>
    /// The page cut: how many of the returned rows are groups of the page, and
    /// whether the row past the page says one more group remains (ADR-0128 §3).
    /// </summary>
    private static (int Count, bool HasMore) TrimPage(int rowCount, int? limit)
    {
        if (limit is not { } cap)
        {
            return (rowCount, false);
        }

        return (Math.Min(rowCount, cap), rowCount > cap);
    }

    /// <summary>One group row's key, each column mapped to the kind the reference reports it as.</summary>
    private static AttributeValue[] MapKey(
        FeatureSchema schema, AggregateQuery aggregate, IReadOnlyList<object?> row, int groupCount)
    {
        var key = new AttributeValue[groupCount];
        for (var i = 0; i < groupCount; i++)
        {
            key[i] = SqlServerRowMapper.MapValue(schema[schema.IndexOf(aggregate.GroupBy![i])].Kind, row[i]);
        }

        return key;
    }

    /// <summary>One group row's values, one coerced statistic per spec, in request order.</summary>
    private static AttributeValue[] MapValues(
        FeatureSchema schema, AggregateQuery aggregate, IReadOnlyList<object?> row, int groupCount)
    {
        var values = new AttributeValue[aggregate.Specs.Count];
        for (var i = 0; i < aggregate.Specs.Count; i++)
        {
            values[i] = MapOne(schema, aggregate.Specs[i], row[groupCount + i]);
        }

        return values;
    }

    /// <summary>
    /// One statistic's value as the reference reports it: a row count is an
    /// integer, a field count counts the non-null values, and every other
    /// statistic is mapped to the kind the reference reports it as, so a sum
    /// over an integer field is an integer on both sides.
    /// </summary>
    private static AttributeValue MapOne(FeatureSchema schema, AggregateSpec spec, object? raw)
    {
        if (spec.IsRowCount)
        {
            return SqlServerRowMapper.MapValue(AttributeKind.Int64, raw);
        }

        if (spec.Statistic == AggregateStatistic.Count)
        {
            return FeatureReduction.Counted(Convert.ToInt64(raw, CultureInfo.InvariantCulture));
        }

        return SqlServerRowMapper.MapValue(
            FeatureReduction.ResultKind(spec, schema[schema.IndexOf(spec.Field)].Kind),
            raw);
    }

    /// <summary>
    /// The total a pushed reduction reports: only an unpaged grouped reduction
    /// knows how many groups there are, because a page cut is a count the cap
    /// just cut (ADR-0128).
    /// </summary>
    private static int? PagedTotal(AggregateQuery aggregate, int count) =>
        aggregate.IsGrouped && aggregate.Limit is null ? count : null;

    /// <summary>
    /// The one group an ungrouped reduction of an empty selection answers with
    /// (ADR-0098 §3): every statistic of an empty set is a null, and the row
    /// count counts rows, so it is a zero.
    /// </summary>
    private static AggregateGroup EmptyGroup(IReadOnlyList<AggregateSpec> specs) =>
        new([], [.. specs.Select(spec => spec.IsRowCount ? AttributeValue.FromInt64(0) : AttributeValue.Null)]);

    /// <summary>
    /// Whether the single row an ungrouped reduction returned reduced no rows at
    /// all, which only the row count can say: every other statistic of an empty
    /// set is a null either way, and a null is the answer for those whatever this
    /// returns.
    /// </summary>
    private static bool MatchedNothing(AggregateQuery aggregate, IReadOnlyList<object?> row)
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

    /// <summary>
    /// Whether this database already compares text by bytes, which is what
    /// decides whether a pushed-down text term carries an explicit
    /// <c>COLLATE Latin1_General_100_BIN2</c> (ADR-0121, ADR-0124 §3). Read
    /// only when the reduction actually reduces or deduplicates a text column:
    /// the probe is a round trip, and a reduction over numbers cannot be changed
    /// by the collation.
    /// </summary>
    private async Task<bool> ByteOrderTextAsync(
        FeatureSchema schema, IReadOnlyList<string> columns, IReadOnlyList<OrderTerm> order, CancellationToken cancellationToken) =>
        columns.Concat(order.Select(term => term.Field)).Any(field =>
            schema.IndexOf(field) >= 0 && schema[schema.IndexOf(field)].Kind == AttributeKind.String)
            ? SqlServerTextCollation.IsByteOrder(await storage.DatabaseCollationAsync(cancellationToken))
            : false;

    private async Task<long> ScalarAsync(
        SqlServerDatasetName name, string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await SqlServerDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        return rows.Count == 0
            ? throw SpatialException.Unavailable("The database returned no row for a single-row aggregate query.")
            : Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);
    }
}
