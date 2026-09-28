namespace Spatial.Core.Features.Ingest;

/// <summary>
/// What one decode did, returned to the caller (ADR-0041 §4). The old
/// contract reported nothing: a dropped row and a loaded row looked identical
/// from outside, so "the upload succeeded" and "the upload silently lost
/// 4% of its rows" were the same event.
/// </summary>
/// <param name="RecordsRead">Source records the decoder read, including dropped ones.</param>
/// <param name="FeaturesEmitted">Features that reached a page.</param>
/// <param name="Skipped">The bounded prefix of dropped records, with typed reasons.</param>
/// <param name="SkippedCount">Every dropped record, whether recorded above or not.</param>
/// <param name="Inferred">The inferred attribute fields and the evidence for each.</param>
/// <param name="Schema">The final schema, geometry field included.</param>
/// <param name="Crs">What the decode did about the source CRS.</param>
/// <param name="Sampled">How many records the schema was inferred from.</param>
/// <param name="SchemaSampled">Whether inference stopped before the end of the document.</param>
public sealed record DecodeReport(
    long RecordsRead,
    long FeaturesEmitted,
    long SkippedCount,
    IReadOnlyList<IngestSkip> Skipped,
    IReadOnlyList<InferredField> Inferred,
    FeatureSchema Schema,
    IngestCrs Crs,
    long Sampled,
    bool SchemaSampled)
{
    /// <summary>
    /// The most dropped records a report lists. A 500 MB upload with a broken
    /// column would otherwise build a 500 MB report; the count stays exact and
    /// <see cref="SkipsTruncated"/> says the list is a prefix.
    /// </summary>
    public const int MaxRecordedSkips = 100;

    /// <summary>Whether <see cref="Skipped"/> is a prefix of the dropped records.</summary>
    public bool SkipsTruncated => SkippedCount > Skipped.Count;

    public override string ToString() =>
        $"{RecordsRead} record(s), {FeaturesEmitted} feature(s), {SkippedCount} skipped, CRS {Crs}";
}
