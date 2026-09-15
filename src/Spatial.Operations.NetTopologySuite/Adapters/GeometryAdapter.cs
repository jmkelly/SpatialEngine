using System.Runtime.InteropServices;
using Spatial.Core.Geometry;
using NtsFactory = NetTopologySuite.Geometries.GeometryFactory;
using NtsGeometry = NetTopologySuite.Geometries.Geometry;
using NtsGeometryCollection = NetTopologySuite.Geometries.GeometryCollection;
using NtsLinearRing = NetTopologySuite.Geometries.LinearRing;
using NtsLineString = NetTopologySuite.Geometries.LineString;
using NtsMultiLineString = NetTopologySuite.Geometries.MultiLineString;
using NtsMultiPoint = NetTopologySuite.Geometries.MultiPoint;
using NtsMultiPolygon = NetTopologySuite.Geometries.MultiPolygon;
using NtsOgcType = NetTopologySuite.Geometries.OgcGeometryType;
using NtsOrdinate = NetTopologySuite.Geometries.Ordinate;
using NtsPoint = NetTopologySuite.Geometries.Point;
using NtsPolygon = NetTopologySuite.Geometries.Polygon;
using NtsSequence = NetTopologySuite.Geometries.CoordinateSequence;
using NtsSequenceFactory = NetTopologySuite.Geometries.Implementation.CoordinateArraySequenceFactory;

namespace Spatial.Operations.NetTopologySuite.Adapters;

/// <summary>
/// The private core ↔ NetTopologySuite adapter (ADR-0005: third-party geometry
/// types never cross a public boundary). Converts every core geometry shape to
/// an NTS geometry for the planar algorithm, and every NTS result shape back
/// to an immutable core geometry carrying the operation's CRS. Z and M
/// ordinates are carried across when the source layout has them (the planar
/// NTS algorithms compute XY results, so buffer and intersection results are
/// XY; simplify and the adapter round trips preserve the input ordinates).
/// Ring closure is normalised to NTS's requirement: an open ring is closed by
/// appending its first coordinate (the core model does not require closed
/// rings; NTS LinearRings do).
/// </summary>
internal static class GeometryAdapter
{
    private static readonly NtsFactory Factory = new();

    private static readonly Dictionary<GeometryType, Func<IGeometry, NtsGeometry>> NtsConverters = new()
    {
        [GeometryType.Point] = g => PointToNts((Point)g),
        [GeometryType.LineString] = g => LineStringToNts((LineString)g),
        [GeometryType.Polygon] = g => PolygonToNts((Polygon)g),
        [GeometryType.MultiPoint] = g => MultiPointToNts((MultiPoint)g),
        [GeometryType.MultiLineString] = g => MultiLineStringToNts((MultiLineString)g),
        [GeometryType.MultiPolygon] = g => MultiPolygonToNts((MultiPolygon)g),
        [GeometryType.GeometryCollection] = g => GeometryCollectionToNts((GeometryCollection)g),
    };

    /// <summary>Converts a core geometry to the NTS geometry its operations run on.</summary>
    public static NtsGeometry ToNts(IGeometry geometry)
    {
        if (NtsConverters.TryGetValue(geometry.Type, out var convert))
        {
            return convert(geometry);
        }

        throw new ArgumentException($"Cannot convert core geometry type '{geometry.Type}' to NetTopologySuite.", nameof(geometry));
    }

    private static readonly Dictionary<NtsOgcType, Func<NtsGeometry, CoordinateReference?, IGeometry>> CoreConverters = new()
    {
        [NtsOgcType.Point] = (g, crs) => PointToCore((NtsPoint)g, crs),
        [NtsOgcType.LineString] = (g, crs) => LineStringToCore((NtsLineString)g, crs),
        [NtsOgcType.Polygon] = (g, crs) => PolygonToCore((NtsPolygon)g, crs),
        [NtsOgcType.MultiPoint] = (g, crs) => MultiPointToCore((NtsMultiPoint)g, crs),
        [NtsOgcType.MultiLineString] = (g, crs) => MultiLineStringToCore((NtsMultiLineString)g, crs),
        [NtsOgcType.MultiPolygon] = (g, crs) => MultiPolygonToCore((NtsMultiPolygon)g, crs),
        [NtsOgcType.GeometryCollection] = (g, crs) => GeometryCollectionToCore((NtsGeometryCollection)g, crs),
    };

