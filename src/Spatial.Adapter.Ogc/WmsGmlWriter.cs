using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The GML geometry encoding for WMS GetFeatureInfo: geometry values to
/// <c>ogc:FeatureCollection</c> member shapes. Split out of
/// <see cref="WmsService"/> so the request facade keeps only dispatch and
/// the geometry-encoding fan-out lives with the code that uses it
/// (ADR-0040).
/// </summary>
internal static class WmsGmlWriter
{
    internal static XElement WriteGmlGeometry(IGeometry geometry) => geometry switch
    {
        Point point => new XElement(
            OgcXml.Gml + "Point",
            point.Coordinate is { } coordinate
                ? new XElement(OgcXml.Gml + "pos", Doubles(coordinate.X, coordinate.Y))
                : null),
        MultiPoint multi => new XElement(
            OgcXml.Gml + "MultiPoint",
            multi.Points.Select(member => new XElement(OgcXml.Gml + "pointMember", WriteGmlGeometry(member)))),
        LineString line => new XElement(OgcXml.Gml + "LineString", new XElement(OgcXml.Gml + "posList", Positions(line.Sequence))),
        MultiLineString multi => new XElement(
            OgcXml.Gml + "MultiCurve",
            multi.LineStrings.Select(member => new XElement(OgcXml.Gml + "curveMember", WriteGmlGeometry(member)))),
        Polygon polygon => WriteGmlPolygon(polygon),
        MultiPolygon multi => new XElement(
            OgcXml.Gml + "MultiSurface",
            multi.Polygons.Select(member => new XElement(OgcXml.Gml + "surfaceMember", WriteGmlGeometry(member)))),
        GeometryCollection collection => new XElement(
            OgcXml.Gml + "MultiGeometry",
            collection.Geometries.Select(member => new XElement(OgcXml.Gml + "geometryMember", WriteGmlGeometry(member)))),
        _ => new XElement(OgcXml.Gml + "Point"),
    };

    private static XElement WriteGmlPolygon(Polygon polygon)
    {
        var current = new XElement(
            OgcXml.Gml + "Polygon",
            new XElement(
                OgcXml.Gml + "exterior",
                new XElement(OgcXml.Gml + "LinearRing", new XElement(OgcXml.Gml + "posList", Positions(polygon.ExteriorRing.Sequence)))));
        foreach (var ring in polygon.InteriorRings)
        {
            current.Add(new XElement(
                OgcXml.Gml + "interior",
                new XElement(OgcXml.Gml + "LinearRing", new XElement(OgcXml.Gml + "posList", Positions(ring.Sequence)))));
        }

        return current;
    }

    private static string Doubles(double x, double y) =>
        $"{x.ToString("R", CultureInfo.InvariantCulture)} {y.ToString("R", CultureInfo.InvariantCulture)}";

    private static string Positions(ICoordinateSequence sequence)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < sequence.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(' ');
            }

            builder.Append(sequence.GetOrdinate(index, Ordinate.X).ToString("R", CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(sequence.GetOrdinate(index, Ordinate.Y).ToString("R", CultureInfo.InvariantCulture));
        }

        return builder.ToString();
    }
}
