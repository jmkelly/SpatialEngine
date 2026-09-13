using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Resolves the render inputs for an OGC <c>GetMap</c> (ADR-0053 §3): the
/// selected layers become <see cref="MapLayerSource"/> entries with their
/// keyed store and catalogue, so the renderer receives resolved services and
/// never locates a store itself.
/// </summary>
internal static class OgcRender
{
    public static IReadOnlyList<MapLayerSource> Sources(OgcRequestServices services, Map map, IReadOnlyList<MapLayer> layers)
    {
        var sources = new List<MapLayerSource>(layers.Count);
        foreach (var layer in layers)
        {
            var store = layer.Store ?? map.Store;
            sources.Add(new MapLayerSource(layer.Dataset, services.Features(store), services.Catalogue(store)));
        }

        return sources;
    }
}
