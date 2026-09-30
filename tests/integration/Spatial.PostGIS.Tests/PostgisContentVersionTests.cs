using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The durable content version of a PostGIS dataset (ADR-0129): the version a
/// derived cache such as the tile cache keys on has to be a fact the database
/// holds, because the read path and the write path are separate singletons and
/// because a second host reading the same database has to see the same token.
/// These tests drive the real container: the token before a write, after each
/// kind of write, after a rollback, and as a second connection — or a second
/// store — sees it.
/// </summary>
public sealed class PostgisContentVersionTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisContentVersionTests(PostgisContainerFixture fixture) => _fixture = fixture;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geom", AttributeKind.Geometry),
    ]);

    private static Feature Point(long id, string name, double x, double y) =>
        new(
            new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            Schema,
            [
                AttributeValue.FromInt64(id),
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    /// <summary>A fresh table of the shape the tests write to.</summary>
    private static async Task<string> FreshAsync(PostgisTestContext context, string table)
    {
        var dataset = $"public.{table}";
        await context.ExecuteAsync($"DROP TABLE IF EXISTS {dataset} CASCADE");
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");
        return dataset;
    }

    /// <summary>A second store over the same database: a second host, reading the same durable fact.</summary>
    private static PostgisStore SecondHost(PostgisTestContext context) =>
        new(new PostgisOptions { ConnectionString = context.ConnectionString });

    [SkippableFact]
    public async Task A_dataset_nothing_wrote_reports_the_unversioned_token()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_untouched");

        Assert.Equal(ContentVersions.Unversioned, await context.Store.GetContentVersionAsync(dataset));
    }

    [SkippableFact]
    public async Task A_dataset_the_database_does_not_hold_reports_the_unversioned_token()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);

        Assert.Equal(ContentVersions.Unversioned, await context.Store.GetContentVersionAsync("public.version_absent"));
    }

    [SkippableFact]
    public async Task A_write_moves_the_version_a_second_store_and_a_second_connection_can_see()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await using var second = SecondHost(context);
        var dataset = await FreshAsync(context, "version_write");
        var before = await second.GetContentVersionAsync(dataset);

        var written = await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));

        Assert.Equal(1, written);
        var after = await second.GetContentVersionAsync(dataset);
        Assert.NotEqual(before, after);
        Assert.Equal(1, long.Parse(after, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(
            1,
            await context.CountAsync("SELECT version FROM public.spatial_dataset_version WHERE dataset = 'version_write'"));
    }

    [SkippableFact]
    public async Task Every_write_moves_the_version_further()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_growing");

        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));
        var second = await context.Store.GetContentVersionAsync(dataset);
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(2, "two", 2, 2)]));

        Assert.Equal(
            long.Parse(second, System.Globalization.CultureInfo.InvariantCulture) + 1,
            long.Parse(await context.Store.GetContentVersionAsync(dataset), System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task A_write_to_another_dataset_leaves_this_ones_version_alone()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var watched = await FreshAsync(context, "version_watched");
        var other = await FreshAsync(context, "version_other");
        await context.Store.WriteAsync(watched, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));
        var before = await context.Store.GetContentVersionAsync(watched);

        await context.Store.WriteAsync(other, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));

        Assert.Equal(before, await context.Store.GetContentVersionAsync(watched));
    }

    [SkippableFact]
    public async Task A_rolled_back_transaction_leaves_the_version_where_it_was()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_rollback");
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));
        var before = await context.Store.GetContentVersionAsync(dataset);

        var transaction = await context.Store.BeginAsync();
        await context.Store.WriteAsync(
            dataset, new FeatureBatch(Schema, [Point(2, "two", 2, 2)]), transaction);
        Assert.True(await context.Store.RollbackAsync(transaction));

        Assert.Equal(before, await context.Store.GetContentVersionAsync(dataset));
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM public.version_rollback"));
    }

    [SkippableFact]
    public async Task A_committed_transaction_moves_the_version()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_commit");
        var before = await context.Store.GetContentVersionAsync(dataset);

        var transaction = await context.Store.BeginAsync();
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]), transaction);

        Assert.True(await context.Store.CommitAsync(transaction));
        Assert.NotEqual(before, await context.Store.GetContentVersionAsync(dataset));
    }

    [SkippableFact]
    public async Task An_edit_that_changed_a_feature_moves_the_version()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_edit");
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]))).Succeeded);
        var afterAdd = await context.Store.GetContentVersionAsync(dataset);

        Assert.True(Assert.Single(await context.Editor.UpdateAsync(
            dataset, new FeatureBatch(Schema, [Point(1, "renamed", 1, 1)]))).Succeeded);
        var afterUpdate = await context.Store.GetContentVersionAsync(dataset);
        Assert.NotEqual(afterAdd, afterUpdate);

        Assert.True(Assert.Single(await context.Editor.DeleteAsync(
            dataset, [new FeatureId("1")])).Succeeded);

        Assert.NotEqual(afterUpdate, await context.Store.GetContentVersionAsync(dataset));
    }

    [SkippableFact]
    public async Task An_edit_where_every_feature_failed_does_not_move_the_version()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_edit_failed");
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));
        var before = await context.Store.GetContentVersionAsync(dataset);

        var outcomes = await context.Editor.DeleteAsync(dataset, [new FeatureId("404"), new FeatureId("405")]);

        Assert.All(outcomes, outcome => Assert.False(outcome.Succeeded));
        Assert.Equal(before, await context.Store.GetContentVersionAsync(dataset));
    }

    [SkippableFact]
    public async Task A_partially_applied_edit_moves_the_version()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_edit_partial");
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));
        var before = await context.Store.GetContentVersionAsync(dataset);

        var outcomes = await context.Editor.DeleteAsync(dataset, [new FeatureId("1"), new FeatureId("404")]);

        Assert.True(outcomes[0].Succeeded);
        Assert.False(outcomes[1].Succeeded);
        Assert.NotEqual(before, await context.Store.GetContentVersionAsync(dataset));
    }

    [SkippableFact]
    public async Task An_rolled_back_edit_leaves_the_version_where_it_was()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_edit_rollback");
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));
        var before = await context.Store.GetContentVersionAsync(dataset);

        var transaction = await context.Store.BeginAsync();
        Assert.True(Assert.Single(await context.Editor.AddAsync(
            dataset, new FeatureBatch(Schema, [Point(2, "two", 2, 2)]), transaction)).Succeeded);
        Assert.True(await context.Store.RollbackAsync(transaction));

        Assert.Equal(before, await context.Store.GetContentVersionAsync(dataset));
        Assert.Equal(1, await context.CountAsync("SELECT count(*) FROM public.version_edit_rollback"));
    }

    [SkippableFact]
    public async Task An_ingest_moves_the_version()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.version_ingest CASCADE");

        var outcome = await context.Ingest.IngestAsync(
            new IngestRequest("public.version_ingest", 4326, IngestIdentity.Source, "id"),
            [new FeatureBatch(Schema, [Point(1, "one", 1, 1), Point(2, "two", 2, 2)])]);

        Assert.Equal(2, outcome.Features);
        Assert.Equal(1, long.Parse(
            await context.Store.GetContentVersionAsync("public.version_ingest"),
            System.Globalization.CultureInfo.InvariantCulture));
    }

    [SkippableFact]
    public async Task Creating_a_dataset_moves_the_version()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP TABLE IF EXISTS public.version_create CASCADE");
        Assert.Equal(ContentVersions.Unversioned, await context.Store.GetContentVersionAsync("public.version_create"));

        await context.Store.CreateAsync("public.version_create", new FeatureBatch(Schema, [Point(1, "one", 1, 1)]), 4326);

        Assert.NotEqual(ContentVersions.Unversioned, await context.Store.GetContentVersionAsync("public.version_create"));
    }

    [SkippableFact]
    public async Task A_cancelled_version_read_is_cancelled()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await FreshAsync(context, "version_cancelled");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Store.GetContentVersionAsync(dataset, cancellation.Token).AsTask());
    }

    [SkippableFact]
    public async Task An_unconfigured_store_reports_store_unavailable()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var unconfigured = new PostgisStore(new PostgisOptions { ConnectionString = "   " });

        var failure = await Assert.ThrowsAsync<SpatialException>(
            () => unconfigured.GetContentVersionAsync("public.anything").AsTask());

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
    }

    [SkippableFact]
    public async Task The_version_table_lives_in_the_datasets_own_schema()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync("DROP SCHEMA IF EXISTS version_side CASCADE");
        await context.ExecuteAsync("CREATE SCHEMA version_side");
        await context.ExecuteAsync(
            "CREATE TABLE version_side.places (id bigint PRIMARY KEY, name text, geom geometry(Point, 4326))");

        await context.Store.WriteAsync(
            "version_side.places", new FeatureBatch(Schema, [Point(1, "one", 1, 1)]));

        Assert.Equal(1, await context.CountAsync(
            "SELECT version FROM version_side.spatial_dataset_version WHERE dataset = 'places'"));
        await context.ExecuteAsync("DROP SCHEMA version_side CASCADE");
    }
}
