using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The store's read-by-identity and dataset-creation validation (ADR-0038,
/// ADR-0033): null inputs, malformed dataset identifiers, unresolvable
/// sample geometries, missing configuration, unreachable hosts and
/// cancellation fail before (or without) touching a live PostGIS, so these
/// paths need no container. They pin the <c>GetAsync</c> /
/// <c>CreateAsync</c> success, failure and cancellation contracts in the
/// same style as the edit-store validation slice (T-002).
/// </summary>
public sealed class PostgisStoreReadWriteValidationTests
{
    [Fact]
    public async Task Get_rejects_null_ids()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.GetAsync("public.places", null!));
    }

    [Theory]
    [InlineData(".places")]
    [InlineData("a.b.c")]
    [InlineData("public.place; drop table x")]
    public async Task Get_rejects_invalid_dataset_names_before_connecting(string dataset)
    {
        var store = new PostgisStore(new PostgisOptions());

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.GetAsync(dataset, [new FeatureId("1")]));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task Get_on_an_unconfigured_store_is_unavailable_without_connecting()
    {
        var variable = PostgisOptions.EnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            var store = new PostgisStore(new PostgisOptions());

            var failure = await Assert.ThrowsAsync<SpatialException>(() =>
                store.GetAsync("public.places", [new FeatureId("1")]));

            Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Get_with_no_ids_returns_empty_without_connecting()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });

        var found = await store.GetAsync("public.places", []);

        Assert.Empty(found);
    }

    [Fact]
    public async Task Get_against_an_unreachable_host_is_unavailable()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.GetAsync("public.places", [new FeatureId("1")]));

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
    }

    [Fact]
    public async Task Get_honours_cancellation_before_connecting()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.GetAsync("public.places", [new FeatureId("1")], canceled));
    }

    [Fact]
    public async Task Create_rejects_a_null_sample()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });

        await Assert.ThrowsAsync<ArgumentNullException>(() => store.CreateAsync("public.places", null!, 4326));
    }

    [Theory]
    [InlineData(".places")]
    [InlineData("a.b.c")]
    [InlineData("public.place; drop table x")]
    public async Task Create_rejects_invalid_dataset_names_before_connecting(string dataset)
    {
        var store = new PostgisStore(new PostgisOptions());

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.CreateAsync(dataset, EmptySample(), 4326));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task Create_with_mixed_geometry_layouts_is_rejected_before_connecting()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.CreateAsync("public.places", MixedLayoutSample(), 4326));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("cannot be created", failure.Message);
    }

    [Fact]
    public async Task Create_on_an_unconfigured_store_is_unavailable_without_connecting()
    {
        var variable = PostgisOptions.EnvironmentVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, null);
            var store = new PostgisStore(new PostgisOptions());

            var failure = await Assert.ThrowsAsync<SpatialException>(() =>
                store.CreateAsync("public.places", EmptySample(), 4326));

            Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task Create_against_an_unreachable_host_is_unavailable()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });

        var failure = await Assert.ThrowsAsync<SpatialException>(() =>
            store.CreateAsync("public.places", EmptySample(), 4326));

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
    }

    [Fact]
    public async Task Create_honours_cancellation_before_connecting()
    {
        var store = new PostgisStore(new PostgisOptions { ConnectionString = UnreachableConnectionString });
        var canceled = new CancellationToken(canceled: true);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            store.CreateAsync("public.places", EmptySample(), 4326, canceled));
    }

    private static FeatureBatch EmptySample() =>
        new(new FeatureSchema([new FieldDefinition("geometry", AttributeKind.Geometry)]), []);

    private static FeatureBatch MixedLayoutSample()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var crs = CoordinateReference.Epsg(4326);
        return new FeatureBatch(schema,
        [
            new Feature(
                new FeatureId("1"),
                schema,
                [
                    AttributeValue.FromString("Flat"),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, crs)),
                ]),
            new Feature(
                new FeatureId("2"),
                schema,
                [
                    AttributeValue.FromString("High"),
                    AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, 3, crs)),
                ]),
        ]);
    }

    private const string UnreachableConnectionString =
        "Host=unreachable.invalid;Database=spatial;Username=spatial;Password=pw";
}
