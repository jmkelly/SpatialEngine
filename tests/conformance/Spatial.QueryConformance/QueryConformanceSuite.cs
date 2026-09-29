using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;

namespace Spatial.QueryConformance;

/// <summary>
/// The pushdown-equals-reference suite (ADR-0098 §3). A store's own answer
/// for a plan and for each reduction is compared, value for value and sequence
/// for sequence, with the answer the shared reference executor produces over
/// the same fixture. Run it against every store: the stores that push down earn
/// speed, the ones that do not earn correctness, and neither is allowed to
/// answer differently.
///
/// <para>
/// The comparison is deliberately whole-answer rather than per-field: a dialect
/// that sorts nulls first, sums a null group to zero, or returns a distinct set
/// in a different order has not returned the reference's answer, whatever its
/// numbers are.
/// </para>
/// </summary>
public static class QueryConformanceSuite
{
    /// <summary>
    /// Runs the whole suite against a seeded store. The caller owns seeding and
    /// disposal; the suite only reads.
    ///
    /// <para>
    /// The reference is evaluated over the rows the store itself returns for an
    /// unbounded read, not over the fixture as written: a store assigns its own
    /// feature identities (ADR-0043), and the contract's order for a plan that
    /// asks for none is the store's own order. The fixture therefore seeds the
    /// data — ties, nulls, single-row groups, an empty box — and the store's own
    /// rows are what both answers are computed from.
    /// </para>
    /// </summary>
    public static async Task RunAsync(IFeatureStore store, string dataset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var scan = await store.ScanAsync(dataset, cancellationToken).ConfigureAwait(false);
        var schema = (FeatureSchema)scan[0].Schema;
        var features = scan.SelectMany(batch => batch.Features).ToList();

        var fields = Fields.Of(schema);
        await SamePlanAnswerAsync(store, dataset, FeatureQuery.All, schema, features, cancellationToken).ConfigureAwait(false);
        await SamePlanAnswerAsync(
            store, dataset, new FeatureQuery(BoundingBox: fields.Box), schema, features, cancellationToken).ConfigureAwait(false);
        await SamePlanAnswerAsync(
            store, dataset, new FeatureQuery(BoundingBox: QueryFixture.EmptyBox), schema, features, cancellationToken).ConfigureAwait(false);
        await SamePlanAnswerAsync(
            store, dataset, new FeatureQuery(Where: Matching(fields)), schema, features, cancellationToken).ConfigureAwait(false);
        await SamePlanAnswerAsync(
            store,
            dataset,
            new FeatureQuery(Where: Matching(fields), Order: [new OrderTerm(fields.Numeric)], Limit: 2),
            schema,
            features,
            cancellationToken).ConfigureAwait(false);
        await SamePlanAnswerAsync(
            store, dataset, new FeatureQuery(Where: Absent(fields)), schema, features, cancellationToken).ConfigureAwait(false);
        await SamePlanAnswerAsync(
            store, dataset, new FeatureQuery(Where: Nulls(fields)), schema, features, cancellationToken).ConfigureAwait(false);
        await SameProjectionAnswerAsync(store, dataset, schema, features, cancellationToken).ConfigureAwait(false);
        await SameOrderAnswerAsync(store, dataset, schema, features, cancellationToken).ConfigureAwait(false);
        await SamePagedWalkAsync(store, dataset, schema, features, cancellationToken).ConfigureAwait(false);
        await SameCountAsync(store, dataset, schema, features, cancellationToken).ConfigureAwait(false);
        await SameDistinctAsync(store, dataset, schema, features, cancellationToken).ConfigureAwait(false);
        await SameAggregateAsync(store, dataset, schema, features, cancellationToken).ConfigureAwait(false);
        await SameOrderedAggregateAsync(store, dataset, schema, features, cancellationToken).ConfigureAwait(false);
        await RejectsBadPlansAsync(store, dataset, cancellationToken).ConfigureAwait(false);
    }

