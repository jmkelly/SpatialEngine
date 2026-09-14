namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Configuration for the Esri admin projection (ADR-0041 §5/§6): mounted at
/// <c>Spatial:GeoServices:AdminRoot</c> (default <c>/arcgis/admin</c>) and
/// gated by <c>Spatial:Admin:Token</c>. With no token the routes answer an
/// actionable unavailable error rather than disappearing, so Esri tooling
/// gets a clear failure. Uploads are staged in memory with a TTL and the
/// configured byte and feature caps (mirroring the neutral ingest caps).
/// </summary>
public sealed class EsriAdminOptions
{
    /// <summary>The route prefix of the admin projection.</summary>
    public string Root { get; set; } = "/arcgis/admin";

    /// <summary>The admin token; empty disables the projection (an unavailable error is returned).</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Whether a token is configured.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(Token);

    /// <summary>The maximum upload size in bytes.</summary>
    public long MaxBytes { get; set; } = 104_857_600;

    /// <summary>The maximum number of decoded features (mirrors <c>Spatial:Ingest:MaxFeatures</c>).</summary>
    public int MaxFeatures { get; set; } = 1_000_000;

    /// <summary>The decoded page size.</summary>
    public int BatchSize { get; set; } = 10_000;

    /// <summary>How long a staged upload may be published before it is pruned.</summary>
    public TimeSpan UploadTtl { get; set; } = TimeSpan.FromMinutes(30);
}
