using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Host;

/// <summary>
/// The host's <see cref="IStoreRegistry"/> implementation (ADR-0033): the one
/// place the engine turns a request's store name into its keyed faces.
/// It captures the container <see cref="IServiceProvider"/> deliberately —
/// the store key is a request value, so the lookup cannot be ordinary
/// constructor injection — and every boundary adapter depends on the typed
/// registry instead, keeping the container out of protocol code. The keyed
/// stores are singletons, so resolving from the root provider is safe.
/// </summary>
internal sealed class KeyedStoreRegistry(IServiceProvider provider) : IStoreRegistry
{
    public IDataCatalogue Catalogue(string store) =>
        provider.GetKeyedService<IDataCatalogue>(store)
        ?? throw SpatialException.BadArguments($"Unknown store '{store}'.");

    public IFeatureStore Features(string store) =>
        provider.GetKeyedService<IFeatureStore>(store)
        ?? throw SpatialException.BadArguments($"Unknown store '{store}'.");

    public IFeatureEditStore? EditStore(string store) =>
        provider.GetKeyedService<IFeatureEditStore>(store);

    public IFeatureAttachmentStore? AttachmentStore(string store) =>
        provider.GetKeyedService<IFeatureAttachmentStore>(store);

    public ITransactionStore? Transactions(string store) =>
        provider.GetKeyedService<ITransactionStore>(store);

    public IDatasetIngest? Ingest(string store) =>
        provider.GetKeyedService<IDatasetIngest>(store);

    public IRasterCatalogue? RasterCatalogue(string store) =>
        provider.GetKeyedService<IRasterCatalogue>(store);
}
