namespace Spatial.Core.Geometry;

/// <summary>
/// Builders for immutable geometry values. Constructors are structural (they
/// store what they are given); the factory normalises CRS across parts: a
/// composite carries at most one distinct non-null CRS, parts without a CRS
/// are treated as unspecified, and conflicting part CRSs are rejected.
/// </summary>
public static class GeometryFactory
{
    public static Point CreatePoint(Coordinate coordinate, CoordinateReference? coordinateReference = null) =>
        new(coordinate, coordinateReference);

    public static Point CreatePoint(double x, double y, CoordinateReference? coordinateReference = null) =>
        new(new Coordinate(x, y), coordinateReference);

    public static Point CreatePoint(double x, double y, double z, CoordinateReference? coordinateReference = null) =>
        new(new Coordinate(x, y, z), coordinateReference);

    public static Point CreatePoint(double x, double y, double z, double m, CoordinateReference? coordinateReference = null) =>
        new(new Coordinate(x, y, z, m), coordinateReference);

    /// <summary>An empty point. <paramref name="layout"/> only matters for round-trip fidelity.</summary>
    public static Point CreateEmptyPoint(CoordinateReference? coordinateReference = null, CoordinateLayout layout = CoordinateLayout.Xy) =>
        new(null, coordinateReference, layout);

    /// <summary>Line string from coordinates; the layout is inferred from the ordinates present.</summary>
    public static LineString CreateLineString(ReadOnlySpan<Coordinate> coordinates, CoordinateReference? coordinateReference = null) =>
        new(PackedCoordinateSequence.FromCoordinates(coordinates), coordinateReference);

    /// <summary>Line string from coordinates packed into an explicit layout (empty spans may carry a layout).</summary>
    public static LineString CreateLineString(ReadOnlySpan<Coordinate> coordinates, CoordinateLayout layout, CoordinateReference? coordinateReference = null) =>
        new(PackedCoordinateSequence.FromCoordinates(coordinates, layout), coordinateReference);

    public static LineString CreateLineString(ICoordinateSequence sequence, CoordinateReference? coordinateReference = null) =>
        new(sequence, coordinateReference);

    public static LineString CreateEmptyLineString(CoordinateLayout layout = CoordinateLayout.Xy, CoordinateReference? coordinateReference = null) =>
        new(PackedCoordinateSequence.FromCoordinates([], layout), coordinateReference);

    public static Polygon CreatePolygon(LineString exteriorRing, IEnumerable<LineString>? interiorRings = null, CoordinateReference? coordinateReference = null)
    {
        ArgumentNullException.ThrowIfNull(exteriorRing);

        var rings = interiorRings?.ToArray() ?? [];
        var crs = ResolveCrs(coordinateReference, rings.Prepend(exteriorRing));
        return new Polygon(exteriorRing, rings, crs);
    }

    /// <summary>Polygon from a single exterior ring.</summary>
    public static Polygon CreatePolygon(ReadOnlySpan<Coordinate> exteriorRing, CoordinateReference? coordinateReference = null) =>
        CreatePolygon(CreateLineString(exteriorRing, coordinateReference), coordinateReference: coordinateReference);

    /// <summary>Polygon whose rings are built from coordinate sequences.</summary>
    public static Polygon CreatePolygon(ICoordinateSequence exteriorRing, IEnumerable<ICoordinateSequence>? interiorRings = null, CoordinateReference? coordinateReference = null)
    {
        ArgumentNullException.ThrowIfNull(exteriorRing);

        var rings = interiorRings?.Select(ring => new LineString(ring)).ToArray() ?? [];
        var crs = ResolveCrs(coordinateReference, rings.Prepend(new LineString(exteriorRing)));
        return new Polygon(new LineString(exteriorRing), rings, crs);
    }

    public static MultiPoint CreateMultiPoint(params Point[] points) =>
        CreateMultiPoint((IEnumerable<Point>)points, null);

    public static MultiPoint CreateMultiPoint(IEnumerable<Point> points, CoordinateReference? coordinateReference)
    {
        ArgumentNullException.ThrowIfNull(points);
        return new MultiPoint(points, ResolveCrs(coordinateReference, points));
    }

    public static MultiLineString CreateMultiLineString(params LineString[] lineStrings) =>
        CreateMultiLineString((IEnumerable<LineString>)lineStrings, null);

    public static MultiLineString CreateMultiLineString(IEnumerable<LineString> lineStrings, CoordinateReference? coordinateReference)
    {
        ArgumentNullException.ThrowIfNull(lineStrings);
        return new MultiLineString(lineStrings, ResolveCrs(coordinateReference, lineStrings));
    }

    public static MultiPolygon CreateMultiPolygon(params Polygon[] polygons) =>
        CreateMultiPolygon((IEnumerable<Polygon>)polygons, null);

    public static MultiPolygon CreateMultiPolygon(IEnumerable<Polygon> polygons, CoordinateReference? coordinateReference)
    {
        ArgumentNullException.ThrowIfNull(polygons);
        return new MultiPolygon(polygons, ResolveCrs(coordinateReference, polygons));
    }

    public static GeometryCollection CreateGeometryCollection(params IGeometry[] geometries) =>
        CreateGeometryCollection((IEnumerable<IGeometry>)geometries, null);

    public static GeometryCollection CreateGeometryCollection(IEnumerable<IGeometry> geometries, CoordinateReference? coordinateReference)
    {
        ArgumentNullException.ThrowIfNull(geometries);
        return new GeometryCollection(geometries, ResolveCrs(coordinateReference, geometries));
    }

    /// <summary>
    /// The single distinct non-null CRS among <paramref name="explicitCrs"/>
    /// and the parts, or <c>null</c> when all are unspecified. Conflicting CRSs
    /// are rejected — composites cannot represent them.
    /// </summary>
    internal static CoordinateReference? ResolveCrs(CoordinateReference? explicitCrs, IEnumerable<IGeometry> parts)
    {
        CoordinateReference? resolved = null;
        if (explicitCrs is { } crs)
        {
            resolved = crs;
        }

        foreach (var part in parts)
        {
            if (part.CoordinateReference is not { } partCrs)
            {
                continue;
            }

            if (resolved is null)
            {
                resolved = partCrs;
                continue;
            }

            if (resolved != partCrs)
            {
                throw new ArgumentException(
                    FormattableString.Invariant($"Parts declare conflicting coordinate references {resolved} and {partCrs}; a composite geometry carries at most one distinct non-null CRS (parts without a CRS are unspecified)."));
            }
        }

        return resolved;
    }
}
