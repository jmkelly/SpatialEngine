using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The containerised attachment-path tests (T-088, ADR-0065 §2): add/list/
/// get/update/delete through <c>PostgisAttachmentStore</c> against the
/// <c>bytea</c> sidecar table — success, per-id failure and cancellation.
/// Every test skips with an explicit reason when no Docker daemon is
/// available.
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisAttachmentIntegrationTests : IClassFixture<PostgisDatabaseFixture>
{
    private const string FoldDataset = "public.attach_fold";

    private readonly PostgisDatabaseFixture _fixture;

    public PostgisAttachmentIntegrationTests(PostgisDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Add_then_list_and_get_round_trip()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var added = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("photo.png", "image/png", [0x89, 0x50, 0x4E, 0x47], "streetscape"));

        Assert.Equal(1, added.Id);
        Assert.Equal("photo.png", added.Name);
        Assert.Equal("image/png", added.ContentType);
        Assert.Equal(4, added.Size);
        Assert.Equal("streetscape", added.Keywords);

        var listed = await context.Attachments.ListAsync("public.attach_target", new FeatureId("1"));
        Assert.Equal([added], listed);

        // The sibling feature stores nothing.
        Assert.Empty(await context.Attachments.ListAsync("public.attach_target", new FeatureId("2")));

        var fetched = await context.Attachments.GetAsync("public.attach_target", new FeatureId("1"), added.Id);
        Assert.Equal(added, fetched.Descriptor);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], fetched.Content);
    }

    [SkippableFact]
    public async Task Attachment_ids_assign_per_feature_from_one()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var first = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));
        var second = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("b.bin", "application/octet-stream", [2]));
        var other = await context.Attachments.AddAsync("public.attach_target", new FeatureId("2"), new FeatureAttachmentWrite("c.bin", "application/octet-stream", [3]));

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(1, other.Id);
    }

    [SkippableFact]
    public async Task Attachments_survive_a_fresh_store_over_the_same_database()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var added = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1, 2, 3]));

        // A new store instance over the same database sees the sidecar rows:
        // persistence, not process memory.
        await using var reopened = PostgisTestContext.Create(_fixture.ConnectionString);
        var fetched = await reopened.Attachments.GetAsync("public.attach_target", new FeatureId("1"), added.Id);

        Assert.Equal(added, fetched.Descriptor);
        Assert.Equal([1, 2, 3], fetched.Content);
    }

    [SkippableFact]
    public async Task An_empty_content_type_defaults_to_octet_stream()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var added = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "", [1]));

        Assert.Equal("application/octet-stream", added.ContentType);
    }

    [SkippableFact]
    public async Task Update_replaces_content_and_metadata()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var added = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));

        var updated = await context.Attachments.UpdateAsync("public.attach_target", new FeatureId("1"), added.Id, new FeatureAttachmentWrite("b.png", "image/png", [9, 10], "front"));

        Assert.Equal(added.Id, updated.Id);
        Assert.Equal("b.png", updated.Name);
        Assert.Equal("image/png", updated.ContentType);
        Assert.Equal(2, updated.Size);
        Assert.Equal("front", updated.Keywords);

        var fetched = await context.Attachments.GetAsync("public.attach_target", new FeatureId("1"), added.Id);
        Assert.Equal(updated, fetched.Descriptor);
        Assert.Equal([9, 10], fetched.Content);
    }

    [SkippableFact]
    public async Task Update_of_a_missing_attachment_is_a_not_found()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.UpdateAsync("public.attach_target", new FeatureId("1"), 999, new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1])));
        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [SkippableFact]
    public async Task Delete_removes_and_reports_per_id_outcomes()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var first = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));
        var second = await context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("b.bin", "application/octet-stream", [2]));

        var outcomes = await context.Attachments.DeleteAsync(
            "public.attach_target", new FeatureId("1"), [first.Id, 999]);

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Succeeded);
        Assert.Equal(first.Id, outcomes[0].Id);
        Assert.False(outcomes[1].Succeeded);
        Assert.Equal(SpatialException.NotFound, outcomes[1].ErrorCode);

        var remaining = await context.Attachments.ListAsync("public.attach_target", new FeatureId("1"));
        Assert.Equal([second], remaining);
    }

    [SkippableFact]
    public async Task An_unknown_dataset_is_a_not_found()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.AddAsync("public.attach_missing", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1])));
        Assert.Equal(SpatialException.NotFound, add.Code);

        var list = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.ListAsync("public.attach_missing", new FeatureId("1")));
        Assert.Equal(SpatialException.NotFound, list.Code);

        var get = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.GetAsync("public.attach_missing", new FeatureId("1"), 1));
        Assert.Equal(SpatialException.NotFound, get.Code);
    }

    [SkippableFact]
    public async Task An_attachment_on_an_unknown_feature_is_a_not_found()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.AddAsync("public.attach_target", new FeatureId("404"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1])));
        Assert.Equal(SpatialException.NotFound, add.Code);

        var get = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.GetAsync("public.attach_target", new FeatureId("404"), 1));
        Assert.Equal(SpatialException.NotFound, get.Code);
    }

    [SkippableFact]
    public async Task A_missing_attachment_is_a_not_found()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.GetAsync("public.attach_target", new FeatureId("1"), 999));
        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [SkippableFact]
    public async Task A_malformed_feature_identity_is_invalid_arguments()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        // public.attach_target has one identity column; a two-part identity cannot be parsed.
        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.ListAsync("public.attach_target", new FeatureId("1|2")));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task Attachments_on_a_table_without_a_primary_key_are_invalid_arguments()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        // public.roads is seeded without a primary key (see the fixture), so
        // per-feature blobs have no stable identity to key on.
        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.AddAsync("public.roads", new FeatureId("0"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1])));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);

        failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.ListAsync("public.roads", new FeatureId("0")));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task Content_over_quota_is_rejected()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var capped = new PostgisAttachmentStore(context.Store, maxBytesPerAttachment: 4);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            capped.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("big.bin", "application/octet-stream", [1, 2, 3, 4, 5])));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);

        // At the boundary the put still lands.
        var added = await capped.AddAsync(
            "public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("ok.bin", "application/octet-stream", [1, 2, 3, 4]));
        Assert.Equal(4, added.Size);
    }

    [SkippableFact]
    public async Task Operations_observe_cancellation()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.ListAsync("public.attach_target", new FeatureId("1"), canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.AddAsync("public.attach_target", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]), canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.GetAsync("public.attach_target", new FeatureId("1"), 1, canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.UpdateAsync("public.attach_target", new FeatureId("1"), 1, new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]), canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.DeleteAsync("public.attach_target", new FeatureId("1"), [1], canceled));
    }

    [SkippableFact]
    public async Task A_sidecar_this_version_creates_declares_the_byte_order()
    {
        // The sidecar is the store's own table, so it declares the order its
        // identity columns are compared in rather than leaving it to whatever
        // collation the database carries (ADR-0130). This database's default is
        // a deterministic one that happens not to fold, so the case assertions
        // below are the red: they read the column's own collation.
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await FoldSeededAsync();
        await context.Attachments.AddAsync(
            FoldDataset, new FeatureId("delta"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));

        Assert.Equal(0, await context.CountAsync(
            "SELECT count(*) FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation "
            + "WHERE a.attrelid = 'public.spatial_attachments'::regclass AND a.attname IN ('dataset', 'feature_id') "
            + "AND c.collname <> 'C'"));
        Assert.Equal(2, await context.CountAsync(
            "SELECT count(*) FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation "
            + "WHERE a.attrelid = 'public.spatial_attachments'::regclass AND a.attname IN ('dataset', 'feature_id') "
            + "AND c.collname = 'C'"));
    }

    [SkippableFact]
    public async Task A_sidecar_created_by_an_earlier_version_is_brought_forward()
    {
        // A sidecar that already exists is the half of the problem: CREATE TABLE
        // IF NOT EXISTS will not re-declare it, so a store that only fixed its
        // create statement would leave a case-folding sidecar folding, and the
        // attachment of `delta` would be listed, read and deleted for `Delta`
        // (ADR-0130). The fold here is a real one — a non-deterministic
        // collation — and every statement that names the sidecar sees it until
        // the columns are re-declared.
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await FoldSeededAsync();
        await context.ExecuteAsync(
            "DROP TABLE IF EXISTS public.spatial_attachments; "
            + "CREATE TABLE public.spatial_attachments (\"dataset\" text COLLATE attachment_fold NOT NULL, "
            + "\"feature_id\" text COLLATE attachment_fold NOT NULL, \"attachment_id\" bigint NOT NULL, "
            + "\"name\" text NOT NULL, \"content_type\" text NOT NULL, \"size_bytes\" bigint NOT NULL, "
            + "\"keywords\" text NULL, \"content\" bytea NOT NULL, PRIMARY KEY (\"dataset\", \"feature_id\", \"attachment_id\"))");
        var added = await context.Attachments.AddAsync(
            FoldDataset, new FeatureId("delta"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));

        // The fold is gone from the table itself, not just from this statement.
        Assert.Equal(0, await context.CountAsync(
            "SELECT count(*) FROM pg_attribute a JOIN pg_collation c ON c.oid = a.attcollation "
            + "WHERE a.attrelid = 'public.spatial_attachments'::regclass AND a.attname IN ('dataset', 'feature_id') "
            + "AND c.collname <> 'C'"));

        Assert.Empty(await context.Attachments.ListAsync(FoldDataset, new FeatureId("Delta")));

        var fetched = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.GetAsync(FoldDataset, new FeatureId("Delta"), added.Id));
        Assert.Equal(SpatialException.NotFound, fetched.Code);

        var outcomes = await context.Attachments.DeleteAsync(FoldDataset, new FeatureId("Delta"), [added.Id]);
        Assert.False(Assert.Single(outcomes).Succeeded);

        // The attachment of `delta` is untouched, and `Delta` starts from one:
        // the next-id probe answered for the feature it was asked about.
        Assert.Equal([added], await context.Attachments.ListAsync(FoldDataset, new FeatureId("delta")));
        Assert.Equal(
            1,
            (await context.Attachments.AddAsync(
                FoldDataset,
                new FeatureId("Delta"),
                new FeatureAttachmentWrite("b.bin", "application/octet-stream", [2]))).Id);
    }

    /// <summary>
    /// A dataset whose key column holds both cases — the escape hatch ADR-0126
    /// §3 named — alongside the rows and a folding collation the sidecar is
    /// measured over. It drops the sidecar, so attachment ids begin at one.
    /// </summary>
    private async Task<PostgisTestContext> FoldSeededAsync()
    {
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        try
        {
            await context.ExecuteAsync($"DROP TABLE IF EXISTS {FoldDataset}; DROP COLLATION IF EXISTS attachment_fold; ");
            // A non-deterministic collation is what makes a text key fold.
            await context.ExecuteAsync(
                "CREATE COLLATION attachment_fold (provider = icu, locale = 'und-u-ks-level2', deterministic = false)");
            await context.ExecuteAsync(
                $"CREATE TABLE {FoldDataset} (\"code\" text COLLATE \"C\" PRIMARY KEY, \"geometry\" geometry(Point, 4326))");
            await context.Store.WriteAsync(FoldDataset, new FeatureBatch(FoldSchema, [FoldFeature("delta"), FoldFeature("Delta")]));
            await context.ExecuteAsync("DROP TABLE IF EXISTS public.spatial_attachments");
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }

    private static readonly FeatureSchema FoldSchema = new(
    [
        new FieldDefinition("code", AttributeKind.String, nullable: false),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private static Feature FoldFeature(string code) =>
        new(
            new FeatureId(code),
            FoldSchema,
            [
                AttributeValue.FromString(code),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))),
            ]);

    private async Task<PostgisTestContext> SeededAsync()
    {
        var context = PostgisTestContext.Create(_fixture.ConnectionString);
        try
        {
            await context.ExecuteAsync("DROP TABLE IF EXISTS public.attach_target");
            await context.ExecuteAsync(
                "CREATE TABLE public.attach_target (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
            await context.ExecuteAsync(
                "INSERT INTO public.attach_target (id, name, geom) VALUES "
                + "(1, 'Berlin', ST_GeomFromText('POINT(13.405 52.52)', 4326)), "
                + "(2, 'Paris', ST_GeomFromText('POINT(2.35 48.85)', 4326))");
            // Each test starts from an empty sidecar so attachment ids begin at one.
            await context.ExecuteAsync("DROP TABLE IF EXISTS public.spatial_attachments");
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }
}
