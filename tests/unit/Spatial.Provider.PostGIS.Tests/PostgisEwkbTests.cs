using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Provider.PostGIS.Geometry;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The EWKB ↔ core geometry interchange (the plugin's only
/// <c>Spatial.Core.Geometry</c> surface, ADR-0028): known PostGIS byte
/// anchors, per-type round trips (XY/XYZ/XYM/XYZM, empty geometries,
/// composites, SRID → CRS), endianness, malformed-payload diagnostics with
/// byte offsets, and the write-side CRS conflict rule.
/// </summary>
public sealed class PostgisEwkbTests
{
    [Fact]
    public void Known_postgis_srid_4326_point_bytes_decode_to_a_berlin_dot()
    {
        var geometry = Read(EwkbFixture.KnownPoint4326());

        var point = Assert.IsType<Point>(geometry);
        Assert.Equal(1, point.X);
        Assert.Equal(2, point.Y);
        Assert.Equal(new CoordinateReference("EPSG", "4326"), point.CoordinateReference);
        Assert.False(geometry.IsEmpty);
    }

    [Fact]
    public void Point_without_srid_has_no_crs()
    {
        var geometry = Read(EwkbFixture.Point());

        Assert.Null(geometry.CoordinateReference);
        Assert.Equal(1, Assert.IsType<Point>(geometry).X);
    }

    [Fact]
    public void Empty_point_sentinel_stays_empty()
    {
        var geometry = Read(EwkbFixture.PointEmpty());

        Assert.True(geometry.IsEmpty);
        Assert.IsType<Point>(geometry);
    }

    [Fact]
    public void Big_endian_point_decodes()
    {
        var point = Assert.IsType<Point>(Read(EwkbFixture.PointBigEndian(3.5, -4.25)));

        Assert.Equal(3.5, point.X);
        Assert.Equal(-4.25, point.Y);
    }

    [Fact]
    public void Point_z_and_point_m_decode_with_layouts()
    {
        var withZ = Assert.IsType<Point>(Read(EwkbFixture.PointZ(9)));
        Assert.Equal(CoordinateLayout.Xyz, withZ.Layout);
        Assert.Equal(9, withZ.Z);

        var withM = Assert.IsType<Point>(Read(EwkbFixture.PointM(1, 2, 7)));
        Assert.Equal(CoordinateLayout.Xym, withM.Layout);
        Assert.Equal(7, withM.M);
        Assert.Null(withM.Z);
    }

    [Fact]
    public void Point_xyzm_decodes_and_writer_preserves_all_ordinates()
    {
        var decoded = Assert.IsType<Point>(Read(EwkbFixture.PointXyzm(4326, 1, 2, 3, 4)));

        Assert.Equal(CoordinateLayout.Xyzm, decoded.Layout);
        Assert.Equal(3, decoded.Z);
        Assert.Equal(4, decoded.M);

        var roundTrip = Read(Write(decoded, 4326));
        Assert.Equal(decoded, roundTrip);
    }

    [Fact]
    public void Line_string_decodes_and_round_trips()
    {
        var decoded = Assert.IsType<LineString>(Read(EwkbFixture.LineString(4326, 0, 0, 1, 1, 2, 1)));

        Assert.Equal(3, decoded.CoordinateCount);
        Assert.Equal(new CoordinateReference("EPSG", "4326"), decoded.CoordinateReference);
        Assert.Equal(decoded, Read(Write(decoded, 4326)));
    }

    [Fact]
    public void Empty_line_string_and_polygon_decode_empty()
    {
        Assert.True(Read(EwkbFixture.LineStringEmpty(4326)).IsEmpty);
        Assert.True(Read(EwkbFixture.PolygonEmpty(4326)).IsEmpty);
    }

    [Fact]
    public void Polygon_with_hole_decodes_and_round_trips()
    {
        var polygon = Assert.IsType<Polygon>(Read(EwkbFixture.Polygon(
            4326,
            [0, 0, 4, 0, 4, 4, 0, 4, 0, 0],
            [1, 1, 2, 1, 2, 2, 1, 2, 1, 1])));

        Assert.Equal(10, polygon.CoordinateCount);
        Assert.Single(polygon.InteriorRings);
        Assert.Equal(polygon, Read(Write(polygon, 4326)));
    }

    [Fact]
    public void Line_string_with_z_ordinates_round_trips_through_the_writer()
    {
        var line = GeometryFactory.CreateLineString(
            [new Coordinate(1, 2, 5), new Coordinate(3, 4, 6)], CoordinateLayout.Xyz);

        var decoded = Assert.IsType<LineString>(Read(Write(line, 0)));

        Assert.Equal(CoordinateLayout.Xyz, decoded.Layout);
        Assert.Equal(line, decoded);
    }

    [Fact]
    public void Line_string_with_m_ordinates_round_trips_through_the_writer()
    {
        var line = GeometryFactory.CreateLineString(
            [new Coordinate(1, 2, null, 7), new Coordinate(3, 4, null, 8)], CoordinateLayout.Xym);

        var decoded = Assert.IsType<LineString>(Read(Write(line, 0)));

        Assert.Equal(CoordinateLayout.Xym, decoded.Layout);
        Assert.Equal(line, decoded);
    }

    [Fact]
    public void Line_string_with_z_and_m_ordinates_round_trips_through_the_writer()
    {
        var line = GeometryFactory.CreateLineString(
            [new Coordinate(1, 2, 5, 7), new Coordinate(3, 4, 6, 8)], CoordinateLayout.Xyzm);

        var decoded = Assert.IsType<LineString>(Read(Write(line, 0)));

        Assert.Equal(CoordinateLayout.Xyzm, decoded.Layout);
        Assert.Equal(line, decoded);
    }

    [Fact]
    public void Multi_geometries_and_collections_decode()
    {
        var multiPoint = EwkbFixture.Multi(4, 4326, EwkbFixture.Point(4326), EwkbFixture.PointXyz(0, 5, 6, 7));
        var points = Assert.IsType<MultiPoint>(Read(multiPoint));
        Assert.Equal(2, points.Points.Count);

        var multiLine = EwkbFixture.Multi(5, 0, EwkbFixture.LineString(0, 0, 0, 1, 0), EwkbFixture.LineString(0, 2, 0, 3, 0));
        Assert.Equal(2, Assert.IsType<MultiLineString>(Read(multiLine)).LineStrings.Count);

        var multiPolygon = EwkbFixture.Multi(6, 0, EwkbFixture.Polygon(0, [0, 0, 1, 0, 1, 1, 0, 0]));
        Assert.Single(Assert.IsType<MultiPolygon>(Read(multiPolygon)).Polygons);

        var collection = EwkbFixture.Multi(7, 0, EwkbFixture.Point(), EwkbFixture.LineString(0, 0, 0, 1, 1));
        var decodedCollection = Assert.IsType<GeometryCollection>(Read(collection));
        Assert.Equal(2, decodedCollection.Geometries.Count);
        Assert.Equal(GeometryType.Point, decodedCollection.Geometries[0].Type);
        Assert.Equal(GeometryType.LineString, decodedCollection.Geometries[1].Type);
    }

    [Fact]
    public void Collection_and_multipart_round_trip_and_children_share_no_crs()
    {
        var collection = Read(EwkbFixture.Multi(7, 3857, EwkbFixture.Point(0), EwkbFixture.LineString(0, 0, 0, 1, 1)));

        var roundTrip = Read(Write(collection, 3857));

        Assert.Equal(collection, roundTrip);
        Assert.Equal(new CoordinateReference("EPSG", "3857"), roundTrip.CoordinateReference);
    }

    [Fact]
    public void Writer_emits_the_known_postgis_point_anchor_exactly()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));
        var bytes = Write(geometry, 4326);

        Assert.Equal(EwkbFixture.KnownPoint4326(), bytes);
    }

    [Fact]
    public void Writer_without_srid_omits_the_srid_flag()
    {
        var bytes = Write(GeometryFactory.CreatePoint(1, 2), srid: 0);

        Assert.Equal(EwkbFixture.Point(), bytes);
    }

    [Fact]
    public void Writer_refuses_a_crs_that_conflicts_with_the_dataset_srid()
    {
        var attribute = FeatureTests.Geometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326)));

        var exception = Assert.Throws<PostgisCrsMismatchException>(() => PostgisEwkb.WriteGeometry(attribute, 3857));

        Assert.Contains("EPSG:4326", exception.Message);
        Assert.Contains("SRID 3857", exception.Message);
    }

    [Fact]
    public void Writer_accepts_a_crsless_geometry_at_the_dataset_srid()
    {
        var attribute = FeatureTests.Geometry(GeometryFactory.CreatePoint(1, 2));

        var bytes = PostgisEwkb.WriteGeometry(attribute, 4326);

        Assert.Equal(EwkbFixture.KnownPoint4326(), bytes);
    }

    [Fact]
    public void Empty_geometry_write_is_stable()
    {
        Assert.True(Read(Write(GeometryFactory.CreateEmptyPoint(), 0)).IsEmpty);
        Assert.True(Read(Write(GeometryFactory.CreateEmptyPoint(CoordinateReference.Epsg(4326), CoordinateLayout.Xyz), 4326)).IsEmpty);
        Assert.True(Read(Write(GeometryFactory.CreateEmptyLineString(), 0)).IsEmpty);
        Assert.IsType<Polygon>(Read(Write(Read(EwkbFixture.PolygonEmpty()), 0)));
        Assert.True(Read(Write(Read(EwkbFixture.PolygonEmpty()), 0)).IsEmpty);
    }

    [Fact]
    public void Point_z_only_round_trips_through_the_writer()
    {
        var decoded = Assert.IsType<Point>(Read(EwkbFixture.PointZ(9)));

        var roundTrip = Assert.IsType<Point>(Read(Write(decoded, 0)));

        Assert.Equal(CoordinateLayout.Xyz, roundTrip.Layout);
        Assert.Equal(9, roundTrip.Z);
        Assert.Null(roundTrip.M);
    }

    [Fact]
    public void Point_m_only_round_trips_through_the_writer()
    {
        var decoded = Assert.IsType<Point>(Read(EwkbFixture.PointM(1, 2, 7)));

        var roundTrip = Assert.IsType<Point>(Read(Write(decoded, 0)));

        Assert.Equal(CoordinateLayout.Xym, roundTrip.Layout);
        Assert.Null(roundTrip.Z);
        Assert.Equal(7, roundTrip.M);
    }

    [Fact]
    public void Malformed_payloads_fail_with_byte_accurate_errors()
    {
        AssertOffset(EwkbFixture.KnownPoint4326()[..^4]); // truncated
        AssertOffset([0x01, 0x01]); // type but no payload
        AssertOffset([0x01, 0x07, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]); // absurd collection count
        AssertOffset([0x01, 0x63, 0x00, 0x00, 0x00]); // unknown type 0x63
        AssertOffset([0x01, 0x03, 0x00, 0x00, 0x10]); // bbox flag is rejected
    }

    [Fact]
    public void Read_geometry_attribute_wraps_the_decoded_value()
    {
        var attribute = PostgisEwkb.ReadGeometry(EwkbFixture.KnownPoint4326());

        Assert.Equal(AttributeKind.Geometry, attribute.Kind);
        Assert.Equal(GeometryType.Point, attribute.GeometryValue.Type);
        Assert.Equal(new CoordinateReference("EPSG", "4326"), attribute.GeometryValue.CoordinateReference);
    }

    [Fact]
    public void Malformed_stored_geometry_throws_the_format_exception()
    {
        Assert.Throws<PostgisEwkbFormatException>(() => PostgisEwkb.ReadGeometry([0x01, 0x01, 0x00]));
    }

    private static void AssertOffset(byte[] bytes)
    {
        var exception = Assert.Throws<PostgisEwkbFormatException>(() => Read(bytes));
        Assert.Contains("offset", exception.Message);
    }

    private static IGeometry Read(byte[] bytes) => PostgisEwkb.ReadGeometry(bytes).GeometryValue;

    private static byte[] Write(IGeometry geometry, int srid) => PostgisEwkb.WriteGeometry(FeatureTests.Geometry(geometry), srid);
}
