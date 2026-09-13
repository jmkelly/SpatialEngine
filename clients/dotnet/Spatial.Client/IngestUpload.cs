namespace Spatial.Client;

/// <summary>
/// The parameters of one neutral upload (ADR-0041): what file is being
/// uploaded, where it lands, its declared format and CRS, its identity mode
/// and the optional publication name to register in the same call.
/// </summary>
public sealed record IngestUpload(
    string FileName,
    string Dataset,
    int Srid,
    string Format = "geojson",
    string Store = "memory",
    string? Identity = null,
    string? IdentityField = null,
    string? Publish = null,
    int? SourceSrid = null);
