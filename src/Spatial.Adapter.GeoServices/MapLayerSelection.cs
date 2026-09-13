namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>layers</c> selection parameter (spec §4): empty, <c>all</c>,
/// <c>visible</c> or <c>top</c> select every layer; <c>show:id,id</c> selects
/// only those; <c>hide:id,id</c> selects all but those; a bare id list selects
/// those. Selection is by stable publication layer id.
/// </summary>
internal static class MapLayerSelection
{
    public static IReadOnlyList<MapLayerInfo> Select(IReadOnlyList<MapLayerInfo> layers, string? selection)
    {
        var (ids, include) = Parse(selection);
        return ids is null
            ? layers
            : layers.Where(layer => ids.Contains(layer.Layer.Id) == include).ToArray();
    }

    public static IReadOnlyList<PublishedLayer> Select(IReadOnlyList<PublishedLayer> layers, string? selection)
    {
        var (ids, include) = Parse(selection);
        return ids is null
            ? layers
            : layers.Where(layer => ids.Contains(layer.Id) == include).ToArray();
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
