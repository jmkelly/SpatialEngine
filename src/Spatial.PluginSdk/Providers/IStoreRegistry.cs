namespace Spatial.PluginSdk.Providers;

/// <summary>
/// Resolves the engine's keyed data stores (ADR-0033) by name. The store key
/// is a request value, so a protocol adapter cannot receive its capabilities
/// by ordinary constructor injection; this is the one typed seam through
/// which a runtime store name becomes its <see cref="IDataCatalogue"/>,
/// <see cref="IFeatureStore"/> and additive capability faces. Implementations
/// resolve the keyed services; a boundary adapter depends on this contract,
/// never on the DI container.
///
/// <para>The required read capabilities (<see cref="Catalogue"/> and
/// <see cref="Features"/>) throw <c>invalid.arguments</c> for an unknown
/// store. The additive capabilities return <c>null</c> when the store does
/// not provide them, so the caller keeps the error code that fits its
/// protocol (a read-only Feature Server versus an Image Server with no raster
/// provider).</para>
/// </summary>
public interface IStoreRegistry
{
    /// <summary>The keyed dataset catalogue for <paramref name="store"/>; an unknown store is <c>invalid.arguments</c>.</summary>
    IDataCatalogue Catalogue(string store);

    /// <summary>The keyed feature store for <paramref name="store"/>; an unknown store is <c>invalid.arguments</c>.</summary>
    IFeatureStore Features(string store);

    /// <summary>The keyed editing capability for <paramref name="store"/>, or <c>null</c> when the store is read-only.</summary>
    IFeatureEditStore? EditStore(string store);

    /// <summary>The keyed attachment capability for <paramref name="store"/>, or <c>null</c> when the store holds no attachment blobs.</summary>
    IFeatureAttachmentStore? AttachmentStore(string store);

    /// <summary>The keyed transaction capability for <paramref name="store"/>, or <c>null</c> when the store has none.</summary>
    ITransactionStore? Transactions(string store);

    /// <summary>The keyed ingest capability for <paramref name="store"/>, or <c>null</c> when the store cannot ingest.</summary>
    IDatasetIngest? Ingest(string store);

    /// <summary>The keyed raster catalogue for <paramref name="store"/>, or <c>null</c> when the store has no raster provider.</summary>
    IRasterCatalogue? RasterCatalogue(string store);
}