    /// <summary>Converts an NTS result back to an immutable core geometry carrying <paramref name="crs"/>.</summary>
    public static IGeometry ToCore(NtsGeometry geometry, CoordinateReference? crs)
    {
        if (CoreConverters.TryGetValue(geometry.OgcGeometryType, out var convert))
        {
            return convert(geometry, crs);
        }

        throw new ArgumentException($"Cannot convert NetTopologySuite result type '{geometry.GetType().Name}' back to core geometry.", nameof(geometry));
    }

    private static NtsPoint PointToNts(Point point)
    {
        if (point.IsEmpty)
        {
            return Factory.CreatePoint();
        }

        return Factory.CreatePoint(SequenceFor(point, point.Layout));
    }

    /// <summary>A single-coordinate sequence for a non-empty point.</summary>
    private static NtsSequence SequenceFor(Point point, CoordinateLayout layout)
    {
        var sequence = CoordinateArraySequence(layout.HasZ(), layout.HasM(), 1);
        var coordinate = point.Coordinate!.Value;
        sequence.SetOrdinate(0, NtsOrdinate.X, coordinate.X);
        sequence.SetOrdinate(0, NtsOrdinate.Y, coordinate.Y);
        if (layout.HasZ())
        {
            sequence.SetOrdinate(0, NtsOrdinate.Z, coordinate.Z ?? double.NaN);
        }

        if (layout.HasM())
        {
            sequence.SetOrdinate(0, NtsOrdinate.M, coordinate.M ?? double.NaN);
        }

        return sequence;
    }

    private static NtsLineString LineStringToNts(LineString lineString) =>
        Factory.CreateLineString(SequenceFor(lineString.Sequence, lineString.Layout));

    private static NtsPolygon PolygonToNts(Polygon polygon)
    {
        var shell = RingToNts(polygon.ExteriorRing);
        var holes = polygon.InteriorRings.Select(RingToNts).ToArray();
        return Factory.CreatePolygon(shell, holes);
    }

    private static NtsMultiPoint MultiPointToNts(MultiPoint multiPoint) =>
        Factory.CreateMultiPoint(multiPoint.Points.Select(PointToNts).ToArray());

    private static NtsMultiLineString MultiLineStringToNts(MultiLineString multiLineString) =>
        Factory.CreateMultiLineString(
            multiLineString.LineStrings.Select(line => Factory.CreateLineString(SequenceFor(line.Sequence, line.Layout))).ToArray());

    private static NtsMultiPolygon MultiPolygonToNts(MultiPolygon multiPolygon) =>
        Factory.CreateMultiPolygon(multiPolygon.Polygons.Select(PolygonToNts).ToArray());

    private static NtsGeometryCollection GeometryCollectionToNts(GeometryCollection collection) =>
        Factory.CreateGeometryCollection(collection.Geometries.Select(ToNts).ToArray());

    private static NtsLinearRing RingToNts(LineString ring)
    {
        var sequence = SequenceFor(ring.Sequence, ring.Layout, closeRing: true);
        return Factory.CreateLinearRing(sequence);
    }

    /// <summary>
    /// Copies a core sequence into an NTS sequence. When <paramref name="closeRing"/>
    /// is set and the ring is open, the first coordinate is appended as the closing
    /// coordinate (NTS requires closed LinearRings).
    /// </summary>
    private static NtsSequence SequenceFor(ICoordinateSequence source, CoordinateLayout layout, bool closeRing = false)
    {
        var hasZ = layout.HasZ();
        var hasM = layout.HasM();
        var count = source.Count;
        var needsClose = closeRing && count > 0 && !IsClosed(source);
        var size = count + (needsClose ? 1 : 0);
        var sequence = CoordinateArraySequence(hasZ, hasM, size);
        for (var i = 0; i < count; i++)
        {
            WriteCoordinate(sequence, source.GetCoordinate(i), i, hasZ, hasM);
        }

        if (needsClose)
        {
            CopyCoordinate(sequence, source.GetCoordinate(0), count, hasZ, hasM);
        }

        return sequence;
    }

