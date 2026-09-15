using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The containerised attachment-path tests (T-088, ADR-0065 §2): add/list/
/// get/update/delete through <c>PostgisAttachmentStore</c> against the
/// <c>bytea</c> sidecar table — success, per-id failure and cancellation.
/// Every test skips with an explicit reason when no Docker daemon is
/// available.
/// </summary>
public sealed class PostgisAttachmentIntegrationTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisAttachmentIntegrationTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Add_then_list_and_get_round_trip()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();

        var added = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "photo.png", "image/png", [0x89, 0x50, 0x4E, 0x47],
            keywords: "streetscape");

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

        var first = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "a.bin", "application/octet-stream", [1]);
        var second = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "b.bin", "application/octet-stream", [2]);
        var other = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("2"), "c.bin", "application/octet-stream", [3]);

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(1, other.Id);
    }

    [SkippableFact]
    public async Task Attachments_survive_a_fresh_store_over_the_same_database()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var added = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "a.bin", "application/octet-stream", [1, 2, 3]);

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

        var added = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "a.bin", "", [1]);

        Assert.Equal("application/octet-stream", added.ContentType);
    }

    [SkippableFact]
    public async Task Update_replaces_content_and_metadata()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var added = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "a.bin", "application/octet-stream", [1]);

        var updated = await context.Attachments.UpdateAsync(
            "public.attach_target", new FeatureId("1"), added.Id, "b.png", "image/png", [9, 10], keywords: "front");

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
            context.Attachments.UpdateAsync(
                "public.attach_target", new FeatureId("1"), 999, "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [SkippableFact]
    public async Task Delete_removes_and_reports_per_id_outcomes()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = await SeededAsync();
        var first = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "a.bin", "application/octet-stream", [1]);
        var second = await context.Attachments.AddAsync(
            "public.attach_target", new FeatureId("1"), "b.bin", "application/octet-stream", [2]);

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
            context.Attachments.AddAsync("public.attach_missing", new FeatureId("1"), "a.bin", "application/octet-stream", [1]));
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
            context.Attachments.AddAsync("public.attach_target", new FeatureId("404"), "a.bin", "application/octet-stream", [1]));
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
            context.Attachments.AddAsync("public.roads", new FeatureId("0"), "a.bin", "application/octet-stream", [1]));
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
            capped.AddAsync("public.attach_target", new FeatureId("1"), "big.bin", "application/octet-stream", [1, 2, 3, 4, 5]));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);

        // At the boundary the put still lands.
        var added = await capped.AddAsync(
            "public.attach_target", new FeatureId("1"), "ok.bin", "application/octet-stream", [1, 2, 3, 4]);
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
            context.Attachments.AddAsync(
                "public.attach_target", new FeatureId("1"), "a.bin", "application/octet-stream", [1], cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.GetAsync("public.attach_target", new FeatureId("1"), 1, canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.UpdateAsync(
                "public.attach_target", new FeatureId("1"), 1, "a.bin", "application/octet-stream", [1], cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            context.Attachments.DeleteAsync("public.attach_target", new FeatureId("1"), [1], canceled));
    }

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
