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
/// order the dataset's identity cannot make total, a distinct request whose
/// plan order is not total over the distinct rows (SQL's <c>DISTINCT</c> returns
/// rows in no defined order, and the contract's order for them is the plan's),
/// and a grouped reduction whose plan asks for no order (the same reason). A
/// reduction this store cannot push down is not a failure and not a refusal: it
/// is the same value, computed here.
/// </para>
///
/// <para>
/// The <em>page</em> is pushed whenever it can be addressed: a plan that
/// restricts nothing is a read of the whole table, and a whole table read with
/// a <c>LIMIT</c> is a page rather than a materialisation (ADR-0116 §1). Only an
/// unordered or inexpressible order falls back, because a page whose rows are
/// in no total order has no position an <c>OFFSET</c> can name.
/// </para>
///
/// <para>
/// The <em>reduction</em> faces are not subject to that, and the difference is
/// deliberate (ADR-0184 §1): a count, a distinct set and a grouped reduction
/// push their restriction on a dataset that declares no identity column, because
/// they return values and no feature — there is no ordinal for a <c>WHERE</c>
/// to renumber. What stays declined on such a dataset is the feature read
/// itself, including its page: the features it returns are named by the
/// ordinal of the read, and only a whole read keeps those ordinals the same
/// across two pages (ADR-0184 §2).
/// </para>
/// </summary>
internal sealed class PostgisPlanReader(PostgisStorage storage, PostgisCatalogue catalogue)
{
    private const int BatchSize = 512;

    /// <summary>Reads the page a plan names, with the total when it was computed cheaply.</summary>
    public async Task<FeatureQueryPage> ReadAsync(
        PostgisDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        var facts = await catalogue.DescribeFactsAsync(name, cancellationToken);
        var description = facts.Description;
        FeatureQueryValidation.Validate(description.Schema, query);
        var parameters = new List<object?>();
        var where = await RestrictionAsync(facts, query, parameters, cancellationToken);
        var order = PostgisPlanQueries.Order(
            query.Order ?? [], description.IdColumns, description.Schema, await TextOrderAsync(facts, cancellationToken));
        if (!Pushed(where, query, order))
        {
            // The restriction is not expressible without renumbering this
            // dataset's features, or the plan asks for an order this table
            // cannot make total. Read what the database could restrict — which
            // is everything, in the first case — and finish with the reference
            // executor, which selects over the whole read.
            return FeaturePlanExecutor.Finish(
                description.Schema,
                await SelectedAsync(facts, query, where, parameters, cancellationToken),
                query,
                cancellationToken);
        }

        var start = FeaturePageCursor.StartOffset(query);
        var total = await TotalAsync(storage, name, where, parameters, cancellationToken);
        var shape = Columns(facts, query.Projection);
        var sql = PostgisPlanQueries.Read(
            name, shape.Columns, where, order, new PostgisPlanQueries.Paging(query.Limit, start), parameters);
        var page = await BatchesAsync(sql, parameters, shape, cancellationToken);
        var consumed = page.Sum(batch => batch.Count);
        var more = total is { } matched && start + consumed < matched;
        return FeatureQueryPage.Page(
            page,
            more,
            more ? FeaturePageCursor.Issue(query, start + consumed) : null,
            total);
    }

    /// <summary>
    /// Whether the whole page can be addressed in SQL. A restriction the
    /// dialect expressed is one; an <em>empty</em> restriction is one too — a
    /// plan that restricts nothing is a read of the whole table, and reading it
    /// with a <c>LIMIT</c> is what keeps a large layer off the heap (ADR-0116
    /// §1) — but only when the plan's order is an <c>ORDER BY</c> this table can
    /// make total, identity tie-break included. Without one, a row's place in
    /// the page is its place in whatever order the scan happened to return, and
    /// an <c>OFFSET</c> into that order is not a position the next statement
    /// can reproduce. So an unordered plan keeps the reference's in-memory page
    /// over the whole read, and a plan whose requested order the dialect cannot
    /// express does too.
    /// </summary>
    internal static bool Pushed(string? where, FeatureQuery query, IReadOnlyList<string>? order)
    {
        if (query.Order is { Count: > 0 })
        {
            return order is not null;
        }

        return where is not null || order is not null;
    }

