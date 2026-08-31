
namespace Spatial.Core.Geometry;

/// <summary>
/// Structural equality and hashing for <see cref="IGeometry"/> values.
/// Equality is exact: type, CRS, layout and every coordinate, in order.
/// </summary>
public static class GeometryComparer
{
    public static bool Equals(IGeometry? left, IGeometry? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null || left.Type != right.Type)
        {
            return false;
        }

        return EqualsByType(left, right);
    }

    private static bool EqualsByType(IGeometry left, IGeometry right) => left switch
    {
        // Type equality is already established by the caller, so the casts
        // cannot fail; a foreign implementation claiming a core type casts to
        // null and compares unequal instead of throwing.
        Point point => point.Equals(right as Point),
        LineString lineString => lineString.Equals(right as LineString),
        Polygon polygon => polygon.Equals(right as Polygon),
        MultiPoint multiPoint => multiPoint.Equals(right as MultiPoint),
        MultiLineString multiLineString => multiLineString.Equals(right as MultiLineString),
        MultiPolygon multiPolygon => multiPolygon.Equals(right as MultiPolygon),
        GeometryCollection collection => collection.Equals(right as GeometryCollection),
        _ => false,
    };

    public static int GetHashCode(IGeometry geometry) => geometry switch
    {
        Point point => point.GetHashCode(),
        LineString lineString => lineString.GetHashCode(),
        Polygon polygon => polygon.GetHashCode(),
        MultiPoint multiPoint => multiPoint.GetHashCode(),
        MultiLineString multiLineString => multiLineString.GetHashCode(),
        MultiPolygon multiPolygon => multiPolygon.GetHashCode(),
        GeometryCollection collection => collection.GetHashCode(),
        _ => throw new ArgumentException($"Unknown geometry type '{geometry.Type}'.", nameof(geometry)),
    };

    public static IEqualityComparer<IGeometry> Instance { get; } = new GeometryEqualityComparer();

    private sealed class GeometryEqualityComparer : IEqualityComparer<IGeometry>
    {
        public bool Equals(IGeometry? x, IGeometry? y) => GeometryComparer.Equals(x, y);

        public int GetHashCode(IGeometry obj) => GeometryComparer.GetHashCode(obj);
    }
}
