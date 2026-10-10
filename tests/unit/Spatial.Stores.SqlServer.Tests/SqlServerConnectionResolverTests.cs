using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Stores.SqlServer;
using Spatial.Stores.SqlServer.Configuration;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The live connection seam, test-first: a resolver-backed configuration
/// reports the current string (unconfigured until one arrives), and a store
/// over a registry consults it per operation — flipping the registry flips
/// the store's behaviour without a rebuild.
/// </summary>
public sealed class SqlServerConnectionResolverTests
{
    private const string Live = "Server=db.local;Database=gis;User Id=reader;Password=s3cret";
    private const string Refused = "Server=127.0.0.1,1;Database=gis;User Id=u;Password=p;Connect Timeout=2;TrustServerCertificate=True";

    [Fact]
    public void Resolver_reports_unconfigured_until_a_string_arrives()
    {
        string? current = null;
        var configuration = SqlServerConnectionConfiguration.FromResolver(() => current);

        Assert.False(configuration.IsConfigured);
        Assert.Equal(string.Empty, configuration.Secret);
        Assert.Contains("SPATIAL_SQLSERVER_CONNECTION", configuration.RedactedKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_reflects_the_current_string_and_scrubs_it()
    {
        string? current = Live;
        var configuration = SqlServerConnectionConfiguration.FromResolver(() => current);

        Assert.True(configuration.IsConfigured);
        Assert.Equal(Live, configuration.Secret);
        Assert.Contains("gis", configuration.RedactedKey, StringComparison.Ordinal);
        var scrubbed = configuration.Redact($"connecting with {Live} and s3cret failed");
        Assert.DoesNotContain("s3cret", scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(Live, scrubbed, StringComparison.Ordinal);

        current = null;
        Assert.False(configuration.IsConfigured);
    }

    [Fact]
    public async Task Store_without_a_registry_entry_reports_unconfigured()
    {
        var registry = new MutableRegistry(() => null);
        var store = new SqlServerStore(new SqlServerOptions(), registry);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        Assert.Contains("No connection configuration", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Store_reads_the_registry_on_every_operation()
    {
        string? current = Refused;
        var registry = new MutableRegistry(() => current);
        var store = new SqlServerStore(new SqlServerOptions(), registry);

        var refused = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Equal(SpatialException.StoreUnavailable, refused.Code);
        Assert.Contains("SQL Server store failed", refused.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("p;", refused.Message, StringComparison.Ordinal);

        current = null;
        var unconfigured = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Equal(SpatialException.StoreUnavailable, unconfigured.Code);
        Assert.Contains("No connection configuration", unconfigured.Message, StringComparison.Ordinal);

        current = Refused;
        var refusedAgain = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Contains("SQL Server store failed", refusedAgain.Message, StringComparison.Ordinal);
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
