using Spatial.PluginSdk;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The store's pre-connection validation (ADR-0028): malformed dataset
/// identifiers, missing configuration and inverted bounding boxes fail with
/// <c>invalid.arguments</c>/<c>store.unavailable</c> before any connection
/// is opened, so these paths need no live PostGIS.
/// </summary>
public sealed class PostgisStoreValidationTests
{
    [Theory]
    [InlineData(".places")]
    [InlineData("a.b.c")]
    [InlineData("public.place; drop table x")]
    public async Task Invalid_dataset_names_are_rejected_before_connecting(string dataset)
    {
        var store = new PostgisStore(new PostgisOptions());

        var query = await Assert.ThrowsAsync<SpatialException>(() => store.QueryAsync(dataset));
        Assert.Equal(SpatialException.InvalidArguments, query.Code);

        var describe = await Assert.ThrowsAsync<SpatialException>(() => store.DescribeAsync(dataset));
        Assert.Equal(SpatialException.InvalidArguments, describe.Code);

        var scan = await Assert.ThrowsAsync<SpatialException>(() => store.ScanAsync(dataset));
        Assert.Equal(SpatialException.InvalidArguments, scan.Code);
    }

    [Fact]
    public async Task Unconfigured_store_reports_unavailable_without_connecting()
    {
        var variable = PostgisOptions.EnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);

            var store = new PostgisStore(new PostgisOptions());

            var exception = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
            Assert.Equal(SpatialException.StoreUnavailable, exception.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Inverted_bounding_boxes_are_rejected_before_connecting()
    {
        var store = new PostgisStore(new PostgisOptions
        {
            ConnectionString = "Host=unreachable.invalid;Database=spatial;Username=spatial;Password=pw",
        });

        var exception = await Assert.ThrowsAsync<SpatialException>(() =>
            store.QueryAsync("public.places", new BoundingBox(2, 0, 1, 0)));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public async Task Unknown_transactions_are_rejected_without_a_connection()
    {
        var store = new PostgisStore(new PostgisOptions
        {
            ConnectionString = "Host=unreachable.invalid;Database=spatial;Username=spatial;Password=pw",
        });

        var commit = await Assert.ThrowsAsync<SpatialException>(() => store.CommitAsync("missing"));
        Assert.Equal(SpatialException.InvalidArguments, commit.Code);

        var rollback = await Assert.ThrowsAsync<SpatialException>(() => store.RollbackAsync("missing"));
        Assert.Equal(SpatialException.InvalidArguments, rollback.Code);
    }

    [Fact]
    public async Task Commit_and_rollback_reject_null_handles()
    {
        var store = new PostgisStore(new PostgisOptions
        {
            ConnectionString = "Host=unreachable.invalid;Database=spatial;Username=spatial;Password=pw",
        });

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.CommitAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () => await store.RollbackAsync(null!));
    }
}
