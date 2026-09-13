using System.Text.Json;
using System.Text.RegularExpressions;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Provider.Maps;

/// <summary>
/// Pure validation and normalisation of a <see cref="Map"/> (ADR-0052 §2).
/// Map names are flat and identifier-shaped (folder syntax is rejected); a
/// feature layer's dataset uses the strict <c>schema.table</c> grammar, an
/// image layer names a raster dataset; layer ids are unique and non-negative.
/// A negative layer id means "assign the next free id", which is how the host
/// and the admin projections create maps without hard-coding ids.
///
/// <para>Every enabled service must be fed: Feature/Map/Tiles/WMS/WFS need at
/// least one feature layer and Image needs at least one image layer, so a map
/// never advertises a surface it cannot serve.</para>
/// </summary>
internal static class MapValidator
{
    private static readonly Regex NamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex DatasetPattern = new(@"^[a-z_][a-z0-9_]*\.[a-z_][a-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex RasterDatasetPattern = new(@"^[A-Za-z_][A-Za-z0-9_.\-]*$", RegexOptions.Compiled);

    /// <summary>Validates and normalises a map, assigning any negative layer ids.</summary>
    public static Map Normalize(Map map, int nextLayerId)
    {
        ArgumentNullException.ThrowIfNull(map);
        ValidateHeader(map);
        return map with { Layers = NormalizeLayers(map, nextLayerId) };
    }

    private static void ValidateHeader(Map map)
    {
        if (!NamePattern.IsMatch(map.Name ?? string.Empty))
        {
            throw SpatialException.BadArguments(
                $"Map name '{map.Name}' is invalid: expected a flat identifier [A-Za-z_][A-Za-z0-9_]* (folders are not supported).");
        }

        if (string.IsNullOrWhiteSpace(map.Store))
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' needs a non-empty store.");
        }

        if (map.Layers.Count == 0)
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' must expose at least one layer.");
        }

        ValidateServices(map);
    }

    private static void ValidateServices(Map map)
    {
        var seen = new HashSet<MapService>();
        foreach (var service in map.Services)
        {
            if (!Enum.IsDefined(service))
            {
                throw SpatialException.BadArguments($"Map '{map.Name}' has unknown service '{service}'.");
            }

            if (!seen.Add(service))
            {
                throw SpatialException.BadArguments($"Map '{map.Name}' lists service '{service}' more than once.");
            }
        }
    }

    private static List<MapLayer> NormalizeLayers(Map map, int nextLayerId)
    {
        var layers = new List<MapLayer>(map.Layers.Count);
        var ids = new HashSet<int>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var next = nextLayerId;
        var hasFeature = false;
        var hasImage = false;
        foreach (var layer in map.Layers)
        {
            ValidateLayer(map, layer, keys);
            hasFeature |= layer.Kind == MapLayerKind.Feature;
            hasImage |= layer.Kind == MapLayerKind.Image;
            layers.Add(layer with { LayerId = AssignId(map, layer, ids, ref next) });
        }

        RequireLayers(map, hasFeature, hasImage);
        return layers;
    }

    private static void RequireLayers(Map map, bool hasFeature, bool hasImage)
    {
        var needsFeature = map.Services.Any(service => service is MapService.Feature or MapService.Map or MapService.Tiles or MapService.Wms or MapService.Wfs);
        if (needsFeature && !hasFeature)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' enables a vector service but has no feature layer.");
        }

        if (map.Exposes(MapService.Image) && !hasImage)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' enables the image service but has no image layer.");
        }
    }

    private static void ValidateLayer(Map map, MapLayer layer, HashSet<string> keys)
    {
        var dataset = layer.Dataset;
        var pattern = layer.Kind == MapLayerKind.Image ? RasterDatasetPattern : DatasetPattern;
        if (string.IsNullOrEmpty(dataset) || !pattern.IsMatch(dataset))
        {
            var expected = layer.Kind == MapLayerKind.Image
                ? "a raster dataset identifier [A-Za-z_][A-Za-z0-9_.-]*"
                : "schema.table with only [a-z0-9_]";
            throw SpatialException.BadArguments($"Layer dataset '{dataset}' is invalid: expected {expected}.");
        }

        if (!keys.Add($"{layer.Kind}:{layer.Store ?? map.Store}:{dataset}"))
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' lists dataset '{dataset}' more than once.");
        }

        if (layer.Name is { Length: 0 })
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' has a layer with an empty name.");
        }

        ValidateStyle(map, layer);
    }

    /// <summary>
    /// A non-empty layer style must be a JSON array of style-layer objects
    /// (ADR-0047). The renderer still rejects paint it cannot draw; this only
    /// pins the persisted shape so a corrupt style never reaches the file.
    /// </summary>
    private static void ValidateStyle(Map map, MapLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Style))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(layer.Style);
        }
        catch (JsonException exception)
        {
            throw SpatialException.BadArguments(
                $"Map '{map.Name}' layer '{layer.Dataset}' has a style that is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw SpatialException.BadArguments(
                    $"Map '{map.Name}' layer '{layer.Dataset}' style must be an array of style layers.");
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    throw SpatialException.BadArguments(
                        $"Map '{map.Name}' layer '{layer.Dataset}' style entries must be JSON objects.");
                }
            }
        }
    }

    private static int AssignId(Map map, MapLayer layer, HashSet<int> ids, ref int next)
    {
        var id = layer.LayerId < 0 ? next++ : layer.LayerId;
        if (!ids.Add(id))
        {
            throw SpatialException.BadArguments($"Map '{map.Name}' assigns layer id {id} more than once.");
        }

        return id;
    }

    /// <summary>Whether a name is a valid flat map name.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern.IsMatch(name);
}
