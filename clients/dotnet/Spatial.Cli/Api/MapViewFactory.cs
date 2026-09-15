using System.Text;
using Spatial.Contracts.Providers;

namespace Spatial.Cli;

/// <summary>A map rendered for a client: metadata, its services, GeoServices endpoints and recognised layer styles.</summary>
public sealed record MapView(
    string Name,
    string Kind,
    string Store,
    string? Description,
    string? Copyright,
    string? Endpoint,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<MapLayerView> Layers);

/// <summary>One layer of a <see cref="MapView"/> with its inferred compact style.</summary>
public sealed record MapLayerView(
    int LayerId,
    string Dataset,
    string? Name,
    string? Geometry,
    DrawRecipe? Style);

/// <summary>
/// Turns a core <see cref="Map"/> into the CLI's view (ADR-0053):
/// the GeoServices endpoints its services project to and each layer's compact
/// draw recipe, inferred from the persisted MapLibre fragment (ADR-0047).
/// </summary>
public static class MapViewFactory
{
    /// <summary>Describes a map for <c>map show</c> and <c>map export</c>.</summary>
    public static MapView Describe(Map map, string host, string geoServicesRoot)
    {
        ArgumentNullException.ThrowIfNull(map);
        var endpoints = MapEndpoints.ForAll(host, geoServicesRoot, map);
        return new MapView(
            map.Name,
            KindName(map.Services),
            map.Store,
            map.Description,
            map.Copyright,
            endpoints.Count > 0 ? endpoints[0] : null,
            endpoints,
            map.Layers.Select(DescribeLayer).ToArray());
    }

    /// <summary>Renders a map view as human-readable text.</summary>
    public static string Human(MapView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var builder = new StringBuilder();
        builder.Append(view.Name).Append(" — ").Append(view.Kind).Append(" (").Append(view.Store).Append(')');
        foreach (var endpoint in view.Endpoints)
        {
            builder.Append(Environment.NewLine).Append("  endpoint: ").Append(endpoint);
        }

        foreach (var layer in view.Layers)
        {
            builder.Append(Environment.NewLine)
                .Append("  ").Append(layer.LayerId).Append(": ").Append(layer.Dataset);
            if (layer.Name is not null)
            {
                builder.Append(" (").Append(layer.Name).Append(')');
            }

            if (layer.Geometry is not null)
            {
                builder.Append(" [").Append(layer.Geometry).Append(']');
            }
        }

        return builder.ToString();
    }

    private static MapLayerView DescribeLayer(MapLayer layer)
    {
        var hasStyle = MapLibreStyleBuilder.TryDescribe(layer.Style, out var recipe, out var family);
        return new MapLayerView(
            layer.LayerId,
            layer.Dataset,
            layer.Name,
            hasStyle ? GeometryFamilies.Name(family) : null,
            hasStyle ? recipe : null);
    }

    private static string KindName(IReadOnlyList<MapServiceKind> services) =>
        services.Count == 0
            ? "none"
            : string.Join("+", services.Select(WireKey));

    /// <summary>The pinned JSON wire key for a service (see <see cref="MapServiceKind"/>).</summary>
    private static string WireKey(MapServiceKind service) => service switch
    {
        MapServiceKind.FeatureServer => "feature",
        MapServiceKind.MapServer => "map",
        MapServiceKind.Tiles => "tiles",
        MapServiceKind.Wms => "wms",
        MapServiceKind.Wfs => "wfs",
        MapServiceKind.ImageServer => "image",
        _ => service.ToString().ToLowerInvariant(),
    };
}