    /// <summary>The count of the rows a plan selects, counted by the database.</summary>
    public async Task<int> CountAsync(PostgisDatasetName name, FeatureQuery query, CancellationToken cancellationToken)
    {
        var facts = await catalogue.DescribeFactsAsync(name, cancellationToken);
        var description = facts.Description;
        FeatureQueryValidation.Validate(description.Schema, query);
        var parameters = new List<object?>();
        var where = await ReductionAsync(facts, query, parameters, cancellationToken);
        return where is null
            ? FeatureReduction.CountFeatures(await SelectedAsync(facts, query, null, cancellationToken))
            : (int)await ScalarAsync(storage, PostgisPlanQueries.Count(name, where), parameters, cancellationToken);
    }

    /// <summary>
    /// The deduplicated field combinations a plan selects: a
    /// <c>SELECT DISTINCT</c> under the plan's order when that order is total
    /// over the distinct rows, and reduced over the rows the pushed-down read
    /// returned for every other plan. The dialect returns distinct rows in no
    /// defined order, and the contract's order for them is the order the plan
    /// asked for — so the plan that cannot hand that order to the server keeps
    /// the first-seen order, which only the rows know (ADR-0133 §6).
    /// </summary>
    public async Task<DistinctPage> DistinctAsync(
        PostgisDatasetName name, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken)
    {
        var facts = await catalogue.DescribeFactsAsync(name, cancellationToken);
        var description = facts.Description;
        var schema = (FeatureSchema)description.Schema;
        FeatureQueryValidation.ValidateDistinct(schema, distinct);
        var parameters = new List<object?>();
        var where = await ReductionAsync(facts, query, parameters, cancellationToken);
        var order = query.Order ?? [];
        var sql = PostgisPlanQueries.Distinct(
            name,
            distinct.Fields,
            order,
            schema,
            await TextOrderAsync(facts, schema, distinct.Fields, order, cancellationToken),
            where,
            parameters);
        // A restriction this store cannot state (a dataset with no identity
        // column asked for identities, so there is no expression to say it
        // with — ADR-0184 §1) leaves `where` null for a plan that *does*
        // restrict. Deduplicating on that null would answer with the whole
        // table's set for a restricted plan, so the set is reduced over the
        // rows the reference selected instead.
        if (sql is null || (where is null && PostgisPlanQueries.Restricts(query)))
        {
            return FeatureReduction.Distinct(
                schema,
                await SelectedAsync(facts, query, where, parameters, cancellationToken),
                distinct,
                query.Order);
        }

        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await PostgisDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        var reduced = new List<IReadOnlyList<AttributeValue>>(rows.Count);
        foreach (var row in rows)
        {
            var values = new AttributeValue[distinct.Fields.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = PostgisRowMapper.MapValue(schema[schema.IndexOf(distinct.Fields[i])].Kind, row[i]);
            }

            reduced.Add(values);
        }

        return new DistinctPage(distinct.Fields, reduced, reduced.Count);
    }

    /// <summary>
    /// Whether this database already compares text by bytes, which is what
    /// decides whether a pushed-down text term carries an explicit
    /// <c>COLLATE "C"</c> (ADR-0121). Read only when the reduction deduplicates
    /// or orders a text column: the probe is a round trip, and a set of numbers
    /// cannot be changed by the collation.
    /// </summary>
    private async Task<PostgisTextOrder> TextOrderAsync(
        PostgisDatasetFacts facts,
        FeatureSchema schema,
        IReadOnlyList<string> fields,
        IReadOnlyList<OrderTerm> order,
        CancellationToken cancellationToken) =>
        fields.Concat(order.Select(term => term.Field)).Any(field =>
            schema.IndexOf(field) >= 0 && schema[schema.IndexOf(field)].Kind == AttributeKind.String)
            ? await storage.TextOrderAsync(facts, cancellationToken)
            : PostgisTextOrder.Locale;

    /// <summary>
    /// The restriction a plan pushes, with the database's collation read only
    /// when the restriction actually compares text (ADR-0123, ADR-0126). The
    /// read face compiles its <c>WHERE</c> through here, and so every face that
    /// has to keep its restriction in the caller, so a plan read, a count, a
    /// distinct set, a grouped reduction and a fallback selection all state the
    /// same comparison — and a plan whose predicate is a bounding box and a
    /// number never pays the catalog read that decides the term. A reduction
    /// pushes through <see cref="ReductionAsync"/>, which is this restriction
    /// with one dataset shape's decline lifted (ADR-0184 §1).
    /// </summary>
    private async Task<string?> RestrictionAsync(
        PostgisDatasetFacts facts,
        FeatureQuery query,
        List<object?> parameters,
        CancellationToken cancellationToken) =>
        PostgisPlanQueries.Predicate(
            facts.Dataset,
            facts.Description,
            query,
            // A restriction that compares no text cannot be changed by the
            // collation — in either half of it, the id restriction and the
            // attribute clause — so the read is skipped and the answer unused.
            ComparesText(facts.Description, query)
                ? await storage.TextOrderAsync(facts, cancellationToken)
                : PostgisTextOrder.Locale,
            parameters);

