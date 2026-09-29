using System.Globalization;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The PostGIS plan read and reduction faces (ADR-0074 §4, §6): the plan is
/// compiled to SQL and pushed down as far as the dialect goes — projection,
/// order, row cap, count, grouped aggregate — and whatever SQL cannot express
/// is finished here with the shared reference executor, over the rows that
/// were read. The answer is the reference's answer either way; only the rows
/// that crossed the wire differ.
///
/// <para>
/// Three cases finish here rather than in SQL, each because a pushed version
/// would be a <em>different</em> answer and principle 15 forbids that: an
/// order the dataset's identity cannot make total, a distinct request
/// (SQL's <c>DISTINCT</c> returns rows in no defined order, and the contract's
/// order for them is the plan's), and a grouped reduction whose plan asks for
/// no order (the same reason). A reduction this store cannot push down is not a
/// failure and not a refusal: it is the same value, computed here.
/// </para>
/// </summary>
internal sealed class PostgisPlanReader(PostgisStorage storage, PostgisCatalogue catalogue)
{
    private const int BatchSize = 512;

    /// <summary>Reads the page a plan names, with the total when it was computed cheaply.</summary>
    public async Task<FeatureQueryPage> ReadAsync(
        PostgisDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        FeatureQueryValidation.Validate(description.Schema, query);
        var parameters = new List<object?>();
        var where = PostgisPlanQueries.Predicate(name, description, query, parameters);
        var order = PostgisPlanQueries.Order(query.Order ?? [], description.IdColumns);
        if (where is null || (query.Order is { Count: > 0 } && order is null))
        {
            // The restriction is not expressible without renumbering this
            // dataset's features, or the plan asks for an order this table
            // cannot make total. Read what the database could restrict — which
            // is everything, in the first case — and finish with the reference
            // executor, which selects over the whole read.
            return FeaturePlanExecutor.Finish(
                description.Schema,
                await SelectedAsync(name, description, query, where, parameters, cancellationToken),
                query,
                cancellationToken);
        }

        var start = FeaturePageCursor.StartOffset(query);
        var total = await TotalAsync(storage, name, where, parameters, cancellationToken);
        var shape = Columns(description, query.Projection);
        var sql = PostgisPlanQueries.Read(
            name, shape.Columns, where, order, new PostgisPlanQueries.Paging(query.Limit, start), parameters);
        var page = await BatchesAsync(sql, parameters, shape, cancellationToken);
        var consumed = page.Sum(batch => batch.Count);
        var next = total is { } matched && start + consumed < matched
            ? FeaturePageCursor.Issue(query, start + consumed)
            : null;
        return new FeatureQueryPage(page, next, total);
    }

    /// <summary>The count of the rows a plan selects, counted by the database.</summary>
    public async Task<int> CountAsync(PostgisDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        FeatureQueryValidation.Validate(description.Schema, query);
        var parameters = new List<object?>();
        var where = PostgisPlanQueries.Predicate(name, description, query, parameters);
        return where is null
            ? FeatureReduction.CountFeatures(await SelectedAsync(name, description, query, null, cancellationToken))
            : (int)await ScalarAsync(storage, PostgisPlanQueries.Count(name, where), parameters, cancellationToken);
    }

    /// <summary>
    /// The deduplicated field combinations a plan selects, reduced over the rows
    /// the pushed-down read returned rather than as a SQL <c>DISTINCT</c>: the
    /// dialect returns distinct rows in no defined order, and the contract's
    /// order for them is the order the plan asked for.
    /// </summary>
    public async Task<DistinctPage> DistinctAsync(
        PostgisDatasetName name, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        FeatureQueryValidation.ValidateDistinct(description.Schema, distinct);
        var parameters = new List<object?>();
        var where = PostgisPlanQueries.Predicate(name, description, query, parameters);
        var selected = await SelectedAsync(name, description, query, where, parameters, cancellationToken);
        return FeatureReduction.Distinct((FeatureSchema)description.Schema, selected, distinct);
    }