    private static NtsSequence CoordinateArraySequence(bool hasZ, bool hasM, int size)
    {
        var dimension = 2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0);
        return NtsSequenceFactory.Instance.Create(size, dimension, hasM ? 1 : 0);
    }

    private static void WriteCoordinate(NtsSequence sequence, Coordinate coordinate, int index, bool hasZ, bool hasM)
    {
        sequence.SetOrdinate(index, NtsOrdinate.X, coordinate.X);
        sequence.SetOrdinate(index, NtsOrdinate.Y, coordinate.Y);
        if (hasZ)
        {
            sequence.SetOrdinate(index, NtsOrdinate.Z, coordinate.Z ?? double.NaN);
        }

        if (hasM)
        {
            sequence.SetOrdinate(index, NtsOrdinate.M, coordinate.M ?? double.NaN);
        }
    }

    private static void CopyCoordinate(NtsSequence sequence, Coordinate coordinate, int index, bool hasZ, bool hasM) =>
        WriteCoordinate(sequence, coordinate, index, hasZ, hasM);

    private static bool IsClosed(ICoordinateSequence sequence)
    {
        var first = sequence.GetCoordinate(0);
        var last = sequence.GetCoordinate(sequence.Count - 1);
        return first == last;
    }

    private static Point PointToCore(NtsPoint point, CoordinateReference? crs)
    {
        if (point.IsEmpty)
        {
            return GeometryFactory.CreateEmptyPoint(crs);
        }

        var sequence = point.CoordinateSequence;
        var coordinate = ReadCoordinate(sequence, 0);
        return GeometryFactory.CreatePoint(coordinate, crs);
    }

    private static LineString LineStringToCore(NtsLineString lineString, CoordinateReference? crs)
    {
        var coordinates = ReadCoordinates(lineString.CoordinateSequence);
        return GeometryFactory.CreateLineString(
            AsSpan(coordinates),
            LayoutFor(lineString.CoordinateSequence),
            crs);
    }

    private static Polygon PolygonToCore(NtsPolygon polygon, CoordinateReference? crs) =>
        GeometryFactory.CreatePolygon(
            LineStringToCore(polygon.Shell, crs: null),
            polygon.Holes.Select(hole => LineStringToCore(hole, crs: null)),
            crs);

    private static MultiPoint MultiPointToCore(NtsMultiPoint multiPoint, CoordinateReference? crs) =>
        GeometryFactory.CreateMultiPoint(
            multiPoint.Geometries.Cast<NtsPoint>().Select(point => PointToCore(point, crs: null)),
            crs);

    private static MultiLineString MultiLineStringToCore(NtsMultiLineString multiLineString, CoordinateReference? crs) =>
        GeometryFactory.CreateMultiLineString(
            multiLineString.Geometries.Cast<NtsLineString>().Select(lineString => LineStringToCore(lineString, crs: null)),
            crs);

    private static MultiPolygon MultiPolygonToCore(NtsMultiPolygon multiPolygon, CoordinateReference? crs) =>
        GeometryFactory.CreateMultiPolygon(
            multiPolygon.Geometries.Cast<NtsPolygon>().Select(polygon => PolygonToCore(polygon, crs: null)),
            crs);

    private static GeometryCollection GeometryCollectionToCore(NtsGeometryCollection collection, CoordinateReference? crs) =>
        GeometryFactory.CreateGeometryCollection(
            collection.Geometries.Select(geometry => ToCore(geometry, crs: null)),
            crs);

    /// <summary>The layout that can represent every ordinate the sequence stores.</summary>
    private static readonly Dictionary<(bool HasZ, bool HasM), CoordinateLayout> LayoutByOrdinates = new()
    {
        [(true, true)] = CoordinateLayout.Xyzm,
        [(true, false)] = CoordinateLayout.Xyz,
        [(false, true)] = CoordinateLayout.Xym,
        [(false, false)] = CoordinateLayout.Xy,
    };

    private static CoordinateLayout LayoutFor(NtsSequence sequence) =>
        LayoutByOrdinates[(sequence.HasZ, sequence.HasM)];

    private static Span<Coordinate> AsSpan(List<Coordinate> coordinates) =>
        CollectionsMarshal.AsSpan(coordinates);

    private static List<Coordinate> ReadCoordinates(NtsSequence sequence)
    {
        var coordinates = new List<Coordinate>(sequence.Count);
        for (var i = 0; i < sequence.Count; i++)
        {
            coordinates.Add(ReadCoordinate(sequence, i));
        }

        return coordinates;
    }

    private static Coordinate ReadCoordinate(NtsSequence sequence, int index)
    {
        var x = sequence.GetOrdinate(index, NtsOrdinate.X);
        var y = sequence.GetOrdinate(index, NtsOrdinate.Y);
        var z = sequence.HasZ ? Extract(sequence.GetOrdinate(index, NtsOrdinate.Z)) : null;
        var m = sequence.HasM ? Extract(sequence.GetOrdinate(index, NtsOrdinate.M)) : null;
        return new Coordinate(x, y, z, m);
    }

    private static double? Extract(double value) => double.IsNaN(value) ? null : value;
}
