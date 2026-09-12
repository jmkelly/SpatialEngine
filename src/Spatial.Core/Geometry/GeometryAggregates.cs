
namespace Spatial.Core.Geometry;

/// <summary>
/// Shared structural aggregation rules for composite geometries: merged
/// layout ("most expressive wins"), envelope union and coordinate sums.
/// </summary>
internal static class GeometryAggregates
{
    public static CoordinateLayout MergeLayouts(IEnumerable<IGeometry> parts)
    {
        var hasZ = false;
        var hasM = false;
        foreach (var part in parts)
        {
            hasZ |= part.Layout.HasZ();
            hasM |= part.Layout.HasM();
        }

        return CoordinateLayoutExtensions.FromOrdinates(hasZ, hasM);
    }

    public static Envelope? MergeEnvelopes(IEnumerable<IGeometry> parts)
    {
        Envelope? result = null;
        foreach (var part in parts)
        {
            if (part.Envelope is { } envelope)
            {
                result = result is { } current ? current.Union(envelope) : envelope;
            }
        }

        return result;
    }

    public static int SumCoordinateCounts(IEnumerable<IGeometry> parts)
    {
        var count = 0;
        foreach (var part in parts)
        {
            count += part.CoordinateCount;
        }

        return count;
    }

    public static bool AllEmpty(IEnumerable<IGeometry> parts)
    {
        foreach (var part in parts)
        {
            if (!part.IsEmpty)
            {
                return false;
            }
        }

        return true;
    }
}
