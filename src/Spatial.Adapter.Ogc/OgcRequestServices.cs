using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The in-process services one OGC request resolves against (ADR-0052 §3):
/// the map registry, the raster renderer and the coordinate transforms. The
/// keyed stores are resolved lazily per layer store, so a mixed map reads
/// each layer from its own store.
/// </summary>
internal sealed record OgcRequestServices(
    IServiceProvider Provider,
    IMapRegistry Registry,
    IMapRenderer Renderer,
    ICoordinateTransforms Transforms)
{
    /// <summary>The keyed data catalogue for a store, or <c>invalid.arguments</c> when unknown.</summary>
    public IDataCatalogue Catalogue(string store) =>
        Provider.GetKeyedService<IDataCatalogue>(store)
        ?? throw SpatialException.BadArguments($"Unknown store '{store}'.");

    /// <summary>The keyed feature store for a store, or <c>invalid.arguments</c> when unknown.</summary>
    public IFeatureStore Features(string store) =>
        Provider.GetKeyedService<IFeatureStore>(store)
        ?? throw SpatialException.BadArguments($"Unknown store '{store}'.");

    /// <summary>Resolves a map and requires it to expose <paramref name="service"/> (404 otherwise).</summary>
    public async Task<Map> ResolveMapAsync(string name, MapService service, string label, CancellationToken cancellationToken)
    {
        Map map;
        try
        {
            map = await Registry.GetAsync(name, cancellationToken);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
            throw OgcServiceException.NotDefined($"{label} service '{name}' was not found.");
        }

        if (!map.Exposes(service))
        {
            throw OgcServiceException.NotDefined($"{label} service '{name}' was not found.");
        }

        return map;
    }

    /// <summary>Loads one feature layer's store and dataset description.</summary>
    public async Task<OgcLayer> LoadAsync(Map map, MapLayer layer, CancellationToken cancellationToken)
    {
        var store = layer.Store ?? map.Store;
        var description = await Catalogue(store).DescribeAsync(layer.Dataset, cancellationToken);
        return new OgcLayer(layer, layer.Name ?? layer.Dataset, store, description);
    }
}
