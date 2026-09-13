using System.Text.Json;
using System.Text.Json.Nodes;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Host.Api;

/// <summary>
/// Assembles a MapLibre style document (ADR-0044/ADR-0047) from a
/// publication's persisted per-layer fragments: each layer's <c>Style</c>
/// array is copied in draw order, gains a unique <c>id</c>, and is keyed onto
/// its dataset with <c>source-layer</c>. A layer with no persisted style
/// falls back to a neutral symbol covering every geometry family, so a
/// publication still renders. The document is the same dialect the workbench
/// writes, so client and server agree (ADR-0044).
/// </summary>
internal static class PublicationStyleComposer
{
    /// <summary>
    /// A neutral default symbol for a layer with no persisted style: a fill,
    /// a line and a circle, so points, lines and polygons all draw regardless
    /// of the dataset's geometry family.
    /// </summary>
    private const string DefaultFragments =
        """
        [
          {"type":"fill","layout":{"visibility":"visible"},"paint":{"fill-color":"#4fc3f7","fill-opacity":0.3,"fill-outline-color":"#4fc3f7"}},
          {"type":"line","layout":{"visibility":"visible"},"paint":{"line-color":"#4fc3f7","line-width":2,"line-opacity":0.8}},
          {"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#4fc3f7","circle-radius":6,"circle-opacity":0.9,"circle-stroke-color":"#0b0f14","circle-stroke-width":1}}
        ]
        """;

    public static string Compose(Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var layers = new JsonArray();
        foreach (var layer in publication.Layers)
        {
            var fragments = ReadFragments(publication, layer);
            for (var index = 0; index < fragments.Count; index++)
            {
                if (fragments[index] is not JsonObject)
                {
                    throw SpatialException.BadArguments(
                        $"Publication '{publication.Name}' layer '{layer.Dataset}' style entries must be JSON objects.");
                }

                var fragment = (JsonObject)fragments[index]!.DeepClone();
                fragment["id"] ??= $"{layer.LayerId}-{index}";
                fragment["source-layer"] = layer.Dataset;
                layers.Add(fragment);
            }
        }

        return new JsonObject { ["layers"] = layers }.ToJsonString();
    }

    private static JsonArray ReadFragments(Publication publication, PublicationLayer layer)
    {
        if (string.IsNullOrWhiteSpace(layer.Style))
        {
            return (JsonArray)JsonNode.Parse(DefaultFragments)!;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(layer.Style);
        }
        catch (JsonException exception)
        {
            throw SpatialException.BadArguments(
                $"Publication '{publication.Name}' layer '{layer.Dataset}' has a style that is not valid JSON: {exception.Message}");
        }

        return node as JsonArray
            ?? throw SpatialException.BadArguments(
                $"Publication '{publication.Name}' layer '{layer.Dataset}' style must be an array of style layers.");
    }
}
