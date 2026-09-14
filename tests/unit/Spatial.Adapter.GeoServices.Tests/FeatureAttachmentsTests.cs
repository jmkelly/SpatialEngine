using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The Feature Service attachment engine (T-061, ADR-0066) over
/// <see cref="FeatureAttachments"/> directly: store-backed reads and writes,
/// the honest surface without a capability, typed failures and cancellation.
/// The fakes honour <see cref="CancellationToken"/> like a real store, so a
/// cancelled token must abort the operation even when a store ignores it —
/// long-running work is a cancellable <see cref="Task"/> (AGENTS.md hard
/// wall). HTTP auth and routing are proved by
/// <c>GeoServicesAttachmentsTests</c>; this class proves the engine.
/// </summary>
public sealed class FeatureAttachmentsTests
{
    private static DatasetDescription Describe() =>
        new("test.places", "test", "places", "geometry", 4326, "Point", 2, ["id"], FakeTable.Schema);

    [Fact]
    public async Task Query_lists_one_group_per_feature()
    {
        var table = new FakeTable();
        var attachments = new FakeAttachments();
        await attachments.AddAsync("test.places", new FeatureId("2"), "photo.jpg", "image/jpeg", [1, 2, 3], "streetscape");

        var body = await ExecuteAsync(FeatureAttachments.QueryAsync(
            Describe(), table, attachments, null, CancellationToken.None));

        var groups = body.GetProperty("attachmentGroups").EnumerateArray().ToArray();
        Assert.Equal([1, 2], groups.Select(group => group.GetProperty("parentObjectId").GetInt64()).ToArray());
        Assert.Empty(groups[0].GetProperty("attachmentInfos").EnumerateArray());
        var info = Assert.Single(groups[1].GetProperty("attachmentInfos").EnumerateArray());
        Assert.Equal(1, info.GetProperty("id").GetInt64());
        Assert.Equal("photo.jpg", info.GetProperty("name").GetString());
        Assert.Equal("image/jpeg", info.GetProperty("contentType").GetString());
        Assert.Equal(3, info.GetProperty("size").GetInt64());
        Assert.Equal("streetscape", info.GetProperty("keywords").GetString());
    }

    [Fact]
    public async Task Query_without_a_capability_reports_empty_groups()
    {
        // Without a capability nothing is stored for any feature, so every
        // group is truthfully empty — the same shape as a capable but
        // blob-less store.
        var body = await ExecuteAsync(FeatureAttachments.QueryAsync(
            Describe(), new FakeTable(), null, null, CancellationToken.None));

        var groups = body.GetProperty("attachmentGroups").EnumerateArray().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.All(groups, group => Assert.Empty(group.GetProperty("attachmentInfos").EnumerateArray()));
    }

    [Fact]
    public async Task Query_filters_by_object_ids()
    {
        var body = await ExecuteAsync(FeatureAttachments.QueryAsync(
            Describe(), new FakeTable(), new FakeAttachments(), [2], CancellationToken.None));

        var group = Assert.Single(body.GetProperty("attachmentGroups").EnumerateArray());
        Assert.Equal(2, group.GetProperty("parentObjectId").GetInt64());
    }

