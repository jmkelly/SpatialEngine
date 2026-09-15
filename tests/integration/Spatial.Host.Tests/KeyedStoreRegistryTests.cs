using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Host.Tests;

/// <summary>
/// The host's keyed-store seam (ADR-0033): the registry turns a request's
/// store name into the registered capabilities and reports an unknown store
/// as a structured <c>invalid.arguments</c> failure.
/// </summary>
public sealed class KeyedStoreRegistryTests
{
    [Fact]
    public void Resolves_the_keyed_store_capabilities()
    {
        var store = new StubStore();
        using var provider = new ServiceCollection()
            .AddKeyedSingleton<IDataCatalogue>("demo", store)
            .AddKeyedSingleton<IFeatureStore>("demo", store)
            .BuildServiceProvider();
        var registry = new KeyedStoreRegistry(provider);

        Assert.Same(store, registry.Catalogue("demo"));
        Assert.Same(store, registry.Features("demo"));
        Assert.Null(registry.EditStore("demo"));
        Assert.Null(registry.Transactions("demo"));
        Assert.Null(registry.Ingest("demo"));
        Assert.Null(registry.RasterCatalogue("demo"));
    }

    [Fact]
    public void An_unknown_store_is_invalid_arguments()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        var registry = new KeyedStoreRegistry(provider);

        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => registry.Catalogue("absent")).Code);
        Assert.Equal(SpatialException.InvalidArguments, Assert.Throws<SpatialException>(() => registry.Features("absent")).Code);
    }

    private sealed class StubStore : IDataCatalogue, IFeatureStore
    {
        public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> CreateAsync(string dataset, Spatial.Core.Features.FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Spatial.Core.Features.FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Spatial.Core.Features.FeatureBatch>> QueryAsync(
            string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> WriteAsync(string dataset, Spatial.Core.Features.FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
