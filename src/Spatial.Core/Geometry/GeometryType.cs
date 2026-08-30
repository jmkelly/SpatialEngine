namespace Spatial.Core.Geometry;

/// <summary>
/// Simple-feature geometry types. Numeric values match the classic WKB type
/// numbers for easy interchange.
/// </summary>
public enum GeometryType : byte
{
    Point = 1,
    LineString = 2,
    Polygon = 3,
    MultiPoint = 4,
    MultiLineString = 5,
    MultiPolygon = 6,
    GeometryCollection = 7,
}