    /// <summary>
    /// The reduction a plan selects, pushed down when the dialect can return its
    /// row order: an ungrouped reduction is one group whatever the order, and a
    /// grouped one is pushed when the plan's order is over the group key itself
    /// (a <c>GROUP BY</c> returns rows in no defined order, and a key is a total
    /// order over the groups). Anything else is reduced here over the rows the
    /// restriction selected, where first-seen order is knowable.
    /// </summary>
    public async Task<AggregatePage> AggregateAsync(
        PostgisDatasetName name, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken)
    {
        var description = await catalogue.DescribeAsync(name, cancellationToken);
        var schema = (FeatureSchema)description.Schema;
        FeatureQueryValidation.ValidateAggregate(schema, aggregate);
        var parameters = new List<object?>();
        var where = PostgisPlanQueries.Predicate(name, description, query, parameters);
        // An ungrouped reduction is one row, so the plan's order has nothing to
        // order: it is not written into the SQL at all (an `ORDER BY` over an
        // ungrouped aggregate's column is a query Postgres refuses).
        var order = aggregate.IsGrouped ? query.Order ?? [] : [];
        // A restriction this table cannot carry (a dataset with no identity
        // column names its features by the read's ordinal, so a `WHERE` would
        // renumber them — ADR-0097) leaves `where` null for a plan that *does*
        // restrict. Pushing on that null would aggregate the whole table for a
        // restricted plan, so the reduction is finished over the selected rows
        // instead. An unrestricted plan's null really is "every row".
        var pushed = where is not null || !PostgisPlanQueries.Restricts(query)
            ? await PushedGroupsAsync(name, description, aggregate, where, order, parameters, cancellationToken)
                .ConfigureAwait(false)
            : null;
        return pushed ?? FeatureReduction.Aggregate(
            schema,
            await SelectedAsync(name, description, query, where, parameters, cancellationToken),
            aggregate);
    }

    private async Task<AggregatePage?> PushedGroupsAsync(
        PostgisDatasetName name,
        DatasetDescription description,
        AggregateQuery aggregate,
        string? where,
        IReadOnlyList<OrderTerm> order,
        List<object?> parameters,
        CancellationToken cancellationToken)
    {
        var sql = PostgisPlanQueries.Aggregate(
            name, where, aggregate.GroupBy?.ToArray() ?? [], aggregate.Specs, order, parameters);
        if (sql is null)
        {
            // A group order the dialect cannot return is not an error and not a
            // refusal (ADR-0074 §6): the caller reduces the rows the
            // restriction selected and answers the reference's reduction.
            return null;
        }

        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await PostgisDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        return Groups((FeatureSchema)description.Schema, aggregate, rows);
    }