    /// <summary>
    /// The restriction a <em>reduction</em> pushes, over the same plan and the
    /// same catalog read as the read face's (ADR-0184 §1). It is the same
    /// restriction on every dataset that declares an identity column, and on a
    /// dataset that declares none it is the restriction the <em>read</em> face
    /// declines — because a count, a distinct set and a grouped reduction return
    /// values and no feature, so there is no ordinal for a <c>WHERE</c> to
    /// renumber. Only an identity restriction the dataset cannot state keeps
    /// the whole plan in the caller.
    /// </summary>
    private async Task<string?> ReductionAsync(
        PostgisDatasetFacts facts,
        FeatureQuery query,
        List<object?> parameters,
        CancellationToken cancellationToken) =>
        PostgisPlanQueries.Reduction(
            facts.Dataset,
            facts.Description,
            query,
            ComparesText(facts.Description, query)
                ? await storage.TextOrderAsync(facts, cancellationToken)
                : PostgisTextOrder.Locale,
            parameters);

    /// <summary>Whether the plan's restriction compares a text column, in either half of it.</summary>
    private static bool ComparesText(DatasetDescription description, FeatureQuery query) =>
        (query.Ids is { Count: > 0 } && PostgisIdentity.ComparesText(description))
        || (query.Where is { } where && PostgisPredicateSql.ComparesText(where, description.Schema));

