using System.Buffers.Binary;
using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;

namespace Spatial.Core.Tests;

public class GeometryCodecTests
{
    public static TheoryData<string, IGeometry> RoundTripCases => BuildRoundTripCases();

    private static TheoryData<string, IGeometry> BuildRoundTripCases()
    {
        var crs = CoordinateReference.Epsg(4326);
        var otherCrs = CoordinateReference.Epsg(3857);
        var data = new TheoryData<string, IGeometry>();
        data.Add("point xy", GeometryFactory.CreatePoint(1.5, -2.25));
        data.Add("point xyz", GeometryFactory.CreatePoint(1, 2, 3));
        data.Add("point xym", new Point(new Coordinate(1, 2, M: 4)));
        data.Add("point xyzm", GeometryFactory.CreatePoint(1, 2, 3, 4));
        data.Add("point with crs", GeometryFactory.CreatePoint(1, 2, crs));
        data.Add("point z nan", new Point(new Coordinate(1, 2, Z: double.NaN)));
        data.Add("empty point", GeometryFactory.CreateEmptyPoint());
        data.Add("empty xyz point", GeometryFactory.CreateEmptyPoint(layout: CoordinateLayout.Xyz));
        data.Add("line xy", GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 5), new Coordinate(10, 2)]));
        data.Add("line xyz with nan z", GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 5, Z: 7)], CoordinateLayout.Xyz));
        data.Add("line xyzm with crs", GeometryFactory.CreateLineString(
            [new Coordinate(0, 0, Z: 1, M: 2), new Coordinate(5, 5, Z: 3, M: 4)], CoordinateLayout.Xyzm, crs));
        data.Add("empty line", GeometryFactory.CreateEmptyLineString());
        data.Add("empty xyz line", GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xym));
        data.Add("polygon with hole", GeometryFactory.CreatePolygon(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)]),
            [GeometryFactory.CreateLineString([new Coordinate(4, 4), new Coordinate(6, 4), new Coordinate(6, 6), new Coordinate(4, 6), new Coordinate(4, 4)])],
            crs));
        data.Add("empty polygon", GeometryFactory.CreatePolygon(GeometryFactory.CreateEmptyLineString()));
        data.Add("multi point mixed layouts",
            GeometryFactory.CreateMultiPoint(GeometryFactory.CreatePoint(1, 2), GeometryFactory.CreatePoint(3, 4, 5), GeometryFactory.CreateEmptyPoint()));
        data.Add("multi line string", GeometryFactory.CreateMultiLineString(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)]),
            GeometryFactory.CreateEmptyLineString(CoordinateLayout.Xyz)));
        data.Add("multi polygon", GeometryFactory.CreateMultiPolygon(
            GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(0, 1), new Coordinate(0, 0)]))));
        data.Add("empty multi point", GeometryFactory.CreateMultiPoint());
        data.Add("geometry collection nested",
            GeometryFactory.CreateGeometryCollection(
                GeometryFactory.CreatePoint(9, 9, otherCrs),
                GeometryFactory.CreateGeometryCollection(
                    GeometryFactory.CreateLineString([new Coordinate(1, 2)]),
                    GeometryFactory.CreateEmptyPoint()),
                GeometryFactory.CreatePolygon(GeometryFactory.CreateEmptyLineString())));
        data.Add("empty geometry collection", GeometryFactory.CreateGeometryCollection());
        return data;
    }

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Geometry_round_trips_exactly(string name, IGeometry geometry)
    {
        var bytes = GeometryCodec.Encode(geometry);
        var decoded = GeometryCodec.Decode(bytes);

        Assert.True(GeometryComparer.Equals(geometry, decoded), $"Round trip failed for '{name}': {geometry} vs {decoded}");
        Assert.Equal(geometry.GetHashCode(), decoded.GetHashCode());
        Assert.Equal(bytes, GeometryCodec.Encode(decoded));
    }

    [Fact]
    public void Encoding_is_deterministic_across_backings()
    {
        var packed = GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var array = new LineString(new ArrayCoordinateSequence([new Coordinate(1, 2), new Coordinate(3, 4)]));
        Assert.Equal(GeometryCodec.Encode(packed), GeometryCodec.Encode(array));
        Assert.Equal(GeometryCodec.Encode(packed), GeometryCodec.Encode(GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)])));
    }

    [Fact]
    public void Decoded_structures_are_concrete()
    {
        var decoded = GeometryCodec.Decode(GeometryCodec.Encode(GeometryFactory.CreatePolygon(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)]),
            [GeometryFactory.CreateLineString([new Coordinate(4, 4), new Coordinate(6, 4), new Coordinate(6, 6), new Coordinate(4, 6), new Coordinate(4, 4)])])));

        var polygon = Assert.IsType<Polygon>(decoded);
        Assert.IsType<LineString>(polygon.ExteriorRing);
        Assert.Single(polygon.InteriorRings);
        Assert.IsType<PackedCoordinateSequence>(polygon.ExteriorRing.Sequence);
    }

    [Fact]
    public void Empty_xyz_point_decodes_with_layout_preserved()
    {
        var bytes = Payload(
            [0x01], // layout Xyz
            [0x01], // type Point
            [0x00], // no CRS
            [0x00]); // hasCoordinate = 0 → empty

        var point = Assert.IsType<Point>(GeometryCodec.Decode(bytes));
        Assert.True(point.IsEmpty);
        Assert.Equal(CoordinateLayout.Xyz, point.Layout);
        Assert.Equal(bytes, GeometryCodec.Encode(point));
    }

    [Fact]
    public void Decode_rejects_missing_or_wrong_magic()
    {
        AssertFailed(Array.Empty<byte>(), "truncated");
        AssertFailed([0x53, 0x47, 0x45, 0x4F], "truncated");
        var wrongMagic = "XGEOM"u8.ToArray().Concat(new byte[] { 1 }).ToArray();
        AssertFailed(wrongMagic, "magic");
    }

    [Fact]
    public void Decode_rejects_unknown_version()
    {
        var bytes = new byte[GeometryCodec.HeaderLength + 3];
        GeometryCodec.Magic.CopyTo(bytes);
        bytes[GeometryCodec.HeaderLength - 1] = 9; // version 9
        AssertFailed(bytes, "version 9");
        // The offending byte is the version byte at offset 5 (magic 0..4 + version).
        Assert.False(GeometryCodec.TryDecode(bytes, out _, out var error));
        Assert.Contains("byte offset 5", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_rejects_unknown_layout_and_type_bytes()
    {
        AssertFailed(Payload([0x63], [0x01], [0x00]), "unknown coordinate layout");
        AssertFailed(Payload([0x00], [0x63], [0x00]), "unknown geometry type");
        // A point flagged present whose body holds one double instead of two: layout Xy requires 16 bytes.
        AssertFailed(Payload([0x00], [0x01], [0x00], [0x01], Int64Bits(1.0)), "point body is truncated");
        // A point with an invalid presence flag.
        AssertFailed(Payload([0x00], [0x01], [0x00], [0x02]), "point coordinate presence byte");
    }

    [Fact]
    public void Decode_rejects_invalid_crs_presence_and_content()
    {
        AssertFailed(Payload([0x00], [0x01], [0x02]), "CRS presence byte");
        // Input ends right where the CRS presence byte belongs.
        AssertFailed(Payload([0x00], [0x01]), "expected a CRS presence byte");
        // Authority is the empty string.
        AssertFailed(Payload([0x00], [0x01], [0x01], Int32(0), Int32(3), Str("EPS")), "non-empty");
        // Authority is not valid UTF-8.
        AssertFailed(Payload([0x00], [0x01], [0x01], Int32(2), [0xC3, 0x28], Int32(3), Str("EPS")), "UTF-8");
    }

    [Fact]
    public void Decode_rejects_truncated_and_trailing_input()
    {
        var valid = GeometryCodec.Encode(GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(3, 4)]));
        AssertFailed(valid.AsSpan(0, valid.Length - 1).ToArray(), "truncated");
        AssertFailed(valid.AsSpan(0, valid.Length - 3).ToArray(), "truncated");
        AssertFailed(valid.Concat(new byte[] { 0x00 }).ToArray(), "trailing");
    }

    [Fact]
    public void Decode_rejects_truncated_point_coordinate_with_actionable_error()
    {
        // A point flagged present whose body holds one double instead of two (layout Xy needs 16 bytes).
        var bytes = Payload([0x00], [0x01], [0x00], [0x01], Int64Bits(1.0));
        Assert.False(GeometryCodec.TryDecode(bytes, out var geometry, out var error));
        Assert.Null(geometry);
        Assert.Contains("point body is truncated", error, StringComparison.Ordinal);
        Assert.Contains("unexpected end of input", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_rejects_negative_and_impossible_counts()
    {
        // Line string declaring a negative coordinate count.
        AssertFailed(Payload([0x00], [0x02], [0x00], Int32(-1)), "element count");
        // Line string declaring more elements than the input can hold at all.
        AssertFailed(Payload([0x00], [0x02], [0x00], Int32(1_000_000)), "declares 1000000");
        // Line string whose declared coordinates need more bytes than remain:
        // 10 coordinates × stride 2 × 8 bytes = 160 required, only 40 present.
        // The required-byte figure in the error must stay exactly count × stride × 8.
        var truncated = Payload([0x00], [0x02], [0x00], Int32(10),
            Int64Bits(1.0), Int64Bits(2.0), Int64Bits(3.0), Int64Bits(4.0), Int64Bits(5.0));
        Assert.False(GeometryCodec.TryDecode(truncated, out _, out var error));
        Assert.Contains("declares 10 coordinates", error, StringComparison.Ordinal);
        Assert.Contains("160 required", error, StringComparison.Ordinal);
        // Multi-point declaring more elements than the input can hold.
        AssertFailed(Payload([0x00], [0x04], [0x00], Int32(100_000)), "declares 100000 elements");
    }

    [Fact]
    public void Decode_rejects_wrong_child_types()
    {
        // MultiPoint containing an (empty) line string element.
        var bytes = Payload(
            [0x00], [0x04], [0x00], Int32(1), // multi point, one element
            [0x00], [0x02], [0x00], Int32(0)); // element is an empty line string
        AssertFailed(bytes, "point 0 is a LineString");
    }

    [Fact]
    public void Decode_rejects_excessive_nesting()
    {
        var bytes = BuildNestedCollection(levels: GeometryCodec.MaxNestingDepth + 2);
        AssertFailed(bytes, "nesting");
    }

    [Fact]
    public void Encode_rejects_excessive_nesting()
    {
        IGeometry geometry = GeometryFactory.CreateEmptyPoint();
        for (var i = 0; i < GeometryCodec.MaxNestingDepth + 4; i++)
        {
            geometry = GeometryFactory.CreateGeometryCollection(geometry);
        }

        var exception = Assert.Throws<ArgumentException>(() => GeometryCodec.Encode(geometry));
        Assert.Contains($"nests deeper than the canonical format limit of {GeometryCodec.MaxNestingDepth} levels", exception.Message);
    }

    [Fact]
    public void Invalid_inputs_yield_actionable_errors()
    {
        var valid = GeometryCodec.Encode(GeometryFactory.CreatePoint(1, 2));
        Assert.False(GeometryCodec.TryDecode(valid.AsSpan(0, valid.Length - 2), out var geometry, out var error));
        Assert.Null(geometry);
        Assert.Contains("offset", error);
        Assert.Contains("truncated", error);
    }

    [Fact]
    public void Decode_throws_canonical_format_exception()
    {
        var exception = Assert.Throws<CanonicalFormatException>(() => GeometryCodec.Decode(new byte[] { 1, 2, 3 }));
        Assert.Contains("offset", exception.Message);
    }

    [Fact]
    public void Encode_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => GeometryCodec.Encode(null!));
    }

    private static void AssertFailed(byte[] bytes, string expectedFragment)
    {
        Assert.False(GeometryCodec.TryDecode(bytes, out var geometry, out var error));
        Assert.Null(geometry);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }

    internal static byte[] Payload(params byte[][] chunks)
    {
        var payload = chunks.SelectMany(chunk => chunk).ToArray();
        var bytes = new byte[GeometryCodec.HeaderLength + payload.Length];
        GeometryCodec.Magic.CopyTo(bytes);
        bytes[GeometryCodec.HeaderLength - 1] = GeometryCodec.FormatVersion;
        payload.CopyTo(bytes, GeometryCodec.HeaderLength);
        return bytes;
    }

    internal static byte[] Int32(int value)
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

    internal static byte[] BuildNestedCollection(int levels)
    {
        var chunks = new List<byte[]>();
        for (var i = 0; i < levels; i++)
        {
            chunks.Add([0x00]); // layout Xy
            chunks.Add([0x07]); // GeometryCollection
            chunks.Add([0x00]); // no CRS
            chunks.Add(Int32(1)); // one child
        }

        chunks.Add([0x00]); // layout Xy
        chunks.Add([0x01]); // Point
        chunks.Add([0x00]); // no CRS — empty point
        return Payload(chunks.ToArray());
    }
}
