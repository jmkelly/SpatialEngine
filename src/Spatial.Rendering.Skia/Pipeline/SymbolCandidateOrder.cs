using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Rendering.Skia.Pipeline;

/// <summary>
/// Orders symbol candidates deterministically: by feature identity then
/// source envelope centre, so page order from the store cannot change which
/// label wins a collision (ADR-0049). Extracted from
/// <see cref="SymbolSceneBuilder"/>.
/// </summary>
internal static class SymbolCandidateOrder
{
    public static int Compare((IFeature Feature, IGeometry Geometry) left, (IFeature Feature, IGeometry Geometry) right)
    {
        var byId = string.CompareOrdinal(left.Feature.Id.Value, right.Feature.Id.Value);
        if (byId != 0)
        {
            return byId;
        }

        var (leftX, leftY) = Centre(left.Geometry);
        var (rightX, rightY) = Centre(right.Geometry);
        var byX = leftX.CompareTo(rightX);
        return byX != 0 ? byX : leftY.CompareTo(rightY);
    }

    private static (double X, double Y) Centre(IGeometry geometry) =>
        geometry.Envelope is { } envelope
            ? ((envelope.MinX + envelope.MaxX) / 2, (envelope.MinY + envelope.MaxY) / 2)
            : (double.NaN, double.NaN);
}
