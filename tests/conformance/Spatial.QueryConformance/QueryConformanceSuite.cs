using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Querying;

namespace Spatial.QueryConformance;

/// <summary>
/// The pushdown-equals-reference suite (ADR-0084 §3). A store's own answer
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

        foreach (var query in queries)
        {
            var actual = await store.QueryAsync(dataset, query, token).ConfigureAwait(false);
            var expected = FeaturePlanExecutor.Execute(schema, fixture, query, token);
            Assert.Equal(Sequence(expected), Sequence(actual));
        }
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
        var order = Fields.Of(schema).Orderable;
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
        foreach (var query in Plans(Fields.Of(schema)))
        {
            foreach (var aggregate in Aggregates(Fields.Of(schema)))
            {
                var actual = await FeatureReductionFallback
                    .AggregateAsync(store, dataset, query, aggregate, token)
                    .ConfigureAwait(false);
                var expected = FeatureReduction.Aggregate(
                    schema, FeaturePlanExecutor.Select(schema, fixture, query, token), aggregate);
                Assert.Equal(expected.GroupFields, actual.GroupFields);
                Assert.Equal(expected.ValueNames, actual.ValueNames);
                Assert.Equal(expected.TotalCount, actual.TotalCount);
                Assert.Equal(Groups(expected.Groups), Groups(actual.Groups));
            }
        }
    }

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
    /// extremes, a projection, and a box over the dataset's own geometry so the
    /// spatial restriction has rows to select.
    /// </summary>
    private sealed record Fields(
        string Key,
        string Group,
        string Numeric,
        string Extreme,
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

            var nullable = attributes.FirstOrDefault(field => field.Nullable);
            if (nullable.Name is null)
            {
                nullable = numeric;
            }

            var extremes = attributes.FirstOrDefault(field => field.Kind == AttributeKind.String);
            if (extremes.Name is null)
            {
                extremes = numeric;
            }

            return new Fields(
                attributes[0].Name,
                nullable.Name,
                numeric.Name,
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
        rows.Select(row => string.Join("|", row.Select(value => value.IsNull ? "-" : value.ToString()))).ToArray();

    private static string[] Groups(IEnumerable<AggregateGroup> groups) =>
        groups.Select(group => string.Join("|", group.Key.Concat(group.Values).Select(value => value.IsNull ? "-" : value.ToString())))
            .ToArray();
}