    [Fact]
    public async Task Query_with_an_unknown_object_id_is_not_found()
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => FeatureAttachments.QueryAsync(
            Describe(), new FakeTable(), new FakeAttachments(), [99], CancellationToken.None));

        Assert.Equal(EsriErrorCodes.NotFound, failure.Code);
    }

    [Fact]
    public async Task Parse_ids_rejects_malformed_values()
    {
        Assert.Null(FeatureAttachments.ParseIds(null, "objectIds"));
        Assert.Null(FeatureAttachments.ParseIds("  ", "objectIds"));
        Assert.Equal([1, 2], FeatureAttachments.ParseIds("1,2", "objectIds"));

        var failure = Assert.Throws<EsriInteropException>(() => FeatureAttachments.ParseIds("abc", "objectIds"));
        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains("objectIds", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Infos_lists_the_stored_descriptors()
    {
        var attachments = new FakeAttachments();
        await attachments.AddAsync("test.places", new FeatureId("1"), "a.jpg", "image/jpeg", [1]);
        await attachments.AddAsync("test.places", new FeatureId("1"), "b.jpg", "image/jpeg", [2]);

        var body = await ExecuteAsync(FeatureAttachments.InfosAsync(
            Describe(), new FakeTable(), attachments, 1, CancellationToken.None));

        var infos = body.GetProperty("attachmentInfos").EnumerateArray().ToArray();
        Assert.Equal([1, 2], infos.Select(info => info.GetProperty("id").GetInt64()).ToArray());
    }

    [Fact]
    public async Task Infos_without_a_capability_reports_the_empty_set()
    {
        var body = await ExecuteAsync(FeatureAttachments.InfosAsync(
            Describe(), new FakeTable(), null, 1, CancellationToken.None));

        Assert.Empty(body.GetProperty("attachmentInfos").EnumerateArray());
    }

    [Fact]
    public async Task Infos_for_an_unknown_feature_is_not_found()
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => FeatureAttachments.InfosAsync(
            Describe(), new FakeTable(), new FakeAttachments(), 99, CancellationToken.None));

        Assert.Equal(EsriErrorCodes.NotFound, failure.Code);
    }

    [Fact]
    public async Task Content_serves_the_stored_bytes()
    {
        var attachments = new FakeAttachments();
        await attachments.AddAsync("test.places", new FeatureId("1"), "a.jpg", "image/jpeg", [7, 8, 9]);

        var result = await FeatureAttachments.ContentAsync(
            Describe(), new FakeTable(), attachments, 1, 1, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);

        Assert.Equal("image/jpeg", context.Response.ContentType);
        Assert.Equal([7, 8, 9], ((MemoryStream)context.Response.Body).ToArray());
    }

    [Fact]
    public async Task Content_for_an_unknown_attachment_is_not_found()
    {
        await Assert.ThrowsAsync<SpatialException>(() => FeatureAttachments.ContentAsync(
            Describe(), new FakeTable(), new FakeAttachments(), 1, 99, CancellationToken.None));
    }

    [Fact]
    public async Task Content_without_a_capability_is_not_found()
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => FeatureAttachments.ContentAsync(
            Describe(), new FakeTable(), null, 1, 1, CancellationToken.None));

        Assert.Equal(EsriErrorCodes.NotFound, failure.Code);
    }

    [Fact]
    public async Task Add_reports_the_assigned_id()
    {
        var body = await ExecuteAsync(FeatureAttachments.AddAsync(
            Describe(), new FakeTable(), new FakeAttachments(), 1,
            new AttachmentUpload("a.jpg", "image/jpeg", [1, 2]), CancellationToken.None));

        var added = body.GetProperty("addAttachmentResult");
        Assert.True(added.GetProperty("success").GetBoolean());
        Assert.Equal(1, added.GetProperty("objectId").GetInt64());
    }

    [Theory]
    [InlineData("addAttachment")]
    [InlineData("updateAttachment")]
    [InlineData("deleteAttachments")]
    public async Task Writes_without_a_capability_name_the_missing_capability(string operation)
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => operation switch
        {
            "addAttachment" => FeatureAttachments.AddAsync(
                Describe(), new FakeTable(), null, 1,
                new AttachmentUpload("a.jpg", "image/jpeg", [1]), CancellationToken.None),
            "updateAttachment" => FeatureAttachments.UpdateAsync(
                Describe(), new FakeTable(), null, 1, 1,
                new AttachmentUpload("a.jpg", "image/jpeg", [1]), CancellationToken.None),
            _ => FeatureAttachments.DeleteAsync(
                Describe(), new FakeTable(), null, 1, [1], CancellationToken.None),
        });

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains(operation, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_replaces_the_blob_and_keeps_the_identity()
    {
        var attachments = new FakeAttachments();
        await attachments.AddAsync("test.places", new FeatureId("1"), "a.jpg", "image/jpeg", [1]);

        var body = await ExecuteAsync(FeatureAttachments.UpdateAsync(
            Describe(), new FakeTable(), attachments, 1, 1,
            new AttachmentUpload("b.png", "image/png", [2, 3]), CancellationToken.None));

        var updated = body.GetProperty("updateAttachmentResult");
        Assert.True(updated.GetProperty("success").GetBoolean());
        Assert.Equal(1, updated.GetProperty("objectId").GetInt64());
        var stored = await attachments.GetAsync("test.places", new FeatureId("1"), 1);
        Assert.Equal("b.png", stored.Descriptor.Name);
        Assert.Equal([2, 3], stored.Content);
    }

    [Fact]
    public async Task Delete_reports_each_id_with_typed_per_id_failures()
    {
        var attachments = new FakeAttachments();
        await attachments.AddAsync("test.places", new FeatureId("1"), "a.jpg", "image/jpeg", [1]);

        var body = await ExecuteAsync(FeatureAttachments.DeleteAsync(
            Describe(), new FakeTable(), attachments, 1, [1, 99], CancellationToken.None));

        var results = body.GetProperty("deleteAttachmentResults").EnumerateArray().ToArray();
        Assert.True(results[0].GetProperty("success").GetBoolean());
        Assert.Equal(1, results[0].GetProperty("objectId").GetInt64());
        Assert.False(results[1].GetProperty("success").GetBoolean());
        Assert.Equal(99, results[1].GetProperty("objectId").GetInt64());
        Assert.Equal(EsriErrorCodes.NotFound, results[1].GetProperty("error").GetProperty("code").GetInt32());
    }

    [Theory]
    [InlineData("query")]
    [InlineData("infos")]
    [InlineData("content")]
    [InlineData("add")]
    [InlineData("update")]
    [InlineData("delete")]
    public async Task Every_operation_observes_cancellation(string operation)
    {
        var table = new FakeTable();
        var attachments = new FakeAttachments();
        var token = new CancellationToken(canceled: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation switch
        {
            "query" => FeatureAttachments.QueryAsync(Describe(), table, attachments, null, token),
            "infos" => FeatureAttachments.InfosAsync(Describe(), table, attachments, 1, token),
            "content" => FeatureAttachments.ContentAsync(Describe(), table, attachments, 1, 1, token),
            "add" => FeatureAttachments.AddAsync(
                Describe(), table, attachments, 1, new AttachmentUpload("a.jpg", "image/jpeg", [1]), token),
            "update" => FeatureAttachments.UpdateAsync(
                Describe(), table, attachments, 1, 1, new AttachmentUpload("a.jpg", "image/jpeg", [1]), token),
            _ => FeatureAttachments.DeleteAsync(Describe(), table, attachments, 1, [1], token),
        });
    }

    [Fact]
    public void Layer_metadata_advertises_attachments_only_when_served()
    {
        var served = EsriLayerModel.Describe(0, ServedDataset(), editable: false, hasAttachments: true);
        Assert.True(served.HasAttachments);
        Assert.NotNull(served.AttachmentProperties);
        var properties = served.AttachmentProperties!;
        foreach (var name in new[] { "id", "name", "size", "contentType", "keywords" })
        {
            Assert.Contains(properties, property => property.Name == name && property.IsEnabled);
        }

        var unserved = EsriLayerModel.Describe(0, ServedDataset(), editable: false);
        Assert.False(unserved.HasAttachments);
        Assert.Null(unserved.AttachmentProperties);

        static DatasetDescription ServedDataset() =>
            new("test.places", "test", "places", "geometry", 4326, "Point", 2, ["id"], FakeTable.Schema);
    }

    private static async Task<JsonElement> ExecuteAsync(Task<IResult> pending)
    {
        var result = await pending;
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private sealed class FakeTable : IFeatureStore
    {
        public static readonly FeatureSchema Schema = new(
        [
            new FieldDefinition("id", AttributeKind.Int64, nullable: true),
            new FieldDefinition("name", AttributeKind.String),
        ]);

        private static Feature Row(long id, string name) =>
            new(new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)), Schema,
            [
                AttributeValue.FromInt64(id),
                AttributeValue.FromString(name),
            ]);

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(Schema, [Row(1, "Berlin"), Row(2, "Paris")])];
            return Task.FromResult(batches);
        }

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
            string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            ScanAsync(dataset, cancellationToken);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>A minimal attachment fake: per-feature ids from one, honouring cancellation like a real store.</summary>
    private sealed class FakeAttachments : IFeatureAttachmentStore
    {
        private readonly Dictionary<string, List<FeatureAttachmentDescriptor>> _descriptors = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<byte[]>> _blobs = new(StringComparer.Ordinal);

        private static string Key(string dataset, FeatureId featureId) => $"{dataset}#{featureId.Value}";

        public Task<IReadOnlyList<FeatureAttachmentDescriptor>> ListAsync(
            string dataset, FeatureId featureId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<FeatureAttachmentDescriptor> list = _descriptors.GetValueOrDefault(Key(dataset, featureId), []);
            return Task.FromResult(list);
        }

        public Task<FeatureAttachmentDescriptor> AddAsync(
            string dataset, FeatureId featureId, string name, string contentType, byte[] content,
            string? keywords = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(dataset, featureId);
            if (!_descriptors.TryGetValue(key, out var descriptors))
            {
                descriptors = [];
                _descriptors[key] = descriptors;
            }

            if (!_blobs.TryGetValue(key, out var blobs))
            {
                blobs = [];
                _blobs[key] = blobs;
            }

            var descriptor = new FeatureAttachmentDescriptor(descriptors.Count + 1, name, contentType, content.Length, keywords);
            descriptors.Add(descriptor);
            blobs.Add((byte[])content.Clone());
            return Task.FromResult(descriptor);
        }

        public Task<FeatureAttachmentContent> GetAsync(
            string dataset, FeatureId featureId, long attachmentId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(dataset, featureId);
            var descriptors = _descriptors.GetValueOrDefault(key, []);
            var index = descriptors.FindIndex(descriptor => descriptor.Id == attachmentId);
            if (index < 0)
            {
                throw SpatialException.Missing($"No attachment with identity '{attachmentId}' exists.");
            }

            return Task.FromResult(new FeatureAttachmentContent(
                descriptors[index], (byte[])_blobs[key][index].Clone()));
        }

        public Task<FeatureAttachmentDescriptor> UpdateAsync(
            string dataset, FeatureId featureId, long attachmentId, string name, string contentType, byte[] content,
            string? keywords = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(dataset, featureId);
            var descriptors = _descriptors.GetValueOrDefault(key, []);
            var index = descriptors.FindIndex(descriptor => descriptor.Id == attachmentId);
            if (index < 0)
            {
                throw SpatialException.Missing($"No attachment with identity '{attachmentId}' exists.");
            }

            var descriptor = new FeatureAttachmentDescriptor(attachmentId, name, contentType, content.Length, keywords);
            descriptors[index] = descriptor;
            _blobs[key][index] = (byte[])content.Clone();
            return Task.FromResult(descriptor);
        }

        public Task<IReadOnlyList<FeatureAttachmentOutcome>> DeleteAsync(
            string dataset, FeatureId featureId, IReadOnlyList<long> attachmentIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = Key(dataset, featureId);
            var outcomes = new List<FeatureAttachmentOutcome>(attachmentIds.Count);
            foreach (var attachmentId in attachmentIds)
            {
                var descriptors = _descriptors.GetValueOrDefault(key, []).ToList();
                var index = descriptors.FindIndex(descriptor => descriptor.Id == attachmentId);
                if (index < 0)
                {
                    outcomes.Add(FeatureAttachmentOutcome.Failure(
                        attachmentId, SpatialException.NotFound, $"No attachment with identity '{attachmentId}' exists."));
                    continue;
                }

                descriptors.RemoveAt(index);
                _descriptors[key] = descriptors;
                var blobs = _blobs[key].ToList();
                blobs.RemoveAt(index);
                _blobs[key] = blobs;
                outcomes.Add(FeatureAttachmentOutcome.Success(attachmentId));
            }

            return Task.FromResult<IReadOnlyList<FeatureAttachmentOutcome>>(outcomes);
        }
    }
}