    private static async Task SamePlanAnswerAsync(
        IFeatureStore store, string dataset, FeatureQuery query, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        var actual = await store.QueryAsync(dataset, query, token).ConfigureAwait(false);
        var expected = FeaturePlanExecutor.Execute(schema, fixture, query, token);

        Assert.Equal(expected.TotalCount, actual.TotalCount);
        Assert.Equal(Sequence(expected), Sequence(actual));
    }

    private static async Task SameProjectionAnswerAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        var fields = Fields.Of(schema);
        var query = new FeatureQuery(Projection: fields.Projection, Order: [new OrderTerm(fields.Key)]);
        var actual = await store.QueryAsync(dataset, query, token).ConfigureAwait(false);
        var expected = FeaturePlanExecutor.Execute(schema, fixture, query, token);

        Assert.Equal(fields.Projection, actual.Batches[0].Schema.Fields.Select(field => field.Name).ToArray());
        Assert.Equal(Sequence(expected), Sequence(actual));
    }

    private static async Task SameOrderAnswerAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        var fields = Fields.Of(schema);
        var queries = new List<FeatureQuery>
        {
            new(Order: [new OrderTerm(fields.Orderable)]),
            new(Order: [new OrderTerm(fields.Orderable, SortDirection.Descending)]),
            // A descending sort over a *restricted* row set: the two compose,
            // and a dialect that sorts before it filters (or the reverse) does
            // not give the reference's page.
            new(Where: Matching(fields), Order: [new OrderTerm(fields.Orderable, SortDirection.Descending)]),
        };

        if (!string.Equals(fields.Group, fields.Orderable, StringComparison.Ordinal))
        {
            queries.Add(new FeatureQuery(
                Order: [new OrderTerm(fields.Group), new OrderTerm(fields.Orderable, SortDirection.Descending)]));
        }

        foreach (var query in TextOrders(fields))
        {
            queries.Add(query);
        }

        foreach (var query in queries)
        {
            var actual = await store.QueryAsync(dataset, query, token).ConfigureAwait(false);
            var expected = FeaturePlanExecutor.Execute(schema, fixture, query, token);
            Assert.Equal(Sequence(expected), Sequence(actual));
        }
    }

    /// <summary>
    /// The plans that order by a <em>text</em> key, which is where the
    /// reference's rule and a database's default disagree: the contract
    /// compares strings ordinally — by bytes, never by a locale collation
    /// (ADR-0098 §3) — while an <c>ORDER BY</c> pushed into SQL inherits the
    /// collation of the database it runs against. The fixture's text columns
    /// are built so the two rules put every row somewhere different
    /// (ADR-0121), so a store that pushes a text sort key down without saying
    /// so returns a different sequence here, and one that never pushes it
    /// cannot.
    ///
    /// <para>The list is empty for a store whose schema carries no text field
    /// of its own, rather than a no-op that passes for the same reason as a
    /// correct one.</para>
    /// </summary>
    private static IEnumerable<FeatureQuery> TextOrders(Fields fields)
    {
        if (string.Equals(fields.Text, fields.Numeric, StringComparison.Ordinal))
        {
            return [];
        }

        return
        [
            new FeatureQuery(Order: [new OrderTerm(fields.Text)]),
            new FeatureQuery(Order: [new OrderTerm(fields.Text, SortDirection.Descending)]),
            new FeatureQuery(Where: Matching(fields), Order: [new OrderTerm(fields.Text)]),
            // A text key as the second term of a composite order, so the text
            // comparison is the tie-break rather than the whole sort.
            new FeatureQuery(Order: [new OrderTerm(fields.Numeric), new OrderTerm(fields.Text, SortDirection.Descending)]),
        ];
    }

    /// <summary>
    /// The paging walk: page by page from the start, following each store-issued
    /// cursor, and compare the concatenated sequence with the reference's. A
    /// store that re-orders rows between pages, drops one at a boundary or
    /// repeats one fails here rather than in production.
    /// </summary>
    private static async Task SamePagedWalkAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        var fields = Fields.Of(schema);
        foreach (var key in PagingKeys(fields))
        {
            await PagedWalkAsync(store, dataset, schema, fixture, key, token).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The order keys the walk is taken over: the numeric one every store
    /// carries, and the text one when the schema has a text field of its own —
    /// a page boundary is exactly where a collation drift shows up, because a
    /// store that sorts each page under a different rule than the reference
    /// returns a page the reference never handed out (ADR-0121).
    /// </summary>
    private static IEnumerable<string> PagingKeys(Fields fields) =>
        string.Equals(fields.Text, fields.Numeric, StringComparison.Ordinal)
            ? [fields.Orderable]
            : [fields.Orderable, fields.Text];

    private static async Task PagedWalkAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, string order, CancellationToken token)
    {
        var expected = FeaturePlanExecutor.Execute(
            schema, fixture, new FeatureQuery(Order: [new OrderTerm(order)]), token);
        var walked = new List<Feature>();
        var query = new FeatureQuery(Order: [new OrderTerm(order)], Limit: 2);

        // Bounded so a store that always issues a cursor fails instead of
        // looping; wide enough to walk a dataset of any size the suite meets.
        for (var page = 0; page < 1_000; page++)
        {
            var read = await store.QueryAsync(dataset, query, token).ConfigureAwait(false);
            walked.AddRange(read.Features);
            if (read.NextCursor is null)
            {
                break;
            }

            query = query with { Cursor = read.NextCursor, Offset = null };
        }

        Assert.Equal(Sequence(expected), walked.Select(Feature).ToArray());
    }

    private static async Task SameCountAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        foreach (var query in Plans(Fields.Of(schema)))
        {
            var actual = await FeatureReductionFallback.CountAsync(store, dataset, query, token).ConfigureAwait(false);
            var selected = FeaturePlanExecutor.Select(schema, fixture, query, token);
            Assert.Equal(selected.Count, actual);
        }
    }

    private static async Task SameDistinctAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        var fields = Fields.Of(schema);
        var distinct = new DistinctQuery(
            string.Equals(fields.Group, fields.Orderable, StringComparison.Ordinal)
                ? [fields.Group]
                : [fields.Group, fields.Orderable]);
        foreach (var query in Plans(Fields.Of(schema)))
        {
            var actual = await FeatureReductionFallback
                .DistinctAsync(store, dataset, query, distinct, token)
                .ConfigureAwait(false);
            var expected = FeatureReduction.Distinct(
                schema, FeaturePlanExecutor.Select(schema, fixture, query, token), distinct);
            Assert.Equal(expected.Fields, actual.Fields);
            Assert.Equal(expected.TotalCount, actual.TotalCount);
            Assert.Equal(Rows(expected.Rows), Rows(actual.Rows));
        }
    }

    private static async Task SameAggregateAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        var fields = Fields.Of(schema);
        foreach (var query in Plans(fields))
        {
            foreach (var aggregate in Aggregates(fields))
            {
                var actual = await FeatureReductionFallback
                    .AggregateAsync(store, dataset, query, aggregate, token)
                    .ConfigureAwait(false);
                var expected = FeatureReduction.Aggregate(
                    schema, FeaturePlanExecutor.Select(schema, fixture, query, token), aggregate);
                SameGroups(query, aggregate, expected, actual);
            }
        }
    }

    /// <summary>
    /// The same reduction under a plan that asks for its rows in group-key
    /// order, which is the only order a <c>GROUP BY</c> can return and the one
    /// the served statistics surface offers its store (ADR-0098 §3, and §7 as
    /// amended by SpatialEngine-u2x.9.2). Both directions are compared, because
    /// a store that puts a null-keyed group last ascending and first descending
    /// has two rules to get right and only one of them is the ascending default.
    /// </summary>
    private static async Task SameOrderedAggregateAsync(
        IFeatureStore store, string dataset, FeatureSchema schema, IReadOnlyList<Feature> fixture, CancellationToken token)
    {
        var fields = Fields.Of(schema);
        foreach (var direction in new[] { SortDirection.Ascending, SortDirection.Descending })
        {
            // The group order is a *text* order (ADR-0098 §3), and the
            // fixture's group key is built so a locale collation and an ordinal
            // comparison disagree about it (ADR-0121), so the grouped
            // reductions below are a collation case as well as a null-placement
            // one.
            var query = new FeatureQuery(Where: Matching(fields), Order: [new OrderTerm(fields.Group, direction)]);
            foreach (var aggregate in Aggregates(fields).Where(request => request.IsGrouped))
            {
                var actual = await FeatureReductionFallback
                    .AggregateAsync(store, dataset, query, aggregate, token)
                    .ConfigureAwait(false);
                var expected = FeatureReduction.Aggregate(
                    schema, FeaturePlanExecutor.Select(schema, fixture, query, token), aggregate);
                SameGroups(query, aggregate, expected, actual);
            }
        }
    }

    /// <summary>
    /// One reduction, compared whole. The groups are compared as a set, always:
    /// a store that groups by more than the group key (an order's own columns
    /// leaking into the <c>GROUP BY</c>, so the same key comes back twice),
    /// drops a group, or invents a row for a group that does not exist fails
    /// here. The <em>sequence</em> is compared against the orders the contract
    /// allows — the reference's first-seen order, and, when the plan's order is
    /// over the group key itself, the order the plan asked for, computed here
    /// with the reference's own value ordering (nulls last ascending, first
    /// descending) so a store's explicit <c>NULLS LAST</c> is measured against
    /// the same rule. Everything else — a count, a sum, an extreme, a
    /// percentile, a null, a key — is compared exactly.
    /// </summary>
    private static void SameGroups(FeatureQuery query, AggregateQuery aggregate, AggregatePage expected, AggregatePage actual)
    {
        Assert.Equal(expected.GroupFields, actual.GroupFields);
        Assert.Equal(expected.ValueNames, actual.ValueNames);
        Assert.Equal(expected.TotalCount, actual.TotalCount);

        var groups = Groups(expected.Groups);
        var answered = Groups(actual.Groups);
        Assert.Equal(groups.Order(StringComparer.Ordinal), answered.Order(StringComparer.Ordinal));
        Assert.True(
            answered.SequenceEqual(groups) || answered.SequenceEqual(OverGroupKey(query, aggregate, expected.Groups)),
            $"groups {string.Join(" ; ", answered)} are neither the reference's first-seen order nor the plan's");
    }

    /// <summary>
    /// The reference's groups in the plan's order, or the same sequence when
    /// the plan's order is not over the group key — an order a group row does
    /// not carry is not an order a store can return, so it never becomes an
    /// admissible answer.
    /// </summary>
    private static string[] OverGroupKey(FeatureQuery query, AggregateQuery aggregate, IReadOnlyList<AggregateGroup> groups) =>
        query.Order is { Count: > 0 } order
            && aggregate.GroupBy is { Count: > 0 } groupBy
            && order.All(term => groupBy.Contains(term.Field, StringComparer.Ordinal))
                ? Groups(Ordered(groups, aggregate.GroupBy, order))
                : Groups(groups);

    /// <summary>
    /// Groups put in the order the plan's terms ask for, term by term over the
    /// group key, compared with the reference's own value ordering: nulls last
    /// ascending, first descending, and strings compared ordinally. A group key
    /// is unique per group, so the order this produces is total and two stores
    /// cannot both be right about it.
    /// </summary>
    private static IEnumerable<AggregateGroup> Ordered(
        IEnumerable<AggregateGroup> groups, IReadOnlyList<string> groupBy, IReadOnlyList<OrderTerm> order)
    {
        var keys = groupBy.ToList();
        var positioned = groups.Select((group, position) => (Group: group, Position: position)).ToList();
        IOrderedEnumerable<(AggregateGroup Group, int Position)>? ordered = null;
        foreach (var term in order)
        {
            var index = keys.IndexOf(term.Field);
            if (index < 0)
            {
                continue;
            }

            var comparer = term.IsDescending ? Descending : Ascending;
            ordered = ordered is null
                ? positioned.OrderBy(pair => pair.Group.Key[index], comparer)
                : ordered.ThenBy(pair => pair.Group.Key[index], comparer);
        }

        if (ordered is null)
        {
            return positioned.OrderBy(pair => pair.Position).Select(pair => pair.Group);
        }

        // A store makes the order total by appending whatever of the group key
        // the plan's terms did not name, ascending — the group key is unique per
        // group, so this is a tie-break over the rest of the key and nothing
        // else. Leaving it out would make a correct store look like a
        // differently-ordered one.
        var named = order.Select(term => keys.IndexOf(term.Field)).Where(index => index >= 0).ToHashSet();
        foreach (var index in Enumerable.Range(0, keys.Count).Where(index => !named.Contains(index)))
        {
            ordered = ordered.ThenBy(pair => pair.Group.Key[index], Ascending);
        }

        return ordered.Select(pair => pair.Group);
    }

    /// <summary>The order value a descending term sorts by, as the reference orders values.</summary>
    private static IComparer<AttributeValue> Descending =>
        Comparer<AttributeValue>.Create((left, right) => -AttributeValueComparer.Instance.Compare(left, right));

    /// <summary>The order value an ascending term sorts by, as the reference orders values.</summary>
    private static IComparer<AttributeValue> Ascending => AttributeValueComparer.Instance;

    /// <summary>
    /// The plans every verb is compared over: the whole dataset, a box, the
    /// empty box, an ordered page — and the ones carrying the plan's predicate,
    /// because a restriction and the shaping members compose (ADR-0074 §4,
    /// §6). The predicate cases are where a dialect earns the right to be
    /// believed: a SQL <c>WHERE</c> that sorts or counts the surviving rows
    /// differently from the reference has not answered the same question, and
    /// neither has one that answers an empty match as something other than
    /// empty.
    /// </summary>
    private static IEnumerable<FeatureQuery> Plans(Fields fields)
    {
        yield return FeatureQuery.All;
        yield return new FeatureQuery(BoundingBox: fields.Box);
        yield return new FeatureQuery(BoundingBox: QueryFixture.EmptyBox);
        yield return FeatureQuery.All with { Order = [new OrderTerm(fields.Group)], Limit = 10 };
        yield return new FeatureQuery(Where: Matching(fields));
        yield return FeatureQuery.All with { Where = Matching(fields), Order = [new OrderTerm(fields.Numeric)], Limit = 2 };
        yield return new FeatureQuery(Where: Matching(fields), BoundingBox: QueryFixture.EmptyBox);
        yield return new FeatureQuery(Where: Absent(fields));
        yield return new FeatureQuery(Where: Nulls(fields));
    }

    /// <summary>
    /// A predicate that matches: the numeric field at or above zero, so the
    /// restriction is real without depending on the data the store happens to
    /// hold.
    /// </summary>
    private static Predicate.Compare Matching(Fields fields) =>
        new(new FieldRef(fields.Numeric), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("0"));

    /// <summary>A predicate that matches nothing, so the empty-set answers —
    /// a zero count, no distinct rows, no groups — are compared too.</summary>
    private static Predicate.Compare Absent(Fields fields) =>
        new(new FieldRef(fields.Numeric), ComparisonOperator.GreaterThan, Literal.FromNumber(double.MaxValue));

    /// <summary>An <c>IS NULL</c> predicate: the three-valued case a pushed
    /// down clause and an in-memory evaluator are most likely to disagree
    /// about, because SQL reads a null row as "not true" and never as
    /// "false".</summary>
    private static Predicate.IsNull Nulls(Fields fields) => new(new FieldRef(fields.Group), Negated: false);

    /// <summary>
    /// The reductions compared: every statistic, grouped and ungrouped, with a
    /// result name that differs from the field name so a caller can see the two
    /// are independent.
    /// </summary>
    private static IEnumerable<AggregateQuery> Aggregates(Fields fields)
    {
        var specs = new AggregateSpec[]
        {
            new(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
            new(AggregateStatistic.Count, fields.Numeric, "scored"),
            new(AggregateStatistic.Sum, fields.Numeric, "total"),
            new(AggregateStatistic.Average, fields.Numeric, "mean"),
            new(AggregateStatistic.Minimum, fields.Extreme, "first"),
            new(AggregateStatistic.Maximum, fields.Extreme, "last"),
            new(AggregateStatistic.Variance, fields.Numeric, "var"),
            new(AggregateStatistic.StdDev, fields.Numeric, "sd"),
            new(AggregateStatistic.PercentileContinuous, fields.Numeric, "p90", 0.9),
            new(AggregateStatistic.PercentileDiscrete, fields.Numeric, "p50", 0.5),
        };

        yield return new AggregateQuery(specs);
        yield return new AggregateQuery(specs, [fields.Group]);
        if (!string.Equals(fields.Group, fields.Orderable, StringComparison.Ordinal))
        {
            yield return new AggregateQuery(specs, [fields.Group, fields.Orderable]);
        }
    }

    /// <summary>
    /// The failures a plan must produce, whatever the store: an unknown field,
    /// a negative cap, a page start that is both a cursor and an offset, and a
    /// cursor this store did not issue for this plan. All four are
    /// <c>invalid.arguments</c>, never a silently different page.
    /// </summary>
    private static async Task RejectsBadPlansAsync(IFeatureStore store, string dataset, CancellationToken token)
    {
        var bad = new (string Why, FeatureQuery Plan, string? Cursor)[]
        {
            ("an unknown projected field", new FeatureQuery(Projection: ["nope"]), null),
            ("an unknown sort key", new FeatureQuery(Order: [new OrderTerm("nope")]), null),
            ("a negative row cap", new FeatureQuery(Limit: -1), null),
            ("a cursor and an offset together", new FeatureQuery(Cursor: "v1.0.abc", Offset: 1), null),
            ("a cursor another plan could not have been issued for", FeatureQuery.All, "v1.5.deadbeefdeadbeef"),
            ("a cursor that is not a cursor", FeatureQuery.All, "nonsense"),
        };

        foreach (var (why, plan, cursor) in bad)
        {
            var failure = await Assert.ThrowsAsync<SpatialException>(
                () => store.QueryAsync(dataset, cursor is null ? plan : plan with { Cursor = cursor }, token));
            Assert.Equal(SpatialException.InvalidArguments, failure.Code);
            Assert.False(string.IsNullOrWhiteSpace(failure.Message), why);
        }
    }

    /// <summary>
    /// The schema fields the suite exercises, derived from whatever store it is
    /// run against: a key to order by, a nullable-or-first field to group by, a
    /// numeric field for the numeric statistics, a comparable field for the
    /// extremes, a text field to sort by ordinally, a projection, and a box
    /// over the dataset's own geometry so the spatial restriction has rows to
    /// select.
    /// </summary>
    private sealed record Fields(
        string Key,
        string Group,
        string Numeric,
        string Extreme,
        string Text,
        string Orderable,
        IReadOnlyList<string> Projection,
        BoundingBox Box)
    {
        public static Fields Of(FeatureSchema schema)
        {
            var attributes = schema.Fields.Where(field => field.Kind != AttributeKind.Geometry).ToArray();
            var numeric = attributes.First(field => field.Kind is AttributeKind.Int64 or AttributeKind.Double);
            if (attributes.Length > 1 && string.Equals(numeric.Name, attributes[0].Name, StringComparison.Ordinal))
            {
                // Keep the projection's two fields distinct: a projection that
                // names a field twice is rejected, so a second numeric field is
                // preferred and the first is the projection's other half.
                numeric = attributes.First(field =>
                    field.Kind is AttributeKind.Int64 or AttributeKind.Double
                    && !string.Equals(field.Name, attributes[0].Name, StringComparison.Ordinal));
            }

            // The group key is a nullable field that is neither the key nor the
            // numeric, so the groups it makes hold several rows and include a
            // null-keyed group of its own — the shape a reduction is tempted to
            // get wrong. Grouping by the numeric itself would make every group
            // a single row, which answers nothing about a percentile that has
            // to interpolate or a variance that has to divide.
            var group = attributes.FirstOrDefault(field =>
                field.Nullable
                && !string.Equals(field.Name, attributes[0].Name, StringComparison.Ordinal)
                && !string.Equals(field.Name, numeric.Name, StringComparison.Ordinal));
            if (group.Name is null)
            {
                group = numeric;
            }

            var extremes = attributes.FirstOrDefault(field => field.Kind == AttributeKind.String);
            if (extremes.Name is null)
            {
                extremes = numeric;
            }

            return new Fields(
                attributes[0].Name,
                group.Name,
                numeric.Name,
                extremes.Name,
                extremes.Name,
                numeric.Name,
                [attributes[0].Name, numeric.Name],
                Enclosing(schema));
        }

        /// <summary>The dataset's own extent, so the box pre-filter selects rows
        /// rather than accidentally selecting none.</summary>
        private static BoundingBox Enclosing(FeatureSchema schema)
        {
            var index = schema.Fields.ToList().FindIndex(field => field.Kind == AttributeKind.Geometry);
            return index < 0
                ? QueryFixture.EmptyBox
                : new BoundingBox(double.MinValue / 2, double.MinValue / 2, double.MaxValue / 2, double.MaxValue / 2);
        }
    }

    private static string[] Sequence(FeatureQueryPage page) => page.Features.Select(Feature).ToArray();

    /// <summary>A row's identity and its values, so a compared answer is the
    /// data and not a reference to it.</summary>
    private static string Feature(Feature feature) =>
        $"{feature.Id}={string.Join("|", feature.Attributes.Select(value => value.IsNull ? "-" : $"{value.Kind}:{value}"))}";

    private static string[] Rows(IEnumerable<IReadOnlyList<AttributeValue>> rows) =>
        rows.Select(row => string.Join("|", row.Select(Text))).ToArray();

    private static string[] Groups(IEnumerable<AggregateGroup> groups) =>
        groups.Select(group => string.Join("|", group.Key.Concat(group.Values).Select(Text))).ToArray();

    /// <summary>
    /// One reduced value as text, for the whole-answer comparison. Everything
    /// is rendered exactly; a <em>double</em> reduction is rendered to twelve
    /// significant digits, because that is the precision a pushed-down
    /// statistic can honestly promise: Postgres reduces in <c>numeric</c> and
    /// converts once at the end, so <c>STDDEV_SAMP</c> comes back as the last
    /// representable digit either side of the reference's <c>sqrt</c> of the
    /// same variance. A tolerance the size of the double's own representation
    /// is not a licence to be approximately right — it is the statement that a
    /// decimal engine and a binary one agree to the precision a JSON client can
    /// tell. Every other value — a count, a sum, an extreme, a null, a key —
    /// is compared exactly, because those have no such slack.
    /// </summary>
    private static string Text(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Double => value.DoubleValue.ToString("G12", CultureInfo.InvariantCulture),
        _ => value.IsNull ? "-" : value.ToString(),
    };
}
