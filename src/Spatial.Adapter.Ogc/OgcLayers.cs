using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Layer selection shared by WMS and WFS (ADR-0052 §3): only feature layers
/// feed the OGC projections, and a requested name resolves against the
/// layer's published name (its <see cref="MapLayer.Name"/> or dataset).
/// </summary>
internal static class OgcLayers
{
    /// <summary>The feature layers of a map, in draw order.</summary>
    public static IReadOnlyList<MapLayer> FeatureLayers(Map map) =>
        map.Layers.Where(layer => layer.Kind == MapLayerKind.Feature).ToArray();

    /// <summary>The OGC-visible name of a layer.</summary>
    public static string NameOf(MapLayer layer) => layer.Name ?? layer.Dataset;

    /// <summary>
    /// Selects the requested layers in request order, or every feature layer
    /// when <paramref name="names"/> is empty. An unknown name is an OGC
    /// <c>LayerNotDefined</c> failure.
    /// </summary>
    public static IReadOnlyList<MapLayer> Select(Map map, IReadOnlyList<string> names)
    {
        var layers = FeatureLayers(map);
        if (names.Count == 0)
        {
            return layers;
        }

        var selected = new List<MapLayer>(names.Count);
        foreach (var name in names)
        {
            selected.Add(
                layers.FirstOrDefault(layer => string.Equals(NameOf(layer), name, StringComparison.OrdinalIgnoreCase))
                ?? throw OgcServiceException.NotDefined($"Layer '{name}' is not defined by service '{map.Name}'."));
        }

        return selected;
    }
}
