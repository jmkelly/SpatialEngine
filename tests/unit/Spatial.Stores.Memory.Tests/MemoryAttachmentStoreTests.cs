using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The in-memory feature-attachment face (T-060): per-feature blob
/// put/get/delete keyed by dataset and object id, with core-typed
/// descriptors, provider-owned bytes, a provider-side quota and structured
/// failures. Red-first: the <c>IFeatureAttachmentStore</c> face does
/// not exist yet, so nothing here compiles until it lands.
/// </summary>
public sealed class MemoryAttachmentStoreTests
{
    private static readonly FeatureSchema Source = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static FeatureBatch Batch(params Feature[] features) => new(Source, features);

    private static Feature Point(string id, string name, double x, double y) =>
        new(
            new FeatureId(id),
            Source,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    private static async Task<MemoryStore> IngestedAsync()
    {
        var store = new MemoryStore();
        var ingest = new MemoryIngest(store);
        await ingest.IngestAsync(
            new IngestRequest("memory.cities", 4326),
            [Batch(Point("1", "Berlin", 13.4, 52.5), Point("2", "Paris", 2.35, 48.85))]);
        return store;
    }

    private static MemoryAttachments Attachments(MemoryStore store) => new(store);

    private static byte[] PngBytes() => [0x89, 0x50, 0x4E, 0x47];

    [Fact]
    public async Task Add_then_list_and_get_round_trip()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var added = await attachments.AddAsync(
            "memory.cities", new FeatureId("1"), "photo.png", "image/png", PngBytes());

        Assert.Equal(1, added.Id);
        Assert.Equal("photo.png", added.Name);
        Assert.Equal("image/png", added.ContentType);
        Assert.Equal(PngBytes().Length, added.Size);

        var listed = await attachments.ListAsync("memory.cities", new FeatureId("1"));
        Assert.Equal([added], listed);

        // The sibling feature stores nothing.
        Assert.Empty(await attachments.ListAsync("memory.cities", new FeatureId("2")));

        var fetched = await attachments.GetAsync("memory.cities", new FeatureId("1"), added.Id);
        Assert.Equal(added, fetched.Descriptor);
        Assert.Equal(PngBytes(), fetched.Content);
    }

    [Fact]
    public async Task Attachment_ids_assign_per_feature_from_one()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var first = await attachments.AddAsync("memory.cities", new FeatureId("1"), "a.bin", "application/octet-stream", [1]);
        var second = await attachments.AddAsync("memory.cities", new FeatureId("1"), "b.bin", "application/octet-stream", [2]);
        var other = await attachments.AddAsync("memory.cities", new FeatureId("2"), "c.bin", "application/octet-stream", [3]);

