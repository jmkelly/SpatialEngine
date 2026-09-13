namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Host configuration for the GeoServices facade (ADR-0035):
/// <c>Spatial:GeoServices:Root</c> is the URL prefix, <c>Services</c> maps a
/// logical Esri service name onto an engine store. A logical service expands
/// to a <c>FeatureServer</c> whose layer ids map to that store's datasets.
/// </summary>
public sealed class GeoServicesOptions
{
    /// <summary>The Esri-conventional route prefix.</summary>
    public string Root { get; set; } = "/arcgis/rest/services";

    /// <summary>The logical services the facade serves.</summary>
    public IReadOnlyList<GeoServicesServiceOptions> Services { get; set; } = [];

    /// <summary>
    /// Whether the Image Service serves raw raster download (spec §8.0.7/§8.5).
    /// Off by default: download exposes provider-owned raster files verbatim,
    /// so a host opts in explicitly (the size cap below is always enforced).
    /// </summary>
    public bool AllowRasterDownload { get; set; }

    /// <summary>The maximum bytes one download or file response may expose.</summary>
    public long MaxRasterDownloadBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>The maximum files one download response may list.</summary>
    public int MaxRasterDownloadFiles { get; set; } = 1000;
}

/// <summary>One configured logical GeoServices service.</summary>
public sealed class GeoServicesServiceOptions
{
    /// <summary>The service name in the URL (for example <c>demo</c>).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The keyed engine store the service reads (for example <c>demo</c> or <c>postgis</c>).</summary>
    public string Store { get; set; } = string.Empty;

    /// <summary>The Esri service type; only <c>FeatureServer</c> is served in this phase.</summary>
    public string Type { get; set; } = "FeatureServer";
}
