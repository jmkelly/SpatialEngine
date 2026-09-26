using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The <c>geometryPrecision</c> half of the query response projection
/// (spec §9.1.4, ADR-0040): rounds every ordinate of a geometry to a fixed
/// number of decimal places, preserving the geometry kind, ring order and
/// coordinate reference. Split out of <see cref="FeatureProjection"/> so the
/// reprojection path and the rounding path can change independently.
/// </summary>
internal static class GeometryRounding
{
    /// <summary>Rounds <paramref name="geometry"/> to <paramref name="precision"/> decimal places.</summary>
    internal static IGeometry Round(IGeometry geometry, int precision)
    {
        var single = RoundSingle(geometry, precision);
        if (single is not null)
        {
            return single;
        }

        return RoundMulti(geometry, precision) ?? geometry;
    }

    private static IGeometry? RoundSingle(IGeometry geometry, int precision)
    {
        var crs = geometry.CoordinateReference;
        return geometry switch
        {
            Point point when point.Coordinate is { } coordinate =>
                GeometryFactory.CreatePoint(RoundCoordinate(coordinate, precision), crs),
            LineString line => RoundLine(line, crs, precision),
            Polygon polygon => RoundPolygon(polygon, crs, precision),
            _ => null,
        };
    }

    private static IGeometry? RoundMulti(IGeometry geometry, int precision)
    {
        var crs = geometry.CoordinateReference;
        return geometry switch
        {
            MultiPoint multiPoint =>
                GeometryFactory.CreateMultiPoint(multiPoint.Points.Select(point => GeometryFactory.CreatePoint(RoundCoordinate(point.Coordinate ?? new Coordinate(0, 0), precision), crs)), crs),
            MultiLineString multiLine =>
                GeometryFactory.CreateMultiLineString(multiLine.LineStrings.Select(line => RoundLine(line, null, precision)), crs),
            MultiPolygon multiPolygon =>
                GeometryFactory.CreateMultiPolygon(multiPolygon.Polygons.Select(polygon => RoundPolygon(polygon, null, precision)), crs),
            GeometryCollection collection =>
                GeometryFactory.CreateGeometryCollection(collection.Geometries.Select(member => Round(member, precision)), crs),
            _ => null,
        };
    }

    private static LineString RoundLine(LineString line, CoordinateReference? crs, int precision)
    {
        var sequence = line.Sequence;
        var rounded = new Coordinate[sequence.Count];
        for (var i = 0; i < rounded.Length; i++)
        {
            rounded[i] = RoundCoordinate(sequence.GetCoordinate(i), precision);
        }

        return GeometryFactory.CreateLineString(rounded, sequence.Layout, crs ?? line.CoordinateReference);
    }

    private static Polygon RoundPolygon(Polygon polygon, CoordinateReference? crs, int precision)
    {
        var exterior = RoundLine(polygon.ExteriorRing, null, precision);
        var holes = polygon.InteriorRings.Select(ring => RoundLine(ring, null, precision));
        return GeometryFactory.CreatePolygon(exterior, holes, crs ?? polygon.CoordinateReference);
    }

    private static Coordinate RoundCoordinate(Coordinate coordinate, int precision) => new(
        Math.Round(coordinate.X, precision),
        Math.Round(coordinate.Y, precision),
        RoundOrdinate(coordinate.Z, precision),
        RoundOrdinate(coordinate.M, precision));

    private static double? RoundOrdinate(double? value, int precision) =>
        value is null || double.IsNaN(value.Value) ? value : Math.Round(value.Value, precision);
}
