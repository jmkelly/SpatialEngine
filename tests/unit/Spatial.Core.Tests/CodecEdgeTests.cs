using System.Buffers.Binary;
using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// Decode/encode error-path assertions that pin the exact diagnostic messages
/// (offset, counts, layout) — the strings a mutation pass can blank, and the
/// branches the round-trip suite never reaches (truncated CRS strings, wrong
/// ring/child types, zero-ring polygons, unknown geometry types).
/// </summary>
public class CodecEdgeTests
{
    [Fact]
    public void Decode_exact_header_edge_cases()
    {
        // Exactly the header (magic + version) with no payload: the layout byte
        // is missing, which must be reported as truncated input, not a header error.
        var headerOnly = new byte[GeometryCodec.HeaderLength];
        GeometryCodec.Magic.CopyTo(headerOnly);
        headerOnly[GeometryCodec.HeaderLength - 1] = GeometryCodec.FormatVersion;
        AssertFailed(headerOnly, "expected a coordinate layout byte");

        // Magic without the version byte.
        var magicOnly = "SGEOM"u8.ToArray();
        AssertFailed(magicOnly, "header is truncated");

        // Valid magic, version byte wrong.
        var wrongVersion = new byte[GeometryCodec.HeaderLength];
        GeometryCodec.Magic.CopyTo(wrongVersion);
        wrongVersion[GeometryCodec.HeaderLength - 1] = 0;
        AssertFailed(wrongVersion, "unsupported canonical geometry format version 0");
    }

    [Fact]
    public void Decode_node_header_truncation_and_unknown_values()
    {
        // Only a layout byte follows the header: the type byte is missing.
        AssertFailed(Payload([0x00]), "expected a geometry type byte");
        // Unknown layout / type bytes.
        AssertFailed(Payload([0x63]), "unknown coordinate layout byte 99");
        AssertFailed(Payload([0x00], [0x63]), "unknown geometry type byte 99");
        // No payload at all: the layout byte is missing.
        AssertFailed(Payload(), "expected a coordinate layout byte");
    }

    [Fact]
    public void Decode_crs_truncation_and_blank_values()
    {
        // Presence byte claims a CRS, then nothing follows.
        AssertFailed(Payload([0x00], [0x01], [0x01]), "expected a CRS authority string");
        // Authority parses, the code string is truncated.
        AssertFailed(Payload([0x00], [0x01], [0x01], Int32(3), Str("EPS"), Int32(3), Str("E")), "expected a CRS code string");
        // Authority is blank.
        AssertFailed(Payload([0x00], [0x01], [0x01], Int32(2), Str("  "), Int32(3), Str("EPS")), "must be non-empty");
        // Code is blank.
        AssertFailed(Payload([0x00], [0x01], [0x01], Int32(3), Str("EPS"), Int32(0)), "must be non-empty");
    }

    [Fact]
    public void Decode_point_body_truncation_reports_the_coordinate_read()
    {
        // Presence flag set, but only X is present (layout Xy needs 16 bytes).
        var bytes = Payload([0x00], [0x01], [0x00], [0x01], Int64Bits(1.0));
        AssertFailed(bytes, "point body is truncated for layout Xy");
        AssertFailed(bytes, "unexpected end of input at byte offset");
        AssertFailed(bytes, "while reading a coordinate");
        // Presence flag > 1 is invalid.
        AssertFailed(Payload([0x00], [0x01], [0x00], [0x02]), "invalid point coordinate presence byte 2");
    }

    [Fact]
    public void Decode_line_string_truncation_reports_exact_counts()
    {
        // Count parses (2), but only 11 bytes remain instead of the 32 required.
        var bytes = Payload([0x00], [0x02], [0x00], Int32(2), new byte[11]);
        AssertFailed(bytes, "line string is truncated: declares 2 coordinates but only 11 bytes remain (32 required)");
        // The element count itself is truncated (fewer than 4 bytes left).
        AssertFailed(Payload([0x00], [0x02], [0x00], [0x01, 0x00]), "invalid line string element count");
    }

    [Fact]
    public void Decode_polygon_ring_edge_cases()
    {
        // Zero rings decodes to an empty polygon (no error).
        var empty = GeometryCodec.Decode(Payload([0x00], [0x03], [0x00], Int32(0)));
        var polygon = Assert.IsType<Polygon>(empty);
        Assert.True(polygon.IsEmpty);
        Assert.Empty(polygon.InteriorRings);

        // A ring that is not a line string is rejected with the offset.
        var wrongRing = Payload([0x00], [0x03], [0x00], Int32(1), [0x00], [0x01], [0x00], [0x00]);
        AssertFailed(wrongRing, "ring 0 is a Point, expected a line string");

        // A truncated ring header reports which ring failed (enough bytes to
        // satisfy the element-count guard, then the ring itself is cut off).
        AssertFailed(Payload([0x00], [0x03], [0x00], Int32(1), [0x00], [0x01], [0x00]), "ring 0:");
    }

    [Fact]
    public void Decode_multi_children_with_wrong_types()
    {
        // MultiLineString containing a point element.
        AssertFailed(Payload([0x00], [0x05], [0x00], Int32(1), [0x00], [0x01], [0x00], [0x00]), "line string 0 is a Point, expected LineString");
        // MultiPolygon containing a point element.
        AssertFailed(Payload([0x00], [0x06], [0x00], Int32(1), [0x00], [0x01], [0x00], [0x00]), "polygon 0 is a Point, expected Polygon");
        // GeometryCollection accepts any child type (empty point here).
        var collection = GeometryCodec.Decode(Payload([0x00], [0x07], [0x00], Int32(1), [0x00], [0x01], [0x00], [0x00]));
        Assert.Single(Assert.IsType<GeometryCollection>(collection).Geometries);
        // The second child decodes to the wrong type for the container.
        AssertFailed(Payload([0x00], [0x04], [0x00], Int32(2), [0x00], [0x01], [0x00], [0x00], [0x00], [0x02], [0x00], Int32(0)), "point 1 is a LineString, expected Point");
    }

    [Fact]
    public void Encode_unknown_geometry_type_yields_actionable_error()
    {
        var foreign = new ForeignGeometry();
        var exception = Assert.Throws<ArgumentException>(() => GeometryCodec.Encode(foreign));
        Assert.Contains("Unknown geometry type '200'", exception.Message);
    }

    private static void AssertFailed(byte[] bytes, string expectedFragment)
    {
        Assert.False(GeometryCodec.TryDecode(bytes, out var geometry, out var error));
        Assert.Null(geometry);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    private static byte[] Payload(params byte[][] chunks)
    {
        var payload = chunks.SelectMany(chunk => chunk).ToArray();
        var bytes = new byte[GeometryCodec.HeaderLength + payload.Length];
        GeometryCodec.Magic.CopyTo(bytes);
        bytes[GeometryCodec.HeaderLength - 1] = GeometryCodec.FormatVersion;
        payload.CopyTo(bytes, GeometryCodec.HeaderLength);
        return bytes;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int64Bits(double value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, BitConverter.DoubleToInt64Bits(value));
        return bytes;
    }

    private static byte[] Str(string value) => System.Text.Encoding.UTF8.GetBytes(value);
}