    /// <summary>
    /// The pushed-down group rows as a reduction: the group key, then one
    /// coerced value per statistic. A value the dialect returned as
    /// <c>numeric</c> becomes the kind the reference reports, so a sum over an
    /// integer field is an integer on both sides of the comparison.
    ///
    /// <para>
    /// An <em>ungrouped</em> reduction over no rows is <em>one row of nulls</em>
    /// (ADR-0098 §3), which SQL does not say: an ungrouped aggregate always
    /// returns a row, and its <c>COUNT(*)</c> of that row is a zero where the
    /// contract's answer is a null. So the row is replaced by the null row when
    /// the row count says no rows were selected, and when the dialect returned
    /// no row at all (which a grouped reduction does over an empty set — and
    /// there the answer is no groups, as it always was). A
    /// <c>COUNT(field)</c> of zero is translated to null for the same reason:
    /// the dialect counts the column's values, and the contract counts its
    /// <em>non-null</em> values, of which there were none.
    /// </para>
    /// </summary>
    private static AggregatePage Groups(FeatureSchema schema, AggregateQuery aggregate, IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        var groupCount = aggregate.GroupBy?.Count ?? 0;
        var names = aggregate.Specs.Select(spec => spec.Name).ToArray();
        if (rows.Count == 0)
        {
            return new AggregatePage(
                aggregate.GroupBy ?? (IReadOnlyList<string>)[],
                names,
                groupCount == 0 ? [FeatureReduction.EmptyGroup(aggregate.Specs)] : [],
                groupCount == 0 ? null : 0);
        }

        if (groupCount == 0 && MatchedNothing(aggregate, rows[0]))
        {
            return new AggregatePage(aggregate.GroupBy ?? (IReadOnlyList<string>)[]!, names, [FeatureReduction.EmptyGroup(aggregate.Specs)], null);
        }

        var groups = new List<AggregateGroup>(rows.Count);
        foreach (var row in rows)
        {
            var key = new AttributeValue[groupCount];
            for (var i = 0; i < groupCount; i++)
            {
                key[i] = PostgisRowMapper.MapValue(schema[schema.IndexOf(aggregate.GroupBy![i])].Kind, row[i]);
            }

            var values = new AttributeValue[aggregate.Specs.Count];
            for (var i = 0; i < aggregate.Specs.Count; i++)
            {
                var spec = aggregate.Specs[i];
                var value = row[groupCount + i];
                values[i] = spec.IsRowCount
                    ? PostgisRowMapper.MapValue(AttributeKind.Int64, value)
                    : spec.Statistic == AggregateStatistic.Count
                        ? FeatureReduction.Counted(Convert.ToInt64(value, CultureInfo.InvariantCulture))
                        : PostgisRowMapper.MapValue(
                            FeatureReduction.ResultKind(spec, schema[schema.IndexOf(spec.Field)].Kind), value);
            }

            groups.Add(new AggregateGroup(key, values));
        }

        return new AggregatePage(
            aggregate.GroupBy ?? (IReadOnlyList<string>)[],
            aggregate.Specs.Select(spec => spec.Name).ToArray(),
            groups,
            aggregate.IsGrouped ? groups.Count : null);
    }

    /// <summary>
    /// Whether the single row an ungrouped reduction returned reduced no rows at
    /// all, which only the row count can say: every other statistic of an empty
    /// set is null either way, and a null is the answer for those whatever this
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
    /// Every feature a plan's restriction selects, with the plan's shaping
    /// stripped: the reduction faces must see the whole selected set, so the
    /// row cap, the order and the projection are not applied to it.
    /// </summary>
    private Task<List<Feature>> SelectedAsync(
        PostgisDatasetName name,
        DatasetDescription description,
        FeatureQuery query,
        string? where,
        CancellationToken cancellationToken) =>
        SelectedAsync(name, description, query, where, new List<object?>(), cancellationToken);

    private async Task<List<Feature>> SelectedAsync(
        PostgisDatasetName name,
        DatasetDescription description,
        FeatureQuery query,
        string? where,
        List<object?> parameters,
        CancellationToken cancellationToken)
    {
        var pushed = where is not null;
        if (!pushed)
        {
            where = PostgisPlanQueries.Predicate(name, description, query, parameters);
        }

        // The store's own row order, never a SQL order of this store's choosing:
        // a reduction's row order is the plan's, and a plan that asked for no
        // order takes the order its scan would have returned.
        var rows = await WholeAsync(name, description, where, parameters, cancellationToken);

        // A restriction the dialect could not express is applied here, over the
        // whole read, so a feature keeps the identity the full scan gave it
        // (ADR-0097). A plan with no restriction at all selects everything,
        // which is the same rows.
        return pushed
            ? rows
            : FeaturePlanExecutor.Select((FeatureSchema)description.Schema, rows, query, cancellationToken);
    }

