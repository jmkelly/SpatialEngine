using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>
/// Builds the GeoServices endpoint a publication projects to (ADR-0052):
/// <c>feature</c> → FeatureServer, <c>map</c> → MapServer, <c>image</c> →
/// ImageServer (ADR-0035/0048/0051). The host serves these under
/// <c>Spatial:GeoServices:Root</c> (default <c>/arcgis/rest/services</c>).
/// </summary>
public static class PublicationEndpoints
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

    /// <summary>The service root URL for a publication, or <see langword="null"/> for an unknown kind.</summary>
    public static string? For(string host, string geoServicesRoot, Publication publication)
    {
        ArgumentNullException.ThrowIfNull(publication);
        var serviceType = publication.Kind switch
        {
            PublicationKind.Feature => "FeatureServer",
            PublicationKind.Map => "MapServer",
            PublicationKind.Image => "ImageServer",
            _ => null,
        };

        return serviceType is null
            ? null
            : $"{Root(host, geoServicesRoot)}/{Uri.EscapeDataString(publication.Name)}/{serviceType}";
    }
}
