using Spatial.Core.Features;
using Spatial.PluginSdk;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The attachment store's pre-connection validation (T-088, ADR-0065 §2):
/// null inputs, empty names, null content, over-quota bytes, malformed
/// dataset identifiers, missing configuration, unreachable hosts and
/// cancellation fail before (or without) touching a live PostGIS, so these
/// paths need no container. They pin the failure and cancellation contracts
/// across all five <c>IFeatureAttachmentStore</c> operations.
/// </summary>
public sealed class PostgisAttachmentStoreValidationTests
{
    [Fact]
    public void Constructor_rejects_a_null_store()
    {
        Assert.Throws<ArgumentNullException>(() => new PostgisAttachmentStore(null!));
    }

    [Fact]
    public async Task Delete_rejects_null_attachment_ids()
    {
        var attachments = Attachments(UnreachableConnectionString);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            attachments.DeleteAsync("public.places", new FeatureId("1"), null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Add_and_update_reject_an_empty_name_before_connecting(string name)
    {
        var attachments = Attachments(UnreachableConnectionString);

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("public.places", new FeatureId("1"), name, "image/png", [1]));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.UpdateAsync("public.places", new FeatureId("1"), 1, name, "image/png", [1]));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);
    }

    [Fact]
    public async Task Add_and_update_reject_null_content_before_connecting()
    {
        var attachments = Attachments(UnreachableConnectionString);

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("public.places", new FeatureId("1"), "a.bin", "application/octet-stream", null!));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.UpdateAsync("public.places", new FeatureId("1"), 1, "a.bin", "application/octet-stream", null!));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);
    }

    [Fact]
    public void The_default_quota_matches_the_memory_provider_cap()
    {
        Assert.Equal(10_485_760, PostgisAttachmentStore.DefaultMaxBytesPerAttachment);
    }

    [Fact]
    public async Task Add_and_update_reject_over_quota_content_before_connecting()
    {
        var attachments = new PostgisAttachmentStore(
            new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString }),
            maxBytesPerAttachment: 4);

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("public.places", new FeatureId("1"), "big.bin", "application/octet-stream", [1, 2, 3, 4, 5]));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.UpdateAsync("public.places", new FeatureId("1"), 1, "big.bin", "application/octet-stream", [1, 2, 3, 4, 5]));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);
    }

    [Theory]
    [InlineData(".places")]
    [InlineData("a.b.c")]
    [InlineData("public.place; drop table x")]
    public async Task Attachment_operations_reject_invalid_dataset_names_before_connecting(string dataset)
    {
        var attachments = Attachments(UnreachableConnectionString);
        var feature = new FeatureId("1");

        var list = await Assert.ThrowsAsync<SpatialException>(() => attachments.ListAsync(dataset, feature));
        Assert.Equal(SpatialException.InvalidArguments, list.Code);

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync(dataset, feature, "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var get = await Assert.ThrowsAsync<SpatialException>(() => attachments.GetAsync(dataset, feature, 1));
        Assert.Equal(SpatialException.InvalidArguments, get.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.UpdateAsync(dataset, feature, 1, "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);

        var delete = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.DeleteAsync(dataset, feature, [1]));
        Assert.Equal(SpatialException.InvalidArguments, delete.Code);
    }

    [Fact]
    public async Task Attachment_operations_on_an_unconfigured_store_are_unavailable_without_connecting()
    {
        var variable = PostgisOptions.EnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            var attachments = new PostgisAttachmentStore(new PostgisStore(new PostgisOptions()));
            var feature = new FeatureId("1");

            var list = await Assert.ThrowsAsync<SpatialException>(() => attachments.ListAsync("public.places", feature));
            Assert.Equal(SpatialException.StoreUnavailable, list.Code);

            var add = await Assert.ThrowsAsync<SpatialException>(() =>
                attachments.AddAsync("public.places", feature, "a.bin", "application/octet-stream", [1]));
            Assert.Equal(SpatialException.StoreUnavailable, add.Code);

            var get = await Assert.ThrowsAsync<SpatialException>(() => attachments.GetAsync("public.places", feature, 1));
            Assert.Equal(SpatialException.StoreUnavailable, get.Code);

            var update = await Assert.ThrowsAsync<SpatialException>(() =>
                attachments.UpdateAsync("public.places", feature, 1, "a.bin", "application/octet-stream", [1]));
            Assert.Equal(SpatialException.StoreUnavailable, update.Code);

            var delete = await Assert.ThrowsAsync<SpatialException>(() =>
                attachments.DeleteAsync("public.places", feature, [1]));
            Assert.Equal(SpatialException.StoreUnavailable, delete.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Attachment_operations_against_an_unreachable_host_are_unavailable()
    {
        var attachments = Attachments(UnreachableConnectionString);
        var feature = new FeatureId("1");

        var list = await Assert.ThrowsAsync<SpatialException>(() => attachments.ListAsync("public.places", feature));
        Assert.Equal(SpatialException.StoreUnavailable, list.Code);

        var add = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.AddAsync("public.places", feature, "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.StoreUnavailable, add.Code);

        var get = await Assert.ThrowsAsync<SpatialException>(() => attachments.GetAsync("public.places", feature, 1));
        Assert.Equal(SpatialException.StoreUnavailable, get.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.UpdateAsync("public.places", feature, 1, "a.bin", "application/octet-stream", [1]));
        Assert.Equal(SpatialException.StoreUnavailable, update.Code);

        var delete = await Assert.ThrowsAsync<SpatialException>(() =>
            attachments.DeleteAsync("public.places", feature, [1]));
        Assert.Equal(SpatialException.StoreUnavailable, delete.Code);
    }

    [Fact]
    public async Task Attachment_operations_honour_cancellation_before_connecting()
    {
        var attachments = Attachments(UnreachableConnectionString);
        var feature = new FeatureId("1");
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.ListAsync("public.places", feature, canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.AddAsync("public.places", feature, "a.bin", "application/octet-stream", [1], cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.GetAsync("public.places", feature, 1, canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.UpdateAsync("public.places", feature, 1, "a.bin", "application/octet-stream", [1], cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            attachments.DeleteAsync("public.places", feature, [1], canceled));
    }

    private static PostgisAttachmentStore Attachments(string connectionString) =>
        new(new PostgisStore(new PostgisOptions { ConnectionString = connectionString }));

    private const string UnreachableConnectionString =
        "Host=unreachable.invalid;Database=spatial;Username=spatial;Password=pw";
}