        Assert.Equal(1, first.Id);
        Assert.Equal(2, second.Id);
        Assert.Equal(1, other.Id);
    }

    [Fact]
    public async Task Update_replaces_content_and_metadata()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);
        var added = await attachments.AddAsync("memory.cities", new FeatureId("1"), "a.bin", "application/octet-stream", [1]);

        var updated = await attachments.UpdateAsync(
            "memory.cities", new FeatureId("1"), added.Id, "b.png", "image/png", PngBytes(), keywords: "front");

        Assert.Equal(added.Id, updated.Id);
        Assert.Equal("b.png", updated.Name);
        Assert.Equal("image/png", updated.ContentType);
        Assert.Equal("front", updated.Keywords);

        var fetched = await attachments.GetAsync("memory.cities", new FeatureId("1"), added.Id);
        Assert.Equal(updated, fetched.Descriptor);
        Assert.Equal(PngBytes(), fetched.Content);
    }

    [Fact]
    public async Task Delete_removes_and_reports_per_id_outcomes()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);
        var first = await attachments.AddAsync("memory.cities", new FeatureId("1"), "a.bin", "application/octet-stream", [1]);
        var second = await attachments.AddAsync("memory.cities", new FeatureId("1"), "b.bin", "application/octet-stream", [2]);

        var outcomes = await attachments.DeleteAsync("memory.cities", new FeatureId("1"), [first.Id, 999]);

        Assert.Equal(2, outcomes.Count);
        Assert.True(outcomes[0].Succeeded);
        Assert.Equal(first.Id, outcomes[0].Id);
        Assert.False(outcomes[1].Succeeded);
        Assert.Equal(SpatialException.NotFound, outcomes[1].ErrorCode);

        var remaining = await attachments.ListAsync("memory.cities", new FeatureId("1"));
        Assert.Equal([second], remaining);
    }

    [Fact]
    public async Task Stored_bytes_are_provider_owned()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);
        var content = PngBytes();

        var added = await attachments.AddAsync("memory.cities", new FeatureId("1"), "a.bin", "application/octet-stream", content);
        content[0] = 0x00;

        var fetched = await attachments.GetAsync("memory.cities", new FeatureId("1"), added.Id);
        fetched.Content[0] = 0x00;

        var again = await attachments.GetAsync("memory.cities", new FeatureId("1"), added.Id);
        Assert.Equal(PngBytes(), again.Content);
    }

    [Fact]
    public async Task An_unknown_dataset_is_a_not_found()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("memory.nope", new FeatureId("1"), "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.NotFound, failure.Code);

        failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.ListAsync("memory.nope", new FeatureId("1")));
        Assert.Equal(SpatialException.NotFound, failure.Code);

        failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.GetAsync("memory.nope", new FeatureId("1"), 1));
        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task An_attachment_on_an_unknown_feature_is_a_not_found()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("memory.cities", new FeatureId("9"), "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.NotFound, failure.Code);

        failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.GetAsync("memory.cities", new FeatureId("9"), 1));
        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task A_missing_attachment_is_a_not_found()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.GetAsync("memory.cities", new FeatureId("1"), 999));
        Assert.Equal(SpatialException.NotFound, failure.Code);

        failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.UpdateAsync("memory.cities", new FeatureId("1"), 999, "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_name_is_rejected(string name)
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("memory.cities", new FeatureId("1"), name, "application/octet-stream", [1]));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task Null_content_is_rejected()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("memory.cities", new FeatureId("1"), "a.bin", "application/octet-stream", null!));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task An_empty_content_type_defaults_to_octet_stream()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);

        var added = await attachments.AddAsync("memory.cities", new FeatureId("1"), "a.bin", "", [1]);

        Assert.Equal("application/octet-stream", added.ContentType);
    }

    [Fact]
    public void The_default_quota_is_ten_mebibytes()
    {
        Assert.Equal(10_485_760, MemoryAttachments.DefaultMaxBytesPerAttachment);
    }

    [Fact]
    public async Task Content_over_quota_is_rejected()
    {
        var store = await IngestedAsync();
        var attachments = new MemoryAttachments(store, maxBytesPerAttachment: 4);

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("memory.cities", new FeatureId("1"), "big.bin", "application/octet-stream", [1, 2, 3, 4, 5]));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);

        // At the boundary the put still lands.
        var added = await attachments.AddAsync("memory.cities", new FeatureId("1"), "ok.bin", "application/octet-stream", [1, 2, 3, 4]);
        Assert.Equal(4, added.Size);
    }

    [Fact]
    public async Task Operations_observe_cancellation()
    {
        var store = await IngestedAsync();
        var attachments = Attachments(store);
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.AddAsync("memory.cities", new FeatureId("1"), "a.bin", "application/octet-stream", [1], cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.ListAsync("memory.cities", new FeatureId("1"), canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.GetAsync("memory.cities", new FeatureId("1"), 1, canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.UpdateAsync("memory.cities", new FeatureId("1"), 1, "a.bin", "application/octet-stream", [1], cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.DeleteAsync("memory.cities", new FeatureId("1"), [1], canceled));
    }
}
