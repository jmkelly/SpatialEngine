using System.Buffers.Binary;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Stores.SqlServer.Geometry;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The WKB interchange (ADR-0072): OGC well-known binary in, core geometry
/// out and back, byte-deterministic, with the SRID/CRS and layout rules the
/// SQL Server provider promises. Malformed payloads fail with a typed format
/// error; a CRS conflict and a Z/M layout are refused rather than mangled.
/// </summary>
public sealed class SqlServerWkbTests
{
    [Fact]
    public void A_point_round_trips_with_the_dataset_crs_stamped()
    {
        var written = SqlServerWkb.WriteGeometry(
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1.5, 2.5, CoordinateReference.Epsg(4326))), 4326);

        Assert.Equal("0101000000" + "000000000000F83F" + "0000000000000440", Convert.ToHexString(written));

        var read = SqlServerWkb.ReadGeometry(written, 4326);
        var point = Assert.IsType<Point>(read.GeometryValue);
        Assert.Equal(1.5, (double)point.X!, precision: 12);
        Assert.Equal(2.5, (double)point.Y!, precision: 12);
        Assert.Equal(CoordinateReference.Epsg(4326), point.CoordinateReference);
    }

    [Fact]
    public void A_planar_srid_stays_an_unknown_crs()
    {
        var written = SqlServerWkb.WriteGeometry(AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2)), 0);

        var read = SqlServerWkb.ReadGeometry(written, 0);

        Assert.Null(read.GeometryValue.CoordinateReference);
    }

    [Fact]
    public void Every_geometry_type_round_trips()
    {
        var point = GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326));
        LineString line = (LineString)Rebased(Line());
        Polygon polygon = (Polygon)Rebased(Polygon());

        Assert.Equal(GeometryType.Point, RoundTrip(point));
        Assert.Equal(GeometryType.LineString, RoundTrip(line));
        Assert.Equal(GeometryType.Polygon, RoundTrip(polygon));
        var crs = CoordinateReference.Epsg(4326);
        var points = new[] { GeometryFactory.CreatePoint(1, 2), GeometryFactory.CreatePoint(3, 4) };
        var lines = new[] { line };
        var polygons = new[] { polygon };
        IGeometry[] parts = [point, line];

        Assert.Equal(GeometryType.MultiPoint, RoundTrip(GeometryFactory.CreateMultiPoint(points, crs)));
        Assert.Equal(GeometryType.MultiLineString, RoundTrip(GeometryFactory.CreateMultiLineString(lines, crs)));
        Assert.Equal(GeometryType.MultiPolygon, RoundTrip(GeometryFactory.CreateMultiPolygon(polygons, crs)));
        Assert.Equal(GeometryType.GeometryCollection, RoundTrip(GeometryFactory.CreateGeometryCollection(parts, crs)));
    }

    [Fact]
    public void Empty_geometries_survive_the_round_trip()
    {
        Assert.True(SqlServerWkb.ReadGeometry(
            SqlServerWkb.WriteGeometry(AttributeValue.FromGeometry(GeometryFactory.CreateEmptyPoint()), 0), 0)
            .GeometryValue.IsEmpty);
        Assert.True(SqlServerWkb.ReadGeometry(
            SqlServerWkb.WriteGeometry(AttributeValue.FromGeometry(GeometryFactory.CreateEmptyLineString()), 0), 0)
            .GeometryValue.IsEmpty);
    }

    [Fact]
    public void An_empty_point_uses_the_nan_sentinel_sql_server_writes()
    {
        var written = SqlServerWkb.WriteGeometry(AttributeValue.FromGeometry(GeometryFactory.CreateEmptyPoint()), 0);

        // Byte order 1, type 1 (point), then two NaN doubles — .NET's
        // double.NaN is 0xFFF8000000000000, little-endian.
        byte[] nan = [0, 0, 0, 0, 0, 0, 0xF8, 0xFF];
        Assert.Equal<byte[]>([0x01, 0x01, 0, 0, 0, .. nan, .. nan], written);
    }

    [Fact]
    public void A_big_endian_child_is_read_as_well_as_a_little_endian_one()
    {
        var payload = new byte[21];
        payload[0] = 0x00;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1, 4), 1);
        BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(5, 8), 1);
        BinaryPrimitives.WriteDoubleBigEndian(payload.AsSpan(13, 8), 2);

        var read = SqlServerWkb.ReadGeometry(payload, 0);

        Assert.Equal(1, (double)Assert.IsType<Point>(read.GeometryValue).X!, precision: 12);
    }

    [Fact]
    public void A_crs_conflict_is_refused()
    {
        var failure = Assert.Throws<SqlServerCrsMismatchException>(() => SqlServerWkb.WriteGeometry(
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(3857))), 4326));

        Assert.Contains("EPSG:3857", failure.Message);
    }

    [Fact]
    public void A_z_or_m_layout_is_refused_rather_than_flattened()
    {
        var xyz = AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, 3, CoordinateReference.Epsg(4326)));

        var failure = Assert.Throws<SqlServerLayoutNotSupportedException>(() => SqlServerWkb.WriteGeometry(xyz, 4326));

        Assert.Contains("XYZ", failure.Message);
        Assert.False(SqlServerWkb.IsStorable(xyz));
        Assert.True(SqlServerWkb.IsStorable(AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))));
        Assert.True(SqlServerWkb.IsStorable(AttributeValue.Null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(12)]
    [InlineData(20)]
    public void A_truncated_payload_fails_with_a_format_error(int length)
    {
        var full = SqlServerWkb.WriteGeometry(
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2, CoordinateReference.Epsg(4326))), 4326);
        var truncated = full[..length];

        var failure = Assert.Throws<SqlServerWkbFormatException>(() => SqlServerWkb.ReadGeometry(truncated, 4326));

        Assert.NotEmpty(failure.Message);
    }

    [Fact]
    public void An_unsupported_geometry_type_fails_with_a_format_error()
    {
        var payload = new byte[5];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(1), 42);

        var failure = Assert.Throws<SqlServerWkbFormatException>(() => SqlServerWkb.ReadGeometry(payload, 4326));

        Assert.Contains("42", failure.Message);
    }

    [Fact]
    public void The_bbox_envelope_is_a_closed_ring_or_a_degenerate_line_or_point()
    {
        var polygon = SqlServerWkb.ReadGeometry(SqlServerWkb.Envelope(0, 1, 2, 3), 4326).GeometryValue;
        Assert.Equal(GeometryType.Polygon, polygon.Type);
        var ring = Assert.IsAssignableFrom<IPolygon>(polygon).ExteriorRing.Sequence;
        Assert.Equal(5, ring.Count);
        Assert.Equal(ring.GetOrdinate(0, Ordinate.X), ring.GetOrdinate(4, Ordinate.X));

        var line = SqlServerWkb.ReadGeometry(SqlServerWkb.Envelope(0, 1, 0, 3), 4326).GeometryValue;
        Assert.Equal(GeometryType.LineString, line.Type);

        var point = SqlServerWkb.ReadGeometry(SqlServerWkb.Envelope(0, 1, 0, 1), 4326).GeometryValue;
        Assert.Equal(GeometryType.Point, point.Type);
    }

    [Fact]
    public void Layout_names_are_reported_for_diagnostics()
    {
        Assert.Equal("XY", SqlServerWkb.LayoutName(AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))));
        Assert.Equal("not geometry", SqlServerWkb.LayoutName(AttributeValue.FromString("x")));
    }

    private static GeometryType RoundTrip(IGeometry geometry)
    {
        var written = SqlServerWkb.WriteGeometry(AttributeValue.FromGeometry(geometry), 4326);
        return SqlServerWkb.ReadGeometry(written, 4326).GeometryValue.Type;
    }

    /// <summary>Restamps a geometry with the CRS the round trip expects to find.</summary>
    private static IGeometry Rebased(IGeometry geometry) => geometry switch
    {
        LineString line => GeometryFactory.CreateLineString(line.Sequence, CoordinateReference.Epsg(4326)),
        Polygon polygon => GeometryFactory.CreatePolygon(
            polygon.ExteriorRing, polygon.InteriorRings, CoordinateReference.Epsg(4326)),
        _ => geometry,
    };

    private static LineString Line() => GeometryFactory.CreateLineString(new[]
    {
        new Coordinate(0, 0, null, null),
        new Coordinate(10, 10, null, null),
    });

    private static Polygon Polygon() => GeometryFactory.CreatePolygon(
        GeometryFactory.CreateLineString(
        [
            new Coordinate(0, 0, null, null),
            new Coordinate(1, 0, null, null),
            new Coordinate(1, 1, null, null),
            new Coordinate(0, 0, null, null),
        ]));
}
