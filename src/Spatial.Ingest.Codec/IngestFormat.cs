using Spatial.Core.Features;

namespace Spatial.Ingest.Codec;

/// <summary>
/// The declared format of an upload body (ADR-0041). The host and the Esri
/// admin projection map their wire format name onto this enum; the codec
/// never sees a protocol concept.
/// </summary>
public enum IngestFormat
{
    /// <summary>An RFC 7946 <c>FeatureCollection</c>, or a single <c>Feature</c>.</summary>
    GeoJson,

    /// <summary>One GeoJSON <c>Feature</c> per line, blank lines ignored.</summary>
    NewlineDelimitedGeoJson,

    /// <summary>Comma-separated values with a header row and x/y (or lon/lat) columns.</summary>
    Csv,
}

/// <summary>
/// Options for one decode (ADR-0041). <see cref="Srid"/> is stamped on every
/// decoded geometry; the source format's own CRS is not consulted because the
/// engine's CRS identity is explicit and curated (ADR-0009).
/// </summary>
public sealed record DecodeOptions
{
    /// <summary>Maximum features per emitted page; must be positive.</summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>The EPSG code stamped on decoded geometries.</summary>
    public int Srid { get; init; } = 4326;

    /// <summary>The schema field name of the geometry column.</summary>
    public string GeometryField { get; init; } = "geometry";

    /// <summary>CSV: the x/longitude column; auto-detected when null.</summary>
    public string? XField { get; init; }

    /// <summary>CSV: the y/latitude column; auto-detected when null.</summary>
    public string? YField { get; init; }

    /// <summary>The source field usable as the feature identity (Identity=Source).</summary>
    public string? IdentityField { get; init; }
}

/// <summary>
/// The decoded upload (ADR-0041): the inferred schema, the canonical
/// <see cref="FeatureBatch"/> pages built from it, and the schema field that
/// can serve as the identity when the source supplied one. Every value is a
/// core value — the JSON/text of the source never leaves the codec.
/// </summary>
public sealed record DecodedDataset(FeatureSchema Schema, IReadOnlyList<FeatureBatch> Pages, string? IdentityField)
{
    public override string ToString() => $"{Schema} ({Pages.Count} page(s))";
}
