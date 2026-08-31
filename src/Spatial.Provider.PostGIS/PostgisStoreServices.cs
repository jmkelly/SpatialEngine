using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Data;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// Composition root for the store-bound services of the PostGIS provider
/// (ADR-0028): owns the shared <see cref="PostgisSchemaReader"/> and
/// <see cref="PostgisTransactionRegistry"/> and wires each capability family's
/// service once, so the facade only depends on the services themselves.
/// </summary>
internal static class PostgisStoreServices
{
    public static (
        PostgisCatalogueService Catalogue,
        PostgisDatasetService Dataset,
        PostgisScanService Scan,
        PostgisQueryService Query,
        PostgisBatchWriter Writer,
        PostgisTransactionService Transactions) Create(PostgisConnectionConfiguration configuration, Lazy<PostgisDataStore> store)
    {
        var schemaReader = new PostgisSchemaReader(store);
        var transactions = new PostgisTransactionRegistry();
        var scan = new PostgisFeatureScan(configuration, store);
        return (
            new PostgisCatalogueService(configuration, store),
            new PostgisDatasetService(configuration, store, schemaReader),
            new PostgisScanService(schemaReader, scan),
            new PostgisQueryService(schemaReader, scan),
            new PostgisBatchWriter(store, schemaReader, transactions),
            new PostgisTransactionService(store, transactions));
    }
}