    /// <summary>
    /// The order a pushed-down sort key over this dataset's text columns is
    /// written in, which is each column's own collation where it declares one
    /// and the database's where it declares none (ADR-0121, ADR-0136). The
    /// database's half is read once per store and cached; a database the probe
    /// cannot answer for is treated as a locale collation, because the term is
    /// the only thing standing between the pushdown and the contract's answer.
    /// </summary>
    private Task<PostgisTextOrder> TextOrderAsync(PostgisDatasetFacts facts, CancellationToken cancellationToken) =>
        storage.TextOrderAsync(facts, cancellationToken);

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
        var facts = await catalogue.DescribeFactsAsync(name, cancellationToken);
        var description = facts.Description;
        var schema = (FeatureSchema)description.Schema;
        FeatureQueryValidation.ValidateAggregate(schema, aggregate);
        var parameters = new List<object?>();
        var where = await ReductionAsync(facts, query, parameters, cancellationToken);
        // An ungrouped reduction is one row, so the plan's order has nothing to
        // order: it is not written into the SQL at all (an `ORDER BY` over an
        // ungrouped aggregate's column is a query Postgres refuses).
        var order = aggregate.IsGrouped ? query.Order ?? [] : [];
        // A restriction the dialect could not state (a dataset with no identity
        // column asked for identities, ADR-0184 §1) leaves `where` null for a
        // plan that *does* restrict. Pushing on that null would reduce the whole
        // table for a restricted plan, so the reduction is finished over the
        // selected rows instead. An unrestricted plan's null really is "every
        // row".
        var pushed = where is not null || !PostgisPlanQueries.Restricts(query)
            ? await PushedGroupsAsync(
                name, facts, aggregate, where, order, await TextOrderAsync(facts, cancellationToken), parameters, cancellationToken)
                .ConfigureAwait(false)
            : null;
        return pushed ?? FeatureReduction.Aggregate(
            schema,
            await SelectedAsync(facts, query, where, parameters, cancellationToken),
            aggregate,
            query.Order);
    }

    private async Task<AggregatePage?> PushedGroupsAsync(
        PostgisDatasetName name,
        PostgisDatasetFacts facts,
        AggregateQuery aggregate,
        string? where,
        IReadOnlyList<OrderTerm> order,
        PostgisTextOrder text,
        List<object?> parameters,
        CancellationToken cancellationToken)
    {
        // One row past the page is how a store answers "are there more groups?"
        // without counting them: the server groups, sorts and stops, and the
        // extra row is the only question a page needs answered (ADR-0128).
        var page = new PostgisPlanQueries.Paging(
            aggregate.Limit is { } limit ? limit + 1 : null,
            aggregate.Offset ?? 0);
        var sql = PostgisPlanQueries.Aggregate(
            name,
            where,
            aggregate.GroupBy?.ToArray() ?? [],
            aggregate.Specs,
            order,
            facts.Description.Schema,
            text,
            parameters,
            aggregate.Having,
            page);
        if (sql is null)
        {
            // A group order the dialect cannot return is not an error and not a
            // refusal (ADR-0074 §6): the caller reduces the rows the
            // restriction selected and answers the reference's reduction.
            return null;
        }

        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await PostgisDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        return Groups((FeatureSchema)facts.Description.Schema, aggregate, rows);
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
            // A paged reduction that returned nothing is past the last group, and
            // how many groups there were is a count the cap just cut, so the
            // total is the store's to leave uncomputed (ADR-0128). An *ungrouped*
            // reduction is one group, and an offset that lands past it is past
            // the last group too: the reference pages the one group and hands
            // back nothing, so a store that answered the empty group here would
            // be answering a page the plan did not ask for
            // (SpatialEngine-u2x.55).
            var pastTheOnlyGroup = groupCount == 0 && aggregate.Offset > 0;
            return new AggregatePage(
                aggregate.GroupBy ?? (IReadOnlyList<string>)[],
                names,
                groupCount == 0 && !pastTheOnlyGroup ? [EmptyGroup(aggregate.Specs)] : [],
                groupCount == 0 || aggregate.Limit is not null ? null : 0);
        }

        if (groupCount == 0 && MatchedNothing(aggregate, rows[0]))
        {
            return new AggregatePage(
                aggregate.GroupBy ?? (IReadOnlyList<string>)[]!, names, [EmptyGroup(aggregate.Specs)], null);
        }

        // The row past the page is the store's "one more group" answer, not a
        // group of the page (ADR-0128): the page is what the query asked for and
        // the flag is what the caller cannot compute without the rows the cap
        // just cut.
        var hasMore = aggregate.Limit is { } limit && rows.Count > limit;
        var groups = new List<AggregateGroup>(Math.Min(rows.Count, aggregate.Limit ?? int.MaxValue));
        for (var at = 0; at < rows.Count && (aggregate.Limit is not { } cap || at < cap); at++)
        {
            var row = rows[at];
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
            aggregate.IsGrouped && aggregate.Limit is null ? groups.Count : null,
            hasMore);
    }

    /// <summary>
    /// The one group an ungrouped reduction of an empty selection answers with
    /// (ADR-0098 §3). Every statistic of an empty set is a null, and the row
    /// count is not a statistic of the set's <em>values</em>: it counts the rows,
    /// and a count of no rows is a zero (the same record's "a count is zero").
    /// SQL's ungrouped <c>COUNT(*)</c> already says zero — reading it as a null
    /// would make the pushed answer differ from the reference's for every plan
    /// that selects nothing (SpatialEngine-u2x.55).
    /// </summary>
    private static AggregateGroup EmptyGroup(IReadOnlyList<AggregateSpec> specs) =>
        new([], [.. specs.Select(spec => spec.IsRowCount ? AttributeValue.FromInt64(0) : AttributeValue.Null)]);

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
    ///
    /// <para>
    /// <paramref name="where"/> is the restriction the caller already resolved
    /// — the read face's own for a fallback read, the reduction's for a count,
    /// a distinct set or a grouped reduction — and it is <em>not</em> derived
    /// here: both of the ways it arrives null mean the same thing to this
    /// method, a restriction the dialect could not state, and re-deriving it
    /// would compile the same predicate to learn nothing (SpatialEngine-v9x).
    /// A null restriction is therefore a whole read whose rows this method
    /// selects over, which is what keeps a feature's identity the ordinal the
    /// whole read gave it (ADR-0097).
    /// </para>
    /// </summary>
    private Task<List<Feature>> SelectedAsync(
        PostgisDatasetFacts facts,
        FeatureQuery query,
        string? where,
        CancellationToken cancellationToken) =>
        SelectedAsync(facts, query, where, new List<object?>(), cancellationToken);

    private async Task<List<Feature>> SelectedAsync(
        PostgisDatasetFacts facts,
        FeatureQuery query,
        string? where,
        List<object?> parameters,
        CancellationToken cancellationToken)
    {
        // The store's own row order, never a SQL order of this store's choosing:
        // a reduction's row order is the plan's, and a plan that asked for no
        // order takes the order its scan would have returned.
        var rows = await WholeAsync(facts, where, parameters, cancellationToken);

        // A restriction the dialect could not express is applied here, over the
        // whole read, so a feature keeps the identity the full scan gave it
        // (ADR-0097). A plan with no restriction at all selects everything,
        // which is the same rows.
        return where is null
            ? FeaturePlanExecutor.Select((FeatureSchema)facts.Description.Schema, rows, query, cancellationToken)
            : rows;
    }

    private async Task<List<Feature>> WholeAsync(
        PostgisDatasetFacts facts, string? where, List<object?> parameters, CancellationToken cancellationToken)
    {
        var shape = Columns(facts, projection: null);
        var sql = PostgisPlanQueries.Read(
            facts.Dataset, shape.Columns, where, null, new PostgisPlanQueries.Paging(null, 0), parameters);
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
    private static Shape Columns(PostgisDatasetFacts facts, IReadOnlyList<string>? projection)
    {
        // The projection is decided once per read, not once per row: a read
        // that appended nothing to drop returns each row's own feature
        // (FeatureRowProjection), which is what a whole read of a keyless layer
        // does on every row it reads (SpatialEngine-yup).
        var description = facts.Description;
        var fields = (projection is null or { Count: 0 }
            ? description.Schema.Fields
            : projection
                .Where(field => field != AggregateSpec.AllFields)
                .Select(field => description.Schema[description.Schema.IndexOf(field)])
                .ToArray()).ToList();
        var result = new FeatureSchema(fields);
        var read = new FeatureSchema([
            .. fields,
            .. description.IdColumns
                .Select(column => description.Schema[description.Schema.IndexOf(column)])
                .Where(field => !fields.Contains(field))]);
        // The identity indexes are the identity columns' positions in what was
        // *read*, which is not the set of columns that were appended: an
        // ordinary read carries the whole schema and therefore already carries
        // the identity, and taking the appended ones as the identity would name
        // every pushed row by its ordinal instead of its key (ADR-0131).
        var indexes = description.IdColumns.Select(column => read.IndexOf(column)).Where(index => index >= 0).ToArray();
        return new Shape(PostgisPlanQueries.Columns(read, null), read, new FeatureRowProjection(read, result), indexes);
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
            features.Add(shape.Projection.Apply(PostgisRowMapper.MapRow(shape.Read, shape.Identity, row, ordinal++)));
        }

        return features.Count == 0
            ? [new FeatureBatch(shape.Result, [])]
            : features.Chunk(BatchSize).Select(chunk => new FeatureBatch(shape.Result, chunk)).ToArray();
    }

    /// <summary>
    /// The number of rows the plan matches, counted by the database. It runs on
    /// every pushed read — with or without a <c>WHERE</c> — because an
    /// aggregate row is not a row set: the count is what lets the page say
    /// whether the plan has more without over-fetching, and it is the same
    /// number the reference reports as the page's total.
    /// </summary>
    private static async Task<int> TotalAsync(
        PostgisStorage storage, PostgisDatasetName name, string? where, List<object?> parameters, CancellationToken cancellationToken) =>
        (int)await ScalarAsync(storage, PostgisPlanQueries.Count(name, where), parameters, cancellationToken);

    private static async Task<long> ScalarAsync(
        PostgisStorage storage, string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken)
    {
        await using var connection = await storage.OpenConnectionAsync(cancellationToken);
        var rows = await PostgisDataStore.ReadRowsAsync(connection, sql, parameters, cancellationToken);
        return rows.Count == 0
            ? throw SpatialException.Unavailable("The database returned no row for a single-row aggregate query.")
            : Convert.ToInt64(rows[0][0], CultureInfo.InvariantCulture);
    }

    /// <summary>One pushed-down read's SQL shape: what is read, and what is returned.</summary>
    private sealed record Shape(
        IReadOnlyList<string> Columns,
        FeatureSchema Read,
        FeatureRowProjection Projection,
        IReadOnlyList<int> Identity)
    {
        /// <summary>The schema the page is answered with: the plan's projection.</summary>
        public FeatureSchema Result => Projection.Result;
    }
}
