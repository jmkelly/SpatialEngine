using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer;
namespace Spatial.SqlServer.Tests;

/// <summary>
/// The containerised attachment-path tests (T-088, ADR-0065 §2 as the SQL
/// Server provider follows it): add/list/get/update/delete through
/// <c>SqlServerAttachmentStore</c> against the <c>varbinary(max)</c> sidecar
/// table — success, per-id failure and cancellation. Every test skips with an
/// explicit reason when no Docker daemon is available.
/// </summary>
public sealed class SqlServerAttachmentIntegrationTests : IClassFixture<SqlServerContainerFixture>
{
    private const string Target = "dbo.attach_target";

    private readonly SqlServerContainerFixture _fixture;

    public SqlServerAttachmentIntegrationTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task Add_then_list_and_get_round_trip()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        var added = await context.Attachments.AddAsync(
            Target, new FeatureId("1"), new FeatureAttachmentWrite("photo.png", "image/png", [0x89, 0x50, 0x4E, 0x47], "streetscape"));

        Assert.Equal(1, added.Id);
        Assert.Equal("photo.png", added.Name);
        Assert.Equal("image/png", added.ContentType);
        Assert.Equal(4, added.Size);
        Assert.Equal("streetscape", added.Keywords);

        var listed = await context.Attachments.ListAsync(Target, new FeatureId("1"));
        Assert.Equal([added], listed);

        // The sibling feature stores nothing.
        Assert.Empty(await context.Attachments.ListAsync(Target, new FeatureId("2")));

