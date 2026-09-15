namespace Spatial.Stores.ArcGisRest;

/// <summary>
/// Host configuration for the ArcGIS REST provider (ADR-0035):
/// <c>Spatial:ArcGisRest:Services</c> maps a keyed store name onto a remote
/// service URL; <c>Token</c> is the optional ArcGIS token. The token is host
/// configuration only — never read from a request body (ADR-0035 §3).
/// </summary>
public sealed class ArcGisRestOptions
{
    /// <summary>The configured remote services.</summary>
    public IReadOnlyList<ArcGisRestServiceOptions> Services { get; set; } = [];

    /// <summary>The optional ArcGIS token appended to outbound requests.</summary>
    public string Token { get; set; } = string.Empty;
}

/// <summary>One configured remote ArcGIS REST service.</summary>
public sealed class ArcGisRestServiceOptions
{
    /// <summary>The keyed store name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The FeatureServer or MapServer base URL.</summary>
    public string Url { get; set; } = string.Empty;
}