    private async Task<List<Feature>> WholeAsync(
        PostgisDatasetName name, DatasetDescription description, string? where, List<object?> parameters, CancellationToken cancellationToken)
    {
        var shape = Columns(description, projection: null);
        var sql = PostgisPlanQueries.Read(
            name, shape.Columns, where, null, new PostgisPlanQueries.Paging(null, 0), parameters);
        var page = await BatchesAsync(sql, parameters, shape, cancellationToken);
        return page.SelectMany(batch => batch.Features).ToList();
    }

    /// <summary>
    /// How a pushed-down read is shaped in SQL: the plan's projection as the
    /// result schema, plus the identity columns the row mapper needs to name
    /// each row, and the column list that reads both. The identity columns are
    /// dropped again before the page is returned, so a projection returns
    /// exactly the fields it asked for.
    /// </summary>
    private static Shape Columns(DatasetDescription description, IReadOnlyList<string>? projection)
    {
        var fields = (projection is null or { Count: 0 }
            ? description.Schema.Fields
            : projection
                .Where(field => field != AggregateSpec.AllFields)
                .Select(field => description.Schema[description.Schema.IndexOf(field)])
                .ToArray()).ToList();
        var result = new FeatureSchema(fields);
        var identity = description.IdColumns
            .Select(column => description.Schema[description.Schema.IndexOf(column)])
            .Where(field => !fields.Contains(field))
            .ToArray();
        var read = new FeatureSchema([.. fields, .. identity]);
        var indexes = identity.Select(field => read.IndexOf(field.Name)).ToArray();
        return new Shape(PostgisPlanQueries.Columns(read, null), read, result, indexes);
    }

    private async Task<FeatureBatch[]> BatchesAsync(
        string sql, IReadOnlyList<object?> parameters, Shape shape, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        await using var reader = await PostgisDataStore.ExecuteReaderAsync(connection, sql, parameters, cancellationToken);
        var features = new List<Feature>(BatchSize);
        long ordinal = 0;
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = PostgisDataStore.ReadRow(reader, reader.FieldCount);
            features.Add(shape.Project(shape.Read, PostgisRowMapper.MapRow(shape.Read, shape.Identity, row, ordinal++)));
        }

        return features.Count == 0
            ? [new FeatureBatch(shape.Result, [])]
            : features.Chunk(BatchSize).Select(chunk => new FeatureBatch(shape.Result, chunk)).ToArray();
    }

    private static async Task<int?> TotalAsync(
        PostgisStorage storage, PostgisDatasetName name, string? where, List<object?> parameters, CancellationToken cancellationToken) =>
        where is null
            ? null
            : (int)await ScalarAsync(storage, PostgisPlanQueries.Count(name, where), parameters, cancellationToken);

    private static async Task<long> ScalarAsync(
        PostgisStorage storage, string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await PostgisDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        return rows.Count == 0
            ? throw SpatialException.Unavailable("The database returned no row for a single-row aggregate query.")
            : Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A plan with only its restriction: the reduction faces must see every
    /// selected row, so the shaping members (projection, order, cap, cursor)
    /// are stripped before the rows are selected.
    /// </summary>
    private static FeatureQuery Unbounded(FeatureQuery query) =>
        query with { Projection = null, Order = null, Limit = null, Offset = null, Cursor = null };

    /// <summary>One pushed-down read's SQL shape: what is read, and what is returned.</summary>
    private sealed record Shape(
        IReadOnlyList<string> Columns,
        FeatureSchema Read,
        FeatureSchema Result,
        IReadOnlyList<int> Identity)
    {
        /// <summary>Drops the identity columns a read needed and keeps the plan's projection.</summary>
        public Feature Project(FeatureSchema read, Feature feature)
        {
            var values = new AttributeValue[Result.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = ReferenceEquals(read, Result) ? feature[i] : feature[read.IndexOf(Result[i].Name)];
            }

            return new Feature(feature.Id, Result, values);
        }
    }
}
