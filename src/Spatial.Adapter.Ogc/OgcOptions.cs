namespace Spatial.Adapter.Ogc;

/// <summary>
/// Host configuration for the OGC boundary adapter (ADR-0053 §3). The routes
/// are mounted under <see cref="Root"/>; <see cref="MaxFeatures"/> bounds a
/// single WFS <c>GetFeature</c> response and <see cref="ServiceTitle"/> is the
/// capabilities title shared by WMS and WFS.
/// </summary>
public sealed class OgcOptions
{
    /// <summary>The OGC route prefix (default <c>/ogc</c>).</summary>
    public string Root { get; set; } = "/ogc";

    /// <summary>The capabilities title for both services.</summary>
    public string ServiceTitle { get; set; } = "Spatial Engine";

    /// <summary>The largest number of features one WFS <c>GetFeature</c> may return.</summary>
    public int MaxFeatures { get; set; } = 10_000;

    /// <summary>
    /// The shared tile scheme used when an OGC tile request names no matrix
    /// set. The host's normal <c>Spatial:Tiles:DefaultScheme</c> is copied into
    /// this adapter option during composition.
    /// </summary>
    public string DefaultTileScheme { get; set; } = "webmercator";

    /// <summary>The OGC identifier for the Web Mercator matrix set.</summary>
    public string WebMercatorTileMatrixSet { get; set; } = "WebMercatorQuad";
}
