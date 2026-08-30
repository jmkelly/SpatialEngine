namespace Spatial.Core.Geometry;

/// <summary>Structural traversal over geometry values.</summary>
public static class GeometryTraversal
{
    /// <summary>
    /// Immediate parts: polygon rings and collection members. Points and line
    /// strings have none.
    /// </summary>
    public static IEnumerable<IGeometry> Parts(this IGeometry geometry) => geometry switch
    {
        Polygon polygon => PolygonParts(polygon),
        GeometryCollection collection => collection.Geometries,
        MultiPoint multiPoint => multiPoint.Points,
        MultiLineString multiLineString => multiLineString.LineStrings,
        MultiPolygon multiPolygon => multiPolygon.Polygons,
        _ => [],
    };

    /// <summary>Depth-first walk yielding the geometry followed by all of its descendants.</summary>
    public static IEnumerable<IGeometry> DepthFirst(this IGeometry geometry)
    {
        yield return geometry;
        foreach (var part in geometry.Parts())
        {
            foreach (var descendant in part.DepthFirst())
            {
                yield return descendant;
            }
        }
    }

    /// <summary>All leaf coordinates, in structural order.</summary>
    public static IEnumerable<Coordinate> Coordinates(this IGeometry geometry) => geometry switch
    {
        Point point => point.Coordinate is { } coordinate ? [coordinate] : [],
        LineString lineString => SequenceCoordinates(lineString),
        _ => geometry.Parts().SelectMany(part => part.Coordinates()),
    };

    private static IEnumerable<IGeometry> PolygonParts(Polygon polygon)
    {
        yield return polygon.ExteriorRing;
        foreach (var ring in polygon.InteriorRings)
        {
            yield return ring;
        }
    }

    private static IEnumerable<Coordinate> SequenceCoordinates(LineString lineString)
    {
        var sequence = lineString.Sequence;
        for (var i = 0; i < sequence.Count; i++)
        {
            yield return sequence.GetCoordinate(i);
        }
    }
}
