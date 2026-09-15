using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>layers</c> selection parameter (spec §4): empty, <c>all</c>,
/// <c>visible</c> or <c>top</c> select every layer; <c>show:id,id</c> selects
/// only those; <c>hide:id,id</c> selects all but those; a bare id list selects
/// those. Selection is by stable publication layer id.
/// </summary>
internal static class MapLayerSelection
{
    public static IReadOnlyList<MapLayerInfo> Select(IReadOnlyList<MapLayerInfo> layers, string? selection, string? layerOption = null)
    {
        ParseLayerOption(layerOption);
        var (ids, include) = Parse(selection);
        return ids is null
            ? layers
            : layers.Where(layer => ids.Contains(layer.Layer.Id) == include).ToArray();
    }

    public static IReadOnlyList<PublishedLayer> Select(IReadOnlyList<PublishedLayer> layers, string? selection, string? layerOption = null)
    {
        ParseLayerOption(layerOption);
        var (ids, include) = Parse(selection);
        return ids is null
            ? layers
            : layers.Where(layer => ids.Contains(layer.Id) == include).ToArray();
    }

    /// <summary>
    /// Validates the export <c>layerOption</c> (S2: <c>all|visible|top</c>).
    /// The engine's layers are flat and always visible with no group
    /// hierarchy, so all three select everything and the <c>layers</c>
    /// parameter refines from there; anything else is a typed
    /// <c>invalid.arguments</c> rather than a silently widened export.
    /// </summary>
    private static void ParseLayerOption(string? layerOption)
    {
        if (string.IsNullOrWhiteSpace(layerOption))
        {
            return;
        }

        if (!layerOption.Trim().Equals("all", StringComparison.OrdinalIgnoreCase)
            && !layerOption.Trim().Equals("visible", StringComparison.OrdinalIgnoreCase)
            && !layerOption.Trim().Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            throw GeoServicesErrors.Invalid(
                $"The 'layerOption' value '{layerOption}' is not supported (all, visible, top).");
        }
    }

    private static (HashSet<int>? Ids, bool Include) Parse(string? selection)
    {
        if (string.IsNullOrWhiteSpace(selection))
        {
            return (null, true);
        }

        var text = selection.Trim();
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase)
            || text.Equals("visible", StringComparison.OrdinalIgnoreCase)
            || text.Equals("top", StringComparison.OrdinalIgnoreCase))
        {
            return (null, true);
        }

        if (text.StartsWith("show:", StringComparison.OrdinalIgnoreCase))
        {
            return (Ids(text[5..]), true);
        }

        if (text.StartsWith("hide:", StringComparison.OrdinalIgnoreCase))
        {
            return (Ids(text[5..]), false);
        }

        return (Ids(text), true);
    }

    private static HashSet<int>? Ids(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }

        return EsriValueParser.ParseInt64s(csv, "layers").Select(value => (int)value).ToHashSet();
    }
}
