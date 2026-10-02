using System.Globalization;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The dataset description cache (ADR-0151, the SQL Server half of ADR-0122)
/// against a real database: every read face describes its dataset before it
/// can compile any T-SQL, and describing runs several catalogue reads. A walk
/// of <c>N</c> pages therefore pays <c>N</c> of them to learn the same schema
/// <c>N</c> times — a fixed, page-size-independent cost, and a round trip per
/// page on the wire.
///
/// <para>
/// The claim is a count, so it is measured as one: the store reports how many
/// descriptions it has read from the catalogue, and a walk that issues one is
/// O(1) in pages. Around it, the rest of the story, the same one ADR-0122
/// records for PostGIS: the description is invalidated by every write the store
/// owns (create, ingest, append, edit and the end of a transaction), a
/// description that failed to be read is not remembered, and a schema changed
/// <em>outside</em> the store is picked up when the entry expires.
/// </para>
/// </summary>
public sealed class SqlServerDescriptionCacheTests : IClassFixture<SqlServerContainerFixture>
{
    private const int Rows = 40;
    private const int PageSize = 4;

    private readonly SqlServerContainerFixture _fixture;

    public SqlServerDescriptionCacheTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, nullable: false),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>The schema a created table replaces the seeded one with, so a stale description is visible.</summary>
    private static readonly FeatureSchema ReplacedSchema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, nullable: false),
        new FieldDefinition("label", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>The ingested schema, without an <c>id</c>: the ingest adds the identity column itself.</summary>
    private static readonly FeatureSchema IngestedSchema = new(
    [
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private static Feature Row(long id, FeatureSchema schema) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString("row"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id % 180, (id % 90) - 45)),
        ]);

    /// <summary>A row of the ingested schema, which has no identity column of its own.</summary>
    private static Feature IngestRow() => new(
        new FeatureId("1"),
        IngestedSchema,
        [
            AttributeValue.FromString("row"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(13, 52)),
        ]);

    private static string Unique(string prefix = "described") => $"dbo.{prefix}_{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// The table is created by hand rather than through <c>CreateAsync</c>,
    /// because the pushdown the walk measures is decided by the dataset's
    /// identity: a table with a primary key is one whose order can be made
    /// total. The rows are written through the store, so the write path is the
    /// store's.
    /// </summary>
    private static async Task<string> SeededAsync(SqlServerTestContext context, int rows = Rows)
    {
        var dataset = Unique();
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id bigint NOT NULL PRIMARY KEY, name nvarchar(100) NULL, geometry geometry NULL)");
        var batch = new List<Feature>(rows);
        for (var id = 1L; id <= rows; id++)
        {
            batch.Add(Row(id, Schema));
        }

        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, batch));
        return dataset;
    }

    /// <summary>
    /// The claim itself: a walk of many pages issues one description read, not
    /// one per page. The walk is asserted to have read every row in order first,
    /// so a store that answered cheaply by answering nothing cannot pass it.
    /// </summary>
    [SkippableFact]
    public async Task A_paged_walk_reads_the_description_once_and_not_once_a_page()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync(context);
        var before = context.Store.DescriptionReads;

        var seen = new List<long>(Rows);
        var pages = 0;
        var query = new FeatureQuery(Order: [new OrderTerm("id")]) with { Limit = PageSize };
        while (true)
        {
            var read = await context.Store.QueryAsync(dataset, query);
            seen.AddRange(read.Features.Select(feature => feature["id"].Int64Value));
            pages++;
            if (!read.HasMore)
            {
                break;
            }

            query = query with { Cursor = read.NextCursor, Offset = null };
        }

        Assert.Equal(Rows, seen.Count);
        Assert.Equal(seen.OrderBy(id => id).ToArray(), seen.ToArray());
        Assert.Equal(Rows / PageSize, pages);
        Assert.Equal(1, context.Store.DescriptionReads - before);
    }

    /// <summary>
    /// The two counters the store reports: a description that has been read is
    /// answered from the store thereafter, so the catalogue is not asked the
    /// same question twice.
    /// </summary>
    [SkippableFact]
    public async Task A_description_is_discovered_once_and_answered_from_the_cache_thereafter()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync(context);
        var before = context.Store.DescriptionReads;

        var first = await context.Store.DescribeAsync(dataset);
        var second = await context.Store.DescribeAsync(dataset);

        Assert.Equal(first, second);
        Assert.Equal(1, context.Store.CachedDescriptions);
        Assert.Equal(1, context.Store.DescriptionReads - before);
    }

    /// <summary>
    /// A create drops the description of the dataset it created, so a table
    /// dropped out of band and recreated through the store is described as the
    /// one that was created — not as the one the store last saw. This is the
    /// invalidation that matters most, because a stale description here is not
    /// a stale number but a stale <em>schema</em>.
    /// </summary>
    [SkippableFact]
    public async Task A_created_dataset_is_described_as_it_was_created_and_not_as_it_was_before()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync(context, rows: 2);
        await context.Store.DescribeAsync(dataset);

        await context.ExecuteAsync($"DROP TABLE {dataset}");
        await context.Store.CreateAsync(dataset, new FeatureBatch(ReplacedSchema, [Row(1, ReplacedSchema)]), 4326);

        var description = await context.Store.DescribeAsync(dataset);
        Assert.Contains("label", description.Schema.Fields.Select(field => field.Name));
        Assert.DoesNotContain("name", description.Schema.Fields.Select(field => field.Name));
    }

    /// <summary>An append drops the description of the dataset it wrote to.</summary>
    [SkippableFact]
    public async Task A_write_drops_the_description_of_the_dataset_it_wrote_to()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync(context);
        await context.Store.DescribeAsync(dataset);
        Assert.Equal(1, context.Store.CachedDescriptions);

        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Row(Rows + 1, Schema)]));

        Assert.Equal(0, context.Store.CachedDescriptions);
    }

    /// <summary>
    /// An edit drops it too — an add, an update and a delete alike, because
    /// each is a write the store made and none of them can be described by a
    /// description taken before them.
    /// </summary>
    [SkippableFact]
    public async Task An_edit_drops_the_description_of_the_dataset_it_edited()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync(context, rows: 4);

        foreach (var edit in new (string Name, Func<Task> Apply)[]
        {
            ("add", () => context.Editor.AddAsync(dataset, new FeatureBatch(Schema, [Row(5, Schema)]))),
            ("update", () => context.Editor.UpdateAsync(dataset, new FeatureBatch(Schema, [Row(5, Schema)]))),
            ("delete", () => context.Editor.DeleteAsync(dataset, [new FeatureId("1")])),
        })
        {
            await context.Store.DescribeAsync(dataset);
            Assert.Equal(1, context.Store.CachedDescriptions);

            await edit.Apply();

            Assert.Equal(0, context.Store.CachedDescriptions);
        }
    }

    /// <summary>
    /// A transaction drops the descriptions of the datasets it wrote to when it
    /// ends, whichever way it ends: a commit made them visible and a rollback
    /// decided they never happened, and a description read before either
    /// decision cannot be trusted to describe the dataset after it.
    /// </summary>
    [SkippableFact]
    public async Task A_finished_transaction_drops_the_descriptions_it_wrote_through()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync(context, rows: 4);

        foreach (var commit in new[] { true, false })
        {
            await context.Store.DescribeAsync(dataset);
            Assert.Equal(1, context.Store.CachedDescriptions);

            var transaction = await context.Store.BeginAsync();
            await context.Store.WriteAsync(
                dataset,
                new FeatureBatch(Schema, [Row(commit ? 90 : 91, Schema)]),
                transaction);
            Assert.Equal(0, context.Store.CachedDescriptions);

            Assert.True(commit
                ? await context.Store.CommitAsync(transaction)
                : await context.Store.RollbackAsync(transaction));
            Assert.Equal(0, context.Store.CachedDescriptions);
        }
    }

    /// <summary>
    /// A description that could not be read is not remembered: a dataset that
    /// did not exist a moment ago is a <c>not.found</c>, and the ingest that
    /// creates it must not inherit that answer.
    /// </summary>
    [SkippableFact]
    public async Task A_description_that_failed_is_not_remembered_and_an_ingest_is_described_afterwards()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = Unique("ingested");
        var before = context.Store.DescriptionReads;

        var failure = await Assert.ThrowsAsync<SpatialException>(() => context.Store.DescribeAsync(dataset));
        Assert.Equal(SpatialException.NotFound, failure.Code);
        Assert.Equal(0, context.Store.CachedDescriptions);

        var outcome = await context.Ingest.IngestAsync(
            new IngestRequest(dataset, 4326), [new FeatureBatch(IngestedSchema, [IngestRow()])]);
        Assert.Equal(1, outcome.Features);

        var description = await context.Store.DescribeAsync(dataset);
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(4326, description.Srid);

        // Two catalogue reads, not one: the description that failed went to the
        // catalogue too, which is why the count is the honest instrument. What
        // it did not do is leave an entry behind for the read that followed it.
        Assert.Equal(2, context.Store.DescriptionReads - before);
        Assert.Equal(1, context.Store.CachedDescriptions);
    }

    /// <summary>
    /// A cancelled read caches nothing, so the next reader asks the database
    /// again rather than inheriting a description that was never finished being
    /// read.
    /// </summary>
    [SkippableFact]
    public async Task A_cancelled_description_is_not_remembered()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync(context);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        // SqlClient surfaces the cancellation on its own token, so what is
        // asserted here is that the read was cancelled and that it left nothing
        // behind for the read that follows it.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Store.DescribeAsync(dataset, cancellation.Token));
        Assert.Equal(0, context.Store.CachedDescriptions);

        var description = await context.Store.DescribeAsync(dataset);
        Assert.Equal(dataset, description.Id);
    }

    /// <summary>
    /// A schema changed outside the store — a hand-run <c>ALTER TABLE</c>, a
    /// migration by another process — is picked up when the entry expires,
    /// rather than needing the host restarted. Within the window the store
    /// answers with the description it holds, which is the cost this record
    /// accepts and the test states: a column added out of band is not in the
    /// schema until the entry expires.
    /// </summary>
    [SkippableFact]
    public async Task A_schema_changed_outside_the_store_is_described_once_the_description_expires()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        var clock = new MovableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await using var context = SqlServerTestContext.Create(
            _fixture.ConnectionString, descriptionCacheTtl: TimeSpan.FromMinutes(1), clock: clock);
        var dataset = await SeededAsync(context, rows: 2);

        Assert.Empty(Columns(await context.Store.DescribeAsync(dataset), "added"));
        await context.ExecuteAsync($"ALTER TABLE {dataset} ADD added nvarchar(50) NULL");

        // The held description is the one the store answers with, and it does
        // not know the column until the entry expires.
        Assert.Empty(Columns(await context.Store.DescribeAsync(dataset), "added"));
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(["added"], Columns(await context.Store.DescribeAsync(dataset), "added"));
    }

    /// <summary>
    /// The cache turned off is the store's earlier behaviour: every read
    /// describes, and nothing is held. It is the setting for a database whose
    /// schema moves on a schedule the store cannot see.
    /// </summary>
    [SkippableFact]
    public async Task A_cache_turned_off_describes_every_read_and_holds_nothing()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(
            _fixture.ConnectionString, descriptionCacheTtl: TimeSpan.Zero);
        var dataset = await SeededAsync(context, rows: 2);
        var before = context.Store.DescriptionReads;

        await context.Store.DescribeAsync(dataset);
        await context.Store.DescribeAsync(dataset);
        await context.Store.QueryAsync(dataset, new FeatureQuery(Order: [new OrderTerm("id")]));

        Assert.Equal(0, context.Store.CachedDescriptions);
        Assert.Equal(3, context.Store.DescriptionReads - before);
    }

    private static string[] Columns(DatasetDescription description, string name) =>
        description.Schema.Fields
            .Where(field => string.Equals(field.Name, name, StringComparison.Ordinal))
            .Select(field => field.Name)
            .ToArray();

    /// <summary>A clock the test moves by hand, so expiry needs no sleeping.</summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
