using Spatial.Core.Features.Ingest;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec;

/// <summary>
/// One parsed source record before a schema exists: its optional source
/// identity, its attribute values keyed by source name, and its geometry
/// (already converted to a core value and stamped with the decode's source
/// CRS).
/// </summary>
internal sealed record RawFeature(string? Id, IReadOnlyDictionary<string, object?> Properties, IGeometry? Geometry);

/// <summary>
/// One source record or the typed reason it was dropped. Carrying the skip in
/// the same shape as the record is what makes "malformed features are
/// dropped" observable instead of a comment: the decoder either throws on the
/// skip (the default) or counts it into the report.
/// </summary>
internal readonly record struct RawRecord(int Position, RawFeature? Feature, IngestSkip? Skip)
{
    public static RawRecord Parsed(int position, RawFeature feature) => new(position, feature, null);

    public static RawRecord Dropped(int position, IngestSkipReason reason, string detail) =>
        new(position, null, new IngestSkip(position, reason, detail));
}

/// <summary>
/// A pull-based source reader: it yields records one at a time so a large
/// upload never has to be in memory all at once, and it declares the document
/// CRS in its preamble so the decode can resolve the source CRS *before* the
/// first geometry is stamped.
/// </summary>
internal interface IRawRecordReader : IDisposable
{
    /// <summary>
    /// Consumes the document preamble (the GeoJSON root's <c>crs</c> member,
    /// a CSV directive preamble, an ND-GeoJSON first record) and returns the
    /// CRS the document declares, or <c>null</c> when it declares none.
    /// </summary>
    ValueTask<string?> InitialiseAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The next source record, or <c>null</c> at end of document. Geometries
    /// are stamped with <paramref name="sourceSrid"/>, which the caller has
    /// resolved from the preamble and any asserted source CRS.
    /// </summary>
    ValueTask<RawRecord?> ReadAsync(int sourceSrid, CancellationToken cancellationToken);
}
