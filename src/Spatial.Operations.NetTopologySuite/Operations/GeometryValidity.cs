using Spatial.Core.Geometry;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// The OGC ring rules the validation contract reports without asking
/// NetTopologySuite, because an open or degenerate ring cannot even be
/// represented as an NTS LinearRing (its factory rejects it). A non-empty
/// ring must be closed (its first coordinate equals its last) and carry at
/// least four coordinates; an open or undersized ring makes the containing
/// geometry structurally invalid. Only these two rules are pre-checked — the
/// rest of the structural validity question (self-intersection, nested
/// holes, connectivity) is NTS's <c>IsValidOp</c>.
/// </summary>
internal static class GeometryValidity
{
    /// <summary>Whether every polygon ring of the geometry satisfies the OGC ring rules.</summary>
    public static bool RingRulesHold(IGeometry geometry)
    {
        foreach (var part in geometry.DepthFirst())
        {
            if (part is not LineString ring)
            {
                continue;
            }

            if (!RingIsValid(ring))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RingIsValid(LineString ring)
    {
        var sequence = ring.Sequence;
        if (sequence.Count == 0)
        {
            return true;
        }

        if (sequence.Count < 4)
        {
            return false;
        }

        var first = sequence.GetCoordinate(0);
        var last = sequence.GetCoordinate(sequence.Count - 1);
        return first == last;
    }
}