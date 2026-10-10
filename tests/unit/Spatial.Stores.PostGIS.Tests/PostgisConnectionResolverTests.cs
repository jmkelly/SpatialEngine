using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Stores.PostGIS;
using Spatial.Stores.PostGIS.Configuration;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The live connection seam, test-first: a resolver-backed configuration
/// reports the current string (unconfigured until one arrives), and a store
/// over a registry consults it per operation — flipping the registry flips
/// the store's behaviour without a rebuild.
/// </summary>
public sealed class PostgisConnectionResolverTests
{
    private const string Live = "Host=db.local;Database=gis;Username=reader;Password=s3cret";
    private const string Refused1 = "Host=127.0.0.1;Port=1;Database=gis;Username=u;Password=p;Timeout=2";
    private const string Refused2 = "Host=127.0.0.1;Port=2;Database=gis;Username=u;Password=p;Timeout=2";

    [Fact]
    public void Resolver_reports_unconfigured_until_a_string_arrives()
    {
        string? current = null;
        var configuration = PostgisConnectionConfiguration.FromResolver(() => current);

        Assert.False(configuration.IsConfigured);
        Assert.Equal(string.Empty, configuration.Secret);
        Assert.Contains("SPATIAL_POSTGIS_CONNECTION", configuration.RedactedKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_reflects_the_current_string_and_scrubs_it()
    {
        string? current = Live;
        var configuration = PostgisConnectionConfiguration.FromResolver(() => current);

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
        var store = new PostgisStore(new PostgisOptions(), registry);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
    }

    [Fact]
    public async Task Store_reads_the_registry_on_every_operation()
    {
        string? current = Refused1;
        var registry = new MutableRegistry(() => current);
        var store = new PostgisStore(new PostgisOptions(), registry);

        var first = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Equal(SpatialException.StoreUnavailable, first.Code);
        Assert.Contains(":1", first.Message, StringComparison.Ordinal);

        current = Refused2;
        var second = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());
        Assert.Equal(SpatialException.StoreUnavailable, second.Code);
        Assert.Contains(":2", second.Message, StringComparison.Ordinal);
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
