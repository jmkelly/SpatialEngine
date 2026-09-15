namespace Spatial.Contracts.Transformations;

/// <summary>
/// The structured result of the CRS description service
/// (ADR-0027/ADR-0033): a provider's description of one coordinate reference system
/// from its catalogue. Carries only core/framework types (ADR-0005) and
/// crosses the in-process contract surface as a <c>CrsDescription</c> DTO (like
/// <c>Geometry</c>, ADR-0020). The description is reader-oriented:
/// axis order, units and the datum/ellipsoid are what clients need to
/// interpret coordinates or display CRS metadata.
/// </summary>
public sealed record CrsDescription(
    string Authority,
    string Code,
    string Name,
    CrsKind Kind,
    int Dimension,
    IReadOnlyList<CrsAxis> Axes,
    string? Datum,
    CrsEllipsoid? Ellipsoid)
{
    /// <inheritdoc />
    public override string ToString() => $"{Authority}:{Code} {Name} ({Kind})";
}
