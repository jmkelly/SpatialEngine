using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>
/// Builds the GeoServices endpoint a map's services project to (ADR-0053):
/// <c>feature</c> → FeatureServer, <c>map</c> → MapServer, <c>image</c> →
/// ImageServer (ADR-0035/0048/0051). The host serves these under
/// <c>Spatial:GeoServices:Root</c> (default <c>/arcgis/rest/services</c>).
/// <c>tiles</c>/<c>wms</c>/<c>wfs</c> have no GeoServices server and are
/// reported as no endpoint.
/// </summary>
public static class MapEndpoints
{
    /// <summary>Joins the host and the GeoServices root into a base URL.</summary>
    public static string Root(string host, string geoServicesRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var trimmedHost = host.TrimEnd('/');
        var root = (geoServicesRoot ?? string.Empty).Trim();
        if (root.Length == 0)
        {
            root = CliSettings.DefaultGeoServicesRoot;
        }

        if (!root.StartsWith('/'))
        {
            root = "/" + root;
        }

        return trimmedHost + root.TrimEnd('/');
    }

    /// <summary>The first GeoServices endpoint of a map's services, or <see langword="null"/> when it has none.</summary>
    public static string? For(string host, string geoServicesRoot, Map map)
    {
        var endpoints = ForAll(host, geoServicesRoot, map);
        return endpoints.Count > 0 ? endpoints[0] : null;
    }

    /// <summary>Every GeoServices endpoint a map's services project to, in declaration order.</summary>
    public static IReadOnlyList<string> ForAll(string host, string geoServicesRoot, Map map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var baseUrl = $"{Root(host, geoServicesRoot)}/{Uri.EscapeDataString(map.Name)}";
        return
        [
            .. map.Services
                .Select(ServiceType)
                .Where(serviceType => serviceType is not null)
                .Select(serviceType => $"{baseUrl}/{serviceType}"),
        ];
    }

    /// <summary>The GeoServices server type for a service, or null when the service is not an Esri server.</summary>
    public static string? ServiceType(MapService service) => service switch
    {
        MapService.Feature => "FeatureServer",
        MapService.Map => "MapServer",
        MapService.Image => "ImageServer",
        _ => null,
    };
}
