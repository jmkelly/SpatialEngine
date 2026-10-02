using Npgsql;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.QueryConformance;
using Spatial.Querying;
using Spatial.Stores.PostGIS;
using Spatial.Stores.PostGIS.Configuration;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The PostGIS provider against the shared pushdown-equals-reference suite
/// (ADR-0098): the plan read, the count, the distinct set and the grouped
/// aggregate are pushed into SQL, and every one of them must answer exactly
/// what the shared reference executor answers over the same fixture — including
/// the rows a dialect is tempted to get wrong (ties, nulls, single-row groups
/// and the empty set). Skips with an explicit reason without Docker.
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisQueryConformanceTests : IClassFixture<PostgisDatabaseFixture>
{
    private readonly PostgisDatabaseFixture _fixture;

    public PostgisQueryConformanceTests(PostgisDatabaseFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_pushed_down_answers_match_the_reference_over_the_conformance_fixture()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.fixture_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        await QueryConformanceSuite.RunAsync(context.Store, dataset);
    }

    /// <summary>
    /// A composite order over a table that <em>has</em> a primary key, which
    /// is the one shape where this store's <c>ORDER BY</c> reaches SQL at all: a
    /// dataset with no identity has no tie-break to append, so the plan is
    /// ordered by the reference over the rows the read returned (ADR-0097).
    /// Every requested key is written into the statement, each a tie-break over
    /// the one before it, so the pushed sequence is the reference's composite
    /// order (ADR-0127) — the case the shared suite reaches now that a pushed
    /// row keeps the identity the key gave it, on a table whose key is a column
    /// of its own (`A_pushed_composite_order_answers_the_reference_row_for_row`)
    /// rather than one of the plan's own fields
    /// (`The_pushed_page_matches_the_reference_over_an_identity_carrying_table`,
    /// SpatialEngine-u2x.55). The rows are therefore compared by their own
    /// values, and the identities are pinned by that other case.
    /// </summary>
    [SkippableFact]
    public async Task A_pushed_composite_order_answers_the_reference_row_for_row()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "public.keyed";
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        // `CreateAsync` makes a table without a primary key, so the table is
        // made by hand: a key is the one thing a pushed order needs and the
        // sample-based create does not add.
        await context.ExecuteAsync(
            $"DROP TABLE IF EXISTS {dataset}; CREATE TABLE {dataset} ("
            + "\"fid\" bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY, "
            + "\"id\" bigint, \"category\" text NULL, \"score\" bigint NULL, "
            + "\"ratio\" double precision NULL, \"name\" text NULL, \"shape\" geometry(Geometry, 4326))");
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        var scan = await context.Store.ScanAsync(dataset);
        var rows = scan.SelectMany(batch => batch.Features).ToList();
        var plan = new FeatureQuery(Order: [new OrderTerm("category"), new OrderTerm("score", SortDirection.Descending)]);

        // Category first (ordinal, nulls last) and the score only within a
        // category: the two keys order the answer against each other, so a
        // statement that applied only the second would return a different one.
        Assert.Equal(["3", "4", "6", "2", "1", "5"], Ids(FeaturePlanExecutor.Execute(scan[0].Schema, rows, plan)));
        Assert.Equal(
            ["3", "4", "6", "2", "1", "5"],
            Ids(await context.Store.QueryAsync(dataset, plan)));

        // And the same composite order cut into a page, which is the shape a
        // large layer is read in: the page is a prefix of the same sequence.
        var paged = await context.Store.QueryAsync(dataset, plan with { Limit = 3, Offset = 1 });
        Assert.Equal(["4", "6", "2"], Ids(paged));
        Assert.Equal(6, paged.TotalCount);

        await context.ExecuteAsync($"DROP TABLE IF EXISTS {dataset}");
    }

    /// <summary>
    /// A pushed read of a keyed table names each feature by its key, not by the
    /// row it happened to be returned in. The ordinary read projects nothing,
    /// so the identity column is already among the columns the read carries and
    /// nothing is appended: the identity is the primary key's position in what
    /// was read, not the set of columns that were appended to it
    /// (SpatialEngine-u2x.55, ADR-0124 §8 — the rule the SQL Server reader
    /// already follows). Taking the appended set would answer every pushed read
    /// of a keyed table with <c>0,1,2,…</c>, and a read that renumbers its rows
    /// is the store's own page contract answering a different question
    /// (ADR-0097).
    /// </summary>
    [SkippableFact]
    public async Task A_pushed_read_of_a_keyed_table_names_each_feature_by_its_key()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "public.keyedids";
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await KeyedTableAsync(context, dataset);
        try
        {
            var keys = await context.ScalarAsync<long[]>($"SELECT array_agg(id ORDER BY id) FROM {dataset}");
            Assert.NotNull(keys);
            var named = keys.Select(key => key.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

            var page = await context.Store.QueryAsync(dataset, FeatureQuery.All);
            Assert.Equal(
                named,
                page.Features.Select(feature => feature.Id.Value).ToArray());

            // The same answer through a projected read, where the identity
            // column is not in the projection at all and is appended for the
            // mapper.
            var projected = await context.Store.QueryAsync(dataset, FeatureQuery.All with { Projection = ["id", "category"] });
            Assert.Equal(named, projected.Features.Select(feature => feature.Id.Value).ToArray());
            Assert.Equal(["id", "category"], projected.Features.First().Schema.Fields.Select(field => field.Name).ToArray());
        }
        finally
        {
            await context.ExecuteAsync($"DROP TABLE IF EXISTS {dataset}");
        }
    }

    /// <summary>
    /// The same shared suite over a table that <em>has</em> a primary key, which
    /// is the one shape where this store pushes the order and the page rather
    /// than finishing the plan in process (ADR-0124 §2, ADR-0131). The suite
    /// compares feature <em>identities</em>, so over a table with no key the
    /// pushed path is never taken and never measured (SpatialEngine-u2x.55).
    /// </summary>
    [SkippableFact]
    public async Task The_pushed_page_matches_the_reference_over_an_identity_carrying_table()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "public.keyedconformance";
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await KeyedTableAsync(context, dataset);
        try
        {
            await QueryConformanceSuite.RunAsync(context.Store, dataset);
        }
        finally
        {
            await context.ExecuteAsync($"DROP TABLE IF EXISTS {dataset}");
        }
    }

    /// <summary>
    /// The two answers a pushed reduction gives for a selection with no rows in
    /// it, which is the one case the dialect and the reference state differently
    /// and the identity-carrying suite above is what found (ADR-0098 §3,
    /// ADR-0128). A count of no rows is a <em>zero</em> — SQL's ungrouped
    /// <c>COUNT(*)</c> already says zero and reading it as a null made the
    /// pushed answer differ from the reference's for every plan that selects
    /// nothing — and a page of an ungrouped reduction that lands past its one
    /// group is past the last group, so it is <em>no groups</em> rather than the
    /// one group of nulls.
    /// </summary>
    [SkippableFact]
    public async Task A_pushed_reduction_of_an_empty_selection_answers_a_zero_row_count_and_pages_to_nothing()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "public.keyedempty";
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await KeyedTableAsync(context, dataset);
        try
        {
            var empty = new FeatureQuery(BoundingBox: QueryFixture.EmptyBox);
            var specs = new[]
            {
                new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
                new AggregateSpec(AggregateStatistic.Sum, "score", "total"),
            };

            var reduced = await context.Store.AggregateAsync(dataset, empty, new AggregateQuery(specs));
            var group = Assert.Single(reduced.Groups);
            Assert.Equal(AttributeValue.FromInt64(0), group.Values[0]);
            Assert.True(group.Values[1].IsNull);

            // The same reduction paged past its only group: the reference pages
            // the one group and hands back nothing, so the store must too.
            var paged = await context.Store.AggregateAsync(
                dataset, empty, new AggregateQuery(specs, Limit: 1, Offset: 1));
            Assert.Empty(paged.Groups);
            Assert.False(paged.HasMore);
        }
        finally
        {
            await context.ExecuteAsync($"DROP TABLE IF EXISTS {dataset}");
        }
    }

    /// <summary>
    /// A table with a primary key, made by hand because <c>CreateAsync</c> makes
    /// one without: a key is the one thing a pushed page needs and the
    /// sample-based create does not add.
    /// </summary>
    private static async Task KeyedTableAsync(PostgisTestContext context, string dataset)
    {
        // The key is the fixture's own first field, so the discovered schema is
        // the fixture's schema and the identity column is one the plan's
        // ordinary (unprojected) read already carries.
        await context.ExecuteAsync(
            $"DROP TABLE IF EXISTS {dataset}; CREATE TABLE {dataset} ("
            + "\"id\" bigint NOT NULL PRIMARY KEY, "
            + "\"category\" text NULL, \"score\" bigint NULL, "
            + "\"ratio\" double precision NULL, \"name\" text NULL, \"shape\" geometry(Geometry, 4326))");
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));
    }

    private static string[] Ids(FeatureQueryPage page) =>
        [.. page.Features.Select(feature => feature["id"].Int64Value.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    [SkippableFact]
    public async Task A_paged_read_returns_the_page_the_store_was_asked_for_and_the_rest_as_a_cursor()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.paged_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        var seen = new List<long>();
        var page = await context.Store.QueryAsync(dataset, new FeatureQuery(Order: [new OrderTerm("id")], Limit: 2));
        seen.AddRange(page.Features.Select(feature => feature["id"].Int64Value));
        Assert.Equal(6, page.TotalCount);

        while (page.NextCursor is not null)
        {
            page = await context.Store.QueryAsync(
                dataset, new FeatureQuery(Order: [new OrderTerm("id")], Limit: 2, Cursor: page.NextCursor));
            seen.AddRange(page.Features.Select(feature => feature["id"].Int64Value));
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(seen.OrderBy(id => id), seen);
        Assert.Equal(6, seen.Distinct().Count());
    }

    /// <summary>
    /// The database's own collation, read once per store: it is what decides
    /// whether a pushed-down sort key over a text column carries
    /// <c>COLLATE "C"</c> (ADR-0121). Read against the database rather than
    /// asserted, because the fixture container may be created with a locale
    /// collation or with <c>C</c> and the store has to be right about either.
    /// </summary>
    [SkippableFact]
    public async Task The_collation_the_sort_keys_are_written_against_is_the_databases_own()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var storage = Storage();
        Assert.Equal(await ContainerCollationAsync(), await storage.DatabaseCollationAsync(CancellationToken.None));

        // Read once and cached: a property of the database is not a property of
        // a query, and the second call is answered without a round trip.
        Assert.Equal(await storage.DatabaseCollationAsync(CancellationToken.None), await storage.DatabaseCollationAsync(CancellationToken.None));
    }

    /// <summary>
    /// A cancelled probe caches nothing, so the next reader asks the database
    /// again instead of inheriting a half-read value — and the plan that needed
    /// the collation still answers the reference's answer afterwards.
    /// </summary>
    [SkippableFact]
    public async Task A_cancelled_collation_probe_leaves_the_next_read_to_ask_again()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.collate_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await using var storage = Storage();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => storage.DatabaseCollationAsync(cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);

        // Same storage, live token: the value is read, not remembered.
        Assert.Equal(await ContainerCollationAsync(), await storage.DatabaseCollationAsync(CancellationToken.None));

        // And the store's own answer is the reference's, collation and all.
        var page = await context.Store.QueryAsync(dataset, new FeatureQuery(Order: [new OrderTerm("category")]));
        Assert.Equal(
            ["A", "A", "_c", "a", "a", "-"],
            page.Features.Select(feature => feature["category"].IsNull ? "-" : feature["category"].StringValue).ToArray());
    }

    private PostgisStorage Storage() =>
        new(PostgisConnectionConfiguration.FromConnectionString(_fixture.ConnectionString));

    private async Task<string?> ContainerCollationAsync()
    {
        await using var dataSource = NpgsqlDataSource.Create(_fixture.ConnectionString);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT datcollate FROM pg_database WHERE datname = current_database()";
        return (string?)await command.ExecuteScalarAsync();
    }
}
