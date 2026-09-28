using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Tiling.Mvt;

/// <summary>
/// Projecting a tile's bounds into the CRS the features are stored in
/// (ADR-0043): the tile rectangle is a ring in the tile CRS, reprojected into
/// the data CRS to become the query envelope. A tile already in the data CRS
/// needs no transform, and an unaddressable CRS identity is rejected.
/// </summary>
internal static class MvtTileProjection
{
    /// <summary>
    /// A tile frame is addressable only when it has extent on both axes: the
    /// encoder divides by those extents, so a zero (or non-finite) one is not a
    /// thin tile but a frame the MVT coordinate space cannot address. A caller
    /// asking for one is asking for geometry that cannot be placed, so it is
    /// rejected rather than clamped onto a centre line it never asked for.
    /// </summary>
    public static bool HasExtent(Envelope bounds) => bounds.Width > 0 && bounds.Height > 0;

    /// <summary>The query bounds for a tile rectangle, in the source CRS.</summary>
    public static BoundingBox ToSourceBounds(
        ICoordinateTransforms transforms,
        Envelope bounds,
        string target,
        string source,
        CancellationToken cancellationToken)
    {
        if (string.Equals(target, source, StringComparison.OrdinalIgnoreCase))
        {
            return new BoundingBox(bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY);
        }

        var separator = target.IndexOf(':');
        if (separator <= 0 || separator == target.Length - 1)
        {
            throw SpatialException.BadArguments($"Vector tile CRS '{target}' is not an authority:code identity.");
        }

        var ring = GeometryFactory.CreateLineString(
            [
                new Coordinate(bounds.MinX, bounds.MinY), new Coordinate(bounds.MaxX, bounds.MinY),
                new Coordinate(bounds.MaxX, bounds.MaxY), new Coordinate(bounds.MinX, bounds.MaxY),
                new Coordinate(bounds.MinX, bounds.MinY)
            ],
            new CoordinateReference(target[..separator], target[(separator + 1)..]));
        var projected = transforms.Transform(ring, target, source, cancellationToken).Envelope ?? bounds;
        return new BoundingBox(projected.MinX, projected.MinY, projected.MaxX, projected.MaxY);
    }
}
