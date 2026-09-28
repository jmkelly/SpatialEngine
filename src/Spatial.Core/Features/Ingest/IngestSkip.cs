namespace Spatial.Core.Features.Ingest;

/// <summary>
/// One source record the decode dropped, with its 1-based position in the
/// document, a typed <see cref="IngestSkipReason"/> and a human-readable
/// detail. A report records at most a bounded prefix of these
/// (<see cref="DecodeReport.MaxRecordedSkips"/>); the count is always exact,
/// so a truncated list is never mistaken for a complete one — see
/// <see cref="DecodeReport.SkipsTruncated"/>.
/// </summary>
public sealed record IngestSkip(int Record, IngestSkipReason Reason, string Detail)
{
    public override string ToString() => $"record {Record}: {Reason} ({Detail})";
}
