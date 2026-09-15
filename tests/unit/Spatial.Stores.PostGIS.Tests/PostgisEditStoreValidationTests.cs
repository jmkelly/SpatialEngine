using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The edit store's pre-connection validation: null inputs, malformed
/// dataset identifiers, missing configuration, unreachable hosts and
/// cancellation fail before (or without) touching a live PostGIS, so these
/// paths need no container. They pin the <c>DeleteAsync</c> /
/// <c>EditBatchAsync</c> (via <c>AddAsync</c>/<c>UpdateAsync</c>) failure
/// and cancellation contracts (ADR-0037).
/// </summary>
public sealed class PostgisEditStoreValidationTests
{
    [Fact]
    public void Constructor_rejects_a_null_store()
    {
        Assert.Throws<ArgumentNullException>(() => new PostgisEditStore(null!));
    }

    [Fact]
    public async Task Add_rejects_a_null_batch()
    {
        var editor = Editor(UnreachableConnectionString);

        await Assert.ThrowsAsync<ArgumentNullException>(() => editor.AddAsync("public.places", null!));
    }

    [Fact]
    public async Task Update_rejects_a_null_batch()
    {
        var editor = Editor(UnreachableConnectionString);

        await Assert.ThrowsAsync<ArgumentNullException>(() => editor.UpdateAsync("public.places", null!));
    }

    [Fact]
    public async Task Delete_rejects_null_feature_ids()
    {
        var editor = Editor(UnreachableConnectionString);

        await Assert.ThrowsAsync<ArgumentNullException>(() => editor.DeleteAsync("public.places", null!));
    }

    [Theory]
    [InlineData(".places")]
    [InlineData("a.b.c")]
    [InlineData("public.place; drop table x")]
    public async Task Edit_operations_reject_invalid_dataset_names_before_connecting(string dataset)
    {
        var editor = Editor(UnreachableConnectionString);
        var batch = EmptyBatch();

        var add = await Assert.ThrowsAsync<SpatialException>(() => editor.AddAsync(dataset, batch));
        Assert.Equal(SpatialException.InvalidArguments, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() => editor.UpdateAsync(dataset, batch));
        Assert.Equal(SpatialException.InvalidArguments, update.Code);

        var delete = await Assert.ThrowsAsync<SpatialException>(() => editor.DeleteAsync(dataset, [new FeatureId("1")]));
        Assert.Equal(SpatialException.InvalidArguments, delete.Code);
    }

    [Fact]
    public async Task Edit_operations_on_an_unconfigured_store_are_unavailable_without_connecting()
    {
        var variable = PostgisOptions.EnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            var editor = new PostgisEditStore(new PostgisStore(new PostgisOptions()));
            var batch = EmptyBatch();

            var add = await Assert.ThrowsAsync<SpatialException>(() => editor.AddAsync("public.places", batch));
            Assert.Equal(SpatialException.StoreUnavailable, add.Code);

            var update = await Assert.ThrowsAsync<SpatialException>(() => editor.UpdateAsync("public.places", batch));
            Assert.Equal(SpatialException.StoreUnavailable, update.Code);

            var delete = await Assert.ThrowsAsync<SpatialException>(() => editor.DeleteAsync("public.places", [new FeatureId("1")]));
            Assert.Equal(SpatialException.StoreUnavailable, delete.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Edit_operations_against_an_unreachable_host_are_unavailable()
    {
        var editor = Editor(UnreachableConnectionString);
        var batch = EmptyBatch();

        var add = await Assert.ThrowsAsync<SpatialException>(() => editor.AddAsync("public.places", batch));
        Assert.Equal(SpatialException.StoreUnavailable, add.Code);

        var update = await Assert.ThrowsAsync<SpatialException>(() => editor.UpdateAsync("public.places", batch));
        Assert.Equal(SpatialException.StoreUnavailable, update.Code);

        var delete = await Assert.ThrowsAsync<SpatialException>(() => editor.DeleteAsync("public.places", [new FeatureId("1")]));
        Assert.Equal(SpatialException.StoreUnavailable, delete.Code);
    }

    [Fact]
    public async Task Edit_operations_honour_cancellation_before_connecting()
    {
        var editor = Editor(UnreachableConnectionString);
        var batch = EmptyBatch();
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            editor.AddAsync("public.places", batch, cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            editor.UpdateAsync("public.places", batch, cancellationToken: canceled));
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            editor.DeleteAsync("public.places", [new FeatureId("1")], cancellationToken: canceled));
    }

    private static PostgisEditStore Editor(string connectionString) =>
        new(new PostgisStore(new PostgisOptions { ConnectionString = connectionString }));

    private static FeatureBatch EmptyBatch() =>
        new(new FeatureSchema([new FieldDefinition("id", AttributeKind.Int64, false)]), []);

    private const string UnreachableConnectionString =
        "Host=unreachable.invalid;Database=spatial;Username=spatial;Password=pw";
}
