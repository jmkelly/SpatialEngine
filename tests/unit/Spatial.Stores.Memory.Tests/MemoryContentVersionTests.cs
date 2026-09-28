using System.Globalization;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The in-memory content version (ADR-0083): a per-dataset counter the store
/// bumps on every mutation, so a derived cache keyed by it (the tile cache)
/// invalidates exactly the datasets that changed. The version is an opaque
/// token, not an existence check, and a rolled-back transaction restores the
/// version it snapshotted.
/// </summary>
public sealed class MemoryContentVersionTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, nullable: true),
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static Feature Point(string id, string name, double x, double y) =>
        new(
            new FeatureId(id),
            Schema,
            [
                AttributeValue.FromInt64(long.Parse(id, CultureInfo.InvariantCulture)),
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    /// <summary>A feature with no identity, so the store assigns one (ADR-0043).</summary>
    private static Feature Unassigned(string name, double x, double y) =>
        new(
            FeatureId.Unassigned,
            Schema,
            [
                AttributeValue.FromInt64(0),
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    private static async Task<MemoryStore> IngestedAsync(string dataset = "memory.places")
    {
        var store = new MemoryStore();
        await new MemoryIngest(store).IngestAsync(
            new IngestRequest(dataset, 4326, IngestIdentity.Source, "id"),
            [new FeatureBatch(Schema, [Point("1", "Berlin", 13.4, 52.5)])]);
        return store;
    }

    private static async Task<string> VersionAsync(MemoryStore store, string dataset = "memory.places") =>
        await store.GetContentVersionAsync(dataset);

    [Fact]
    public async Task An_untouched_dataset_reports_a_stable_version()
    {
        var store = await IngestedAsync();

        var first = await VersionAsync(store);
        var second = await VersionAsync(store);

        Assert.Equal(first, second);
        Assert.NotEqual(string.Empty, first);
    }

    [Fact]
    public async Task A_dataset_outside_the_store_reports_the_unversioned_token()
    {
        var store = await IngestedAsync();

        Assert.Equal(ContentVersions.Unversioned, await store.GetContentVersionAsync("memory.absent"));
    }

    [Fact]
    public async Task A_write_moves_the_version()
    {
        var store = await IngestedAsync();
        var before = await VersionAsync(store);

        await store.WriteAsync("memory.places", new FeatureBatch(Schema, [Unassigned("Perth", 115.86, -31.95)]));

        Assert.NotEqual(before, await VersionAsync(store));
    }

    [Fact]
    public async Task An_empty_write_leaves_the_version_alone()
    {
        var store = await IngestedAsync();
        var before = await VersionAsync(store);

        await store.WriteAsync("memory.places", new FeatureBatch(Schema, []));

        Assert.Equal(before, await VersionAsync(store));
    }

    [Fact]
    public async Task An_add_an_update_and_a_delete_each_move_the_version()
    {
        var store = await IngestedAsync();
        var editor = new MemoryEditor(store);

        await editor.AddAsync("memory.places", new FeatureBatch(Schema, [Unassigned("Perth", 115.86, -31.95)]));
        var afterAdd = await VersionAsync(store);
        Assert.NotEqual(ContentVersions.Unversioned, afterAdd);

        await editor.UpdateAsync("memory.places", new FeatureBatch(Schema, [Point("1", "Berlin", 13.4, 52.5)]));
        Assert.NotEqual(afterAdd, await VersionAsync(store));

        var afterUpdate = await VersionAsync(store);
        await editor.DeleteAsync("memory.places", [new FeatureId("1")]);

        Assert.NotEqual(afterUpdate, await VersionAsync(store));
    }

    [Fact]
    public async Task A_partially_failed_edit_still_moves_the_version()
    {
        var store = await IngestedAsync();
        var before = await VersionAsync(store);

        var outcomes = await new MemoryEditor(store).DeleteAsync("memory.places", [new FeatureId("1"), new FeatureId("absent")]);

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Succeeded);
        Assert.False(outcomes[1].Succeeded);
        Assert.NotEqual(before, await VersionAsync(store));
    }

    [Fact]
    public async Task A_write_to_one_dataset_leaves_another_alone()
    {
        var store = await IngestedAsync("memory.first");
        await new MemoryIngest(store).IngestAsync(
            new IngestRequest("memory.second", 4326, IngestIdentity.Source, "id"),
            [new FeatureBatch(Schema, [Point("2", "Paris", 2.35, 48.85)])]);
        var other = await VersionAsync(store, "memory.second");

        await store.WriteAsync("memory.first", new FeatureBatch(Schema, [Unassigned("Perth", 115.86, -31.95)]));

        Assert.Equal(other, await VersionAsync(store, "memory.second"));
    }

    [Fact]
    public async Task A_rollback_restores_the_version_it_snapshot()
    {
        var store = await IngestedAsync();
        var before = await VersionAsync(store);
        var transaction = await store.BeginAsync();
        await store.WriteAsync("memory.places", new FeatureBatch(Schema, [Unassigned("Perth", 115.86, -31.95)]), transaction);
        var uncommitted = await VersionAsync(store);
        Assert.NotEqual(before, uncommitted);

        Assert.True(await store.RollbackAsync(transaction));

        Assert.Equal(before, await VersionAsync(store));
    }

    [Fact]
    public async Task A_commit_keeps_the_version_the_write_moved_it_to()
    {
        var store = await IngestedAsync();
        var before = await VersionAsync(store);
        var transaction = await store.BeginAsync();
        await store.WriteAsync("memory.places", new FeatureBatch(Schema, [Unassigned("Perth", 115.86, -31.95)]), transaction);

        Assert.True(await store.CommitAsync(transaction));

        Assert.NotEqual(before, await VersionAsync(store));
    }

    [Fact]
    public async Task A_cancelled_read_reports_nothing_and_throws()
    {
        var store = await IngestedAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.GetContentVersionAsync("memory.places", cancellation.Token).AsTask());
    }
}
