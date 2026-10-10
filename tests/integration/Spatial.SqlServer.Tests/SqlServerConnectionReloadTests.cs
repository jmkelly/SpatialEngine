using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The runtime connection swap against a real database: flipping the
/// registry breaks reads, flipping back restores them on the same store
/// instance, and a switch drops the description cache.
/// </summary>
[Collection(SqlServerContainerDefinition.Name)]
public sealed class SqlServerConnectionReloadTests : IClassFixture<SqlServerDatabaseFixture>
{
    private const string Refused =
        "Server=127.0.0.1,1;Database=gis;User Id=u;Password=p;Connect Timeout=2;TrustServerCertificate=True";

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, nullable: false),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private readonly SqlServerDatabaseFixture _fixture;

    public SqlServerConnectionReloadTests(SqlServerDatabaseFixture fixture) => _fixture = fixture;

    private static string Unique() => $"dbo.reload_{Guid.NewGuid().ToString("N")[..8]}";

    [SkippableFact]
    public async Task Flipping_the_registry_breaks_and_restores_reads_without_a_rebuild()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        string? current = _fixture.ConnectionString;
        await using var store = new SqlServerStore(new SqlServerOptions(), new MutableRegistry(() => current));

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
        Skip.IfNot(_fixture.DockerAvailable, _fixture.SkipReason);
        string? current = _fixture.ConnectionString;
        await using var store = new SqlServerStore(new SqlServerOptions(), new MutableRegistry(() => current));
        var dataset = Unique();
        await store.CreateAsync(dataset, new FeatureBatch(Schema, []), 4326);

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
