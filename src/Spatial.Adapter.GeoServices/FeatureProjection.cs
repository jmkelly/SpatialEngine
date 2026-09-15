using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Per-feature projection for query responses: <c>outSR</c> reprojection
/// and <c>geometryPrecision</c> rounding. Split out of
/// <see cref="FeatureQueryEngine"/> so the query facade keeps only
/// orchestration and the shaping fan-out lives with the code that uses
/// it (ADR-0040).
/// </summary>
internal static class FeatureProjection
{
    internal static FeatureQueryEngine.MatchedFeature TransformFeature(
        FeatureQueryEngine.MatchedFeature match,
        EsriFeatureQuery query,
        CoordinateReference? layerCrs,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        var feature = match.Feature;
        var geometryIndex = FeatureGeometry.Index(feature.Schema);
        if (geometryIndex < 0 || feature[geometryIndex].Kind != AttributeKind.Geometry)
        {
            return match;
        }

        var geometry = feature[geometryIndex].GeometryValue;
        if (query.OutSr is { } target && layerCrs is not null && target != layerCrs)
        {
            geometry = transforms.Transform(geometry, layerCrs.Value.ToString(), target.ToString(), cancellationToken);
        }

        if (query.GeometryPrecision is { } precision)
        {
            geometry = RoundGeometry(geometry, precision);
        }
        else if (ReferenceEquals(geometry, feature[geometryIndex].GeometryValue))
        {
            return match;
        }

        var attributes = feature.Attributes.ToArray();
        attributes[geometryIndex] = AttributeValue.FromGeometry(geometry);
        return new FeatureQueryEngine.MatchedFeature(match.ObjectId, new Feature(feature.Id, feature.Schema, attributes));
    }

    internal static IGeometry RoundGeometry(IGeometry geometry, int precision)
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
                GeometryFactory.CreateGeometryCollection(collection.Geometries.Select(member => RoundGeometry(member, precision)), crs),
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


    internal static IGeometry TransformGeometry(
        IGeometry geometry,
        CoordinateReference? source,
        CoordinateReference? target,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        if (target is not { } to || source is not { } from || from == to)
        {
            return geometry;
        }

        return transforms.Transform(geometry, from.ToString(), to.ToString(), cancellationToken);
    }
}
