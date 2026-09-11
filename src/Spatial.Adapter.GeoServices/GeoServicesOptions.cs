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
