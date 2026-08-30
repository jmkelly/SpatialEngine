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

        return left switch
        {
            Point point => right is Point otherPoint && point.Equals(otherPoint),
            LineString lineString => right is LineString otherLine && lineString.Equals(otherLine),
            Polygon polygon => right is Polygon otherPolygon && polygon.Equals(otherPolygon),
            MultiPoint multiPoint => right is MultiPoint otherMultiPoint && multiPoint.Equals(otherMultiPoint),
            MultiLineString multiLineString => right is MultiLineString otherMultiLine && multiLineString.Equals(otherMultiLine),
            MultiPolygon multiPolygon => right is MultiPolygon otherMultiPolygon && multiPolygon.Equals(otherMultiPolygon),
            GeometryCollection collection => right is GeometryCollection otherCollection && collection.Equals(otherCollection),
            _ => false,
        };
    }

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
