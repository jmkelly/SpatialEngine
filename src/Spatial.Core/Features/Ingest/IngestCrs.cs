namespace Spatial.Core.Features.Ingest;

/// <summary>
/// What the decode did about the source CRS (ADR-0041 §4). The document's own
/// declaration is kept verbatim as <see cref="Declared"/> because "reprojected
/// from EPSG:27700" and "reprojected from a name we could not resolve" are
/// very different facts to an operator looking at a shifted dataset.
/// </summary>
/// <param name="SourceSrid">The CRS the source records were read in.</param>
/// <param name="Declared">The source's own CRS declaration, when the format carried one.</param>
/// <param name="TargetSrid">The CRS the emitted geometries carry.</param>
/// <param name="Reprojected">Whether any geometry was actually transformed.</param>
public sealed record IngestCrs(int SourceSrid, string? Declared, int TargetSrid, bool Reprojected)
{
    public override string ToString() => Reprojected
        ? $"{Declared ?? $"EPSG:{SourceSrid}"} → EPSG:{TargetSrid}"
        : $"EPSG:{SourceSrid}";
}