        var fetched = await context.Attachments.GetAsync(Target, new FeatureId("1"), added.Id);
        Assert.Equal(added, fetched.Descriptor);
        Assert.Equal<byte[]>([0x89, 0x50, 0x4E, 0x47], fetched.Content);
    }

    [SkippableFact]
    public async Task Attachment_ids_assign_per_feature_from_one()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        var first = await context.Attachments.AddAsync(Target, new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));
        var second = await context.Attachments.AddAsync(Target, new FeatureId("1"), new FeatureAttachmentWrite("b.bin", "application/octet-stream", [2]));
        var other = await context.Attachments.AddAsync(Target, new FeatureId("2"), new FeatureAttachmentWrite("c.bin", "application/octet-stream", [3]));

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(1, other.Id);
    }

    [SkippableFact]
    public async Task Attachments_survive_a_fresh_store_over_the_same_database()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        var context = await SeededAsync();
        var added = await context.Attachments.AddAsync(
            Target, new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [7, 7, 7]));
        await context.DisposeAsync();

        await using var reopened = SqlServerTestContext.Create(_fixture.ConnectionString);
        var fetched = await reopened.Attachments.GetAsync(Target, new FeatureId("1"), added.Id);

        Assert.Equal<byte[]>([7, 7, 7], fetched.Content);
    }

    [SkippableFact]
    public async Task An_empty_content_type_defaults_to_octet_stream()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        var added = await context.Attachments.AddAsync(
            Target, new FeatureId("1"), new FeatureAttachmentWrite("a.bin", string.Empty, [1]));

        Assert.Equal("application/octet-stream", added.ContentType);
    }

    [SkippableFact]
    public async Task Update_replaces_content_and_metadata()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();
        var added = await context.Attachments.AddAsync(
            Target, new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));

        var updated = await context.Attachments.UpdateAsync(
            Target, new FeatureId("1"), added.Id, new FeatureAttachmentWrite("b.bin", "text/plain", [2, 3], "note"));

        Assert.Equal(added.Id, updated.Id);
        Assert.Equal("b.bin", updated.Name);
        Assert.Equal(2, updated.Size);
        var fetched = await context.Attachments.GetAsync(Target, new FeatureId("1"), added.Id);
        Assert.Equal<byte[]>([2, 3], fetched.Content);
    }

    [SkippableFact]
    public async Task Update_of_a_missing_attachment_is_a_not_found()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.UpdateAsync(
                Target, new FeatureId("1"), 99, new FeatureAttachmentWrite("b.bin", "text/plain", [2])));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [SkippableFact]
    public async Task Delete_removes_and_reports_per_id_outcomes()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();
        var first = await context.Attachments.AddAsync(Target, new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]));
        var second = await context.Attachments.AddAsync(Target, new FeatureId("1"), new FeatureAttachmentWrite("b.bin", "application/octet-stream", [2]));

        var outcomes = await context.Attachments.DeleteAsync(Target, new FeatureId("1"), [first.Id, second.Id, 99]);

        Assert.Equal(3, outcomes.Count);
        Assert.True(outcomes[0].Succeeded);
        Assert.True(outcomes[1].Succeeded);
        Assert.False(outcomes[2].Succeeded);
        Assert.Equal(SpatialException.NotFound, outcomes[2].ErrorCode);
        Assert.Empty(await context.Attachments.ListAsync(Target, new FeatureId("1")));
    }

    [SkippableFact]
    public async Task An_unknown_dataset_is_a_not_found()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.AddAsync("dbo.attach_missing", new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1])));
        Assert.Equal(SpatialException.NotFound, add.Code);

        var list = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.ListAsync("dbo.attach_missing", new FeatureId("1")));
        Assert.Equal(SpatialException.NotFound, list.Code);

        var get = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.GetAsync("dbo.attach_missing", new FeatureId("1"), 1));
        Assert.Equal(SpatialException.NotFound, get.Code);
    }

    [SkippableFact]
    public async Task An_attachment_on_an_unknown_feature_is_a_not_found()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.AddAsync(Target, new FeatureId("404"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1])));
        Assert.Equal(SpatialException.NotFound, add.Code);

        var get = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.GetAsync(Target, new FeatureId("404"), 1));
        Assert.Equal(SpatialException.NotFound, get.Code);
    }

    [SkippableFact]
    public async Task A_missing_attachment_is_a_not_found()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.GetAsync(Target, new FeatureId("1"), 999));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [SkippableFact]
    public async Task A_malformed_feature_identity_is_invalid_arguments()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();

        // The target has one identity column; a two-part identity cannot be parsed.
        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            context.Attachments.ListAsync(Target, new FeatureId("1|2")));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task An_attachment_over_the_quota_is_invalid_arguments()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();
        var store = new SqlServerAttachmentStore(context.Store, maxBytesPerAttachment: 4);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.AddAsync(Target, new FeatureId("1"), new FeatureAttachmentWrite("big.bin", "application/octet-stream", [1, 2, 3, 4, 5])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [SkippableFact]
    public async Task Attachments_honour_cancellation()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        await using var context = await SeededAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Attachments.AddAsync(
                Target, new FeatureId("1"), new FeatureAttachmentWrite("a.bin", "application/octet-stream", [1]), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Attachments.ListAsync(Target, new FeatureId("1"), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            context.Attachments.DeleteAsync(Target, new FeatureId("1"), [1], cancellation.Token));
    }

    /// <summary>
    /// A seeded target dataset and an empty attachment sidecar, so each test
    /// starts from attachment id one.
    /// </summary>
    private async Task<SqlServerTestContext> SeededAsync()
    {
        var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        try
        {
            await context.ExecuteAsync($"IF OBJECT_ID('{Target}', 'U') IS NOT NULL DROP TABLE {Target}");
            await context.ExecuteAsync(
                $"CREATE TABLE {Target} (id int NOT NULL PRIMARY KEY, name nvarchar(50) NULL, geom geometry NULL)");
            await context.ExecuteAsync(
                $"INSERT INTO {Target} (id, name, geom) VALUES "
                + "(1, N'Berlin', geometry::STGeomFromText('POINT(13.405 52.52)', 4326)), "
                + "(2, N'Paris', geometry::STGeomFromText('POINT(2.35 48.85)', 4326))");
            await context.ExecuteAsync("IF OBJECT_ID('spatial_attachments', 'U') IS NOT NULL DROP TABLE spatial_attachments");
            return context;
        }
        catch
        {
            await context.DisposeAsync();
            throw;
        }
    }
}
