using Spatial.Core.Features;
using Spatial.Core.Features.Ingest;

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
/// Options for one decode (ADR-0041).
/// <para>
/// <see cref="Srid"/> is the CRS the decoded geometries carry. When the source
/// format declares a CRS of its own — GeoJSON's <c>crs</c> member, a CSV
/// <c># crs=</c> directive — that declaration is honoured rather than ignored:
/// the source CRS is the declared one, and a decode that would have to
/// transform without a <see cref="Reprojector"/> fails instead of silently
/// labelling the data with the caller's CRS.
/// </para>
/// </summary>
public sealed record DecodeOptions
{
    /// <summary>Maximum features per emitted page; must be positive.</summary>
    public int BatchSize { get; init; } = 10_000;

    /// <summary>
    /// The target CRS: the EPSG code stamped on decoded geometries, and the
    /// CRS a declared source CRS is transformed into.
    /// </summary>
    public int Srid { get; init; } = 4326;

    /// <summary>
    /// The CRS the caller asserts the source is in. When the document declares
    /// one too, the two must agree: an upload whose declared CRS contradicts
    /// the caller's is a mistake worth reporting, not picking a winner for.
    /// </summary>
    public int? SourceSrid { get; init; }

    /// <summary>
    /// The decode's route to a coordinate transform. Without one, a source CRS
    /// that differs from <see cref="Srid"/> is a typed failure rather than a
    /// silent mislabelling.
    /// </summary>
    public IIngestReprojection? Reprojector { get; init; }

    /// <summary>The schema field name of the geometry column.</summary>
    public string GeometryField { get; init; } = "geometry";

    /// <summary>CSV: the x/longitude column; auto-detected when null.</summary>
    public string? XField { get; init; }

    /// <summary>CSV: the y/latitude column; auto-detected when null.</summary>
    public string? YField { get; init; }

    /// <summary>The source field usable as the feature identity (Identity=Source).</summary>
    public string? IdentityField { get; init; }

    /// <summary>
    /// Whether a malformed record is dropped and reported rather than failing
    /// the whole decode. Off by default: a bad row in a one-million-row upload
    /// should not silently become a one-million-row upload with a hole in it.
    /// </summary>
    public bool SkipMalformed { get; init; }

    /// <summary>
    /// How many leading records a streaming decode infers its schema from.
    /// <c>null</c> uses <see cref="BatchSize"/>. A record outside the sample
    /// carrying an attribute the inferred schema has no field for is dropped
    /// and reported, never silently narrowed.
    /// </summary>
    public int? InferSampleSize { get; init; }
}

/// <summary>
/// The buffered decoded upload (ADR-0041): the inferred schema, the canonical
/// <see cref="FeatureBatch"/> pages built from it, the schema field that can
/// serve as the identity, and what the decode did. Every value is a core value
/// — the JSON/text of the source never leaves the codec.
/// </summary>
public sealed record DecodedDataset(
    FeatureSchema Schema,
    IReadOnlyList<FeatureBatch> Pages,
    string? IdentityField,
    DecodeReport Report)
{
    public override string ToString() => $"{Schema} ({Pages.Count} page(s))";
}
