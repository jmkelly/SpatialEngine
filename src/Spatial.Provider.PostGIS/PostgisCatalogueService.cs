using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Data;
using Spatial.Provider.PostGIS.Streams;

namespace Spatial.Provider.PostGIS;

/// <summary>
/// The catalogue.list surface (ADR-0028): mints the catalogue metadata stream
/// and hands the emission loop to <see cref="PostgisMetadataEmitter"/>.
/// Stateless — failures belonging to the invocation propagate to the guarded
/// facade; stream failures stay on the channel.
/// </summary>
internal sealed class PostgisCatalogueService
{
    private readonly PostgisConnectionConfiguration _configuration;
    private readonly Lazy<PostgisDataStore> _store;

    public PostgisCatalogueService(PostgisConnectionConfiguration configuration, Lazy<PostgisDataStore> store)
    {
        _configuration = configuration;
        _store = store;
    }

    /// <summary>Mints the catalogue stream and starts emitting it; stream failures stay on the channel.</summary>
    public async ValueTask<CapabilityResult> StartCatalogueStreamAsync(
        CapabilityInvocation invocation,
        ICapabilityFacilities facilities,
        string? pattern)
    {
        var channel = facilities.Streams.Create(ProviderResourceKinds.CatalogueStream, FeatureBatchStream.StreamCapacity);
        _ = PostgisMetadataEmitter.EmitCatalogueAsync(_configuration, _store, invocation.Capability, channel, pattern, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }
}
