using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The runtime connection swap against a real database: flipping the
/// registry breaks reads, flipping back restores them on the same store
/// instance, and a switch drops the description cache — both are properties
/// of the previous database, so answering the new one from them would be
/// wrong, not merely stale.
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisConnectionReloadTests : IClassFixture<PostgisDatabaseFixture>
{
    private const string Refused = "Host=127.0.0.1;Port=1;Database=gis;Username=u;Password=p;Timeout=2";

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private readonly PostgisDatabaseFixture _fixture;

    public PostgisConnectionReloadTests(PostgisDatabaseFixture fixture) => _fixture = fixture;

    private static Feature Row(long id) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Schema,
        [AttributeValue.FromInt64(id), AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id, id))]);

    private static string Unique() => $"public.reload_{Guid.NewGuid().ToString("N")[..8]}";

    [SkippableFact]
    public async Task Flipping_the_registry_breaks_and_restores_reads_without_a_rebuild()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        string? current = _fixture.ConnectionString;
        await using var store = new PostgisStore(new PostgisOptions(), new MutableRegistry(() => current));

        var before = await store.ListAsync();
        Assert.NotEmpty(before);

        current = Refused;
        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);

        current = _fixture.ConnectionString;
        var after = await store.ListAsync();
        Assert.Equal(before.Count, after.Count);
    }

    [SkippableFact]
    public async Task A_connection_switch_drops_the_description_cache()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        string? current = _fixture.ConnectionString;
        await using var store = new PostgisStore(new PostgisOptions(), new MutableRegistry(() => current));
        var dataset = Unique();
        await store.CreateAsync(dataset, new FeatureBatch(Schema, [Row(1)]), 4326);

        var before = store.DescriptionReads;
        var first = await store.DescribeAsync(dataset);
        var second = await store.DescribeAsync(dataset);
        Assert.Equal(1, store.DescriptionReads - before);
        Assert.Equal(first.Schema, second.Schema);

        current = Refused;
        await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        current = _fixture.ConnectionString;

        var reread = await store.DescribeAsync(dataset);
        Assert.Equal(2, store.DescriptionReads - before);
        Assert.Equal(first.Schema, reread.Schema);
    }

    private sealed class MutableRegistry(Func<string?> current) : IStoreConnectionRegistry
    {
        public bool TryGetConnectionString(string store, out string? connectionString)
        {
            connectionString = current();
            return connectionString is not null;
        }

        public Task<StoreConnectionStatus> SetAsync(string store, string connectionString, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<StoreConnectionStatus>> ListAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<bool> RemoveAsync(string store, CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<IReadOnlyList<StoreConnectionStatus>> ReloadAsync(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
