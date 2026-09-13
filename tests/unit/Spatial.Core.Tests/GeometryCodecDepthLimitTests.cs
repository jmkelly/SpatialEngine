using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;

namespace Spatial.Core.Tests;

/// <summary>
/// T-005: boundary assertions that kill the GeometryCodec mutant survivors —
/// nesting-depth limits at exactly <see cref="GeometryCodec.MaxNestingDepth"/>
/// (succeeds) versus one level deeper (fails), exercised through every
/// container type so each depth + 1 site is pinned, plus malformed-input
/// guards at their exact byte boundaries. The codec is synchronous, so only
/// success and failure outcomes apply (no cancellation).
/// </summary>
public class GeometryCodecDepthLimitTests
{
    public static TheoryData<string, int, bool> ContainerDepthCases => BuildContainerDepthCases();

    private static TheoryData<string, int, bool> BuildContainerDepthCases()
    {
        var max = GeometryCodec.MaxNestingDepth;
        var data = new TheoryData<string, int, bool>();
        foreach (var kind in new[] { "multipoint", "multilinestring", "polygon" })
        {
            data.Add(kind, max - 1, true);
            data.Add(kind, max, false);
        }

        data.Add("multipolygon", max - 2, true);
        data.Add("multipolygon", max - 1, false);
        return data;
    }

    [Fact]
    public void Encode_AtExactlyMaxNestingDepth_Succeeds()
    {
        var geometry = WrapInCollections(GeometryFactory.CreatePoint(1, 2), GeometryCodec.MaxNestingDepth);

        var bytes = GeometryCodec.Encode(geometry);

        Assert.Equal(bytes, GeometryCodec.Encode(GeometryCodec.Decode(bytes)));
    }

    [Fact]
    public void Encode_OneBeyondMaxNestingDepth_Throws()
    {
        var geometry = WrapInCollections(GeometryFactory.CreatePoint(1, 2), GeometryCodec.MaxNestingDepth + 1);

        var exception = Assert.Throws<ArgumentException>(() => GeometryCodec.Encode(geometry));
        Assert.Contains(
            $"nests deeper than the canonical format limit of {GeometryCodec.MaxNestingDepth} levels",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_AtExactlyMaxNestingDepth_Succeeds()
    {
        var bytes = BuildNestedCollectionWithEmptyPoint(GeometryCodec.MaxNestingDepth);

        Assert.True(GeometryCodec.TryDecode(bytes, out var geometry, out var error), $"Expected success at the limit: {error}");
        Assert.NotNull(geometry);
        Assert.Equal(bytes, GeometryCodec.Encode(geometry));
    }

    [Fact]
    public void Decode_OneBeyondMaxNestingDepth_Fails()
    {
        var bytes = BuildNestedCollectionWithEmptyPoint(GeometryCodec.MaxNestingDepth + 1);

        Assert.False(GeometryCodec.TryDecode(bytes, out var geometry, out var error));
        Assert.Null(geometry);
        Assert.Contains(
            $"nesting exceeds the canonical format limit of {GeometryCodec.MaxNestingDepth} levels",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ContainerDepthCases))]
    public void Containers_PinDepthAccountingAtTheLimit(string kind, int levels, bool shouldSucceed)
    {
        var tail = CreateTail(kind);
        var nested = WrapInCollections(tail, levels);
        var handbuilt = PrefixWithCollections(tail, levels);

        if (shouldSucceed)
        {
            var bytes = GeometryCodec.Encode(nested);
            Assert.True(GeometryCodec.TryDecode(bytes, out var decoded, out var error), $"Encode/decode at {levels} levels: {error}");
            Assert.Equal(bytes, GeometryCodec.Encode(decoded!));
            Assert.True(GeometryCodec.TryDecode(handbuilt, out _, out var handError), $"Handbuilt at {levels} levels: {handError}");
            return;
        }

        var exception = Assert.Throws<ArgumentException>(() => GeometryCodec.Encode(nested));
        Assert.Contains(
            $"nests deeper than the canonical format limit of {GeometryCodec.MaxNestingDepth} levels",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(GeometryCodec.TryDecode(handbuilt, out var geometry, out var decodeError));
        Assert.Null(geometry);
        Assert.Contains(
            $"nesting exceeds the canonical format limit of {GeometryCodec.MaxNestingDepth} levels",
            decodeError,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_ElementCountGuard_ExactBoundary()
    {
        // One element needs at least 3 bytes after the count: 2 bytes remain → element-count guard.
        var tooFew = GeometryCodecTests.Payload([0x00], [0x02], [0x00], GeometryCodecTests.Int32(1), new byte[2]);
        AssertFailed(tooFew, "each element needs at least 3 bytes");

        // Exactly 3 bytes remain: the element-count guard passes (3 > 3 is false) and the
        // coordinate-bytes guard reports the real deficit (1 coordinate × stride 2 × 8 = 16 required).
        var enoughForGuard = GeometryCodecTests.Payload([0x00], [0x02], [0x00], GeometryCodecTests.Int32(1), new byte[3]);
        AssertFailed(enoughForGuard, "declares 1 coordinates");
        AssertFailed(enoughForGuard, "16 required");
    }

    [Fact]
    public void Decode_CoordinateBytesGuard_ExactBoundary()
    {
        // 2 coordinates × stride 2 × 8 bytes = 32 required: 31 bytes → truncated with the exact figure.
        var oneShort = GeometryCodecTests.Payload([0x00], [0x02], [0x00], GeometryCodecTests.Int32(2), new byte[31]);
        AssertFailed(oneShort, "declares 2 coordinates");
        AssertFailed(oneShort, "32 required");

        // Exactly 32 bytes → the guard passes (32 > 32 is false) and the line string decodes.
        var exact = GeometryCodecTests.Payload([0x00], [0x02], [0x00], GeometryCodecTests.Int32(2), new byte[32]);
        var line = Assert.IsType<LineString>(GeometryCodec.Decode(exact));
        Assert.Equal(2, line.Sequence.Count);
    }

    [Theory]
    [InlineData(1, 16, "Xyz")]
    [InlineData(2, 16, "Xym")]
    [InlineData(3, 24, "Xyzm")]
    public void Decode_PointTruncation_ForEveryLayout(byte layout, int presentBytes, string layoutName)
    {
        var bytes = GeometryCodecTests.Payload([layout], [0x01], [0x00], [0x01], new byte[presentBytes]);

        AssertFailed(bytes, $"point body is truncated for layout {layoutName}");
        AssertFailed(bytes, "unexpected end of input");
        AssertFailed(bytes, "while reading a coordinate");
    }

    [Fact]
    public void Decode_WhitespaceCrsCode_Rejected()
    {
        var bytes = GeometryCodecTests.Payload(
            [0x00], [0x01], [0x01],
            GeometryCodecTests.Int32(3), System.Text.Encoding.UTF8.GetBytes("EPS"),
            GeometryCodecTests.Int32(2), System.Text.Encoding.UTF8.GetBytes("  "));

        AssertFailed(bytes, "must be non-empty");
    }

    [Fact]
    public void Empty_Multi_Containers_RoundTrip()
    {
        // An empty part list encodes a zero count: Sum over zero parts is 0,
        // while a Max substitution would throw, so empties pin the aggregation.
        foreach (var geometry in new IGeometry[]
                 {
                     GeometryFactory.CreateMultiLineString(),
                     GeometryFactory.CreateMultiPolygon(),
                 })
        {
            var bytes = GeometryCodec.Encode(geometry);
            var decoded = GeometryCodec.Decode(bytes);
            Assert.True(GeometryComparer.Equals(geometry, decoded), $"Round trip failed for {geometry.Type}.");
            Assert.Equal(bytes, GeometryCodec.Encode(decoded));
        }
    }

    [Fact]
    public void MultiPolygon_With_Differently_Sized_Parts_RoundTrips()
    {
        // Two polygons with different encoded lengths: the size prefix must sum
        // every part, so a Max substitution under-allocates and the encode fails.
        var triangle = GeometryFactory.CreatePolygon(GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(0, 1), new Coordinate(0, 0)]));
        var square = GeometryFactory.CreatePolygon(SquareRing());
        var geometry = GeometryFactory.CreateMultiPolygon(triangle, square);

        var bytes = GeometryCodec.Encode(geometry);
        var decoded = GeometryCodec.Decode(bytes);
        Assert.True(GeometryComparer.Equals(geometry, decoded), "Round trip failed for a multi-polygon with differently sized parts.");
        Assert.Equal(bytes, GeometryCodec.Encode(decoded));
    }

    [Fact]
    public void Decode_MissingPointPresenceByte_IsActionable()
    {
        // The payload ends right after the CRS presence byte: the point body is
        // missing its coordinate-presence flag, which must be named in the error.
        var bytes = GeometryCodecTests.Payload([0x00], [0x01], [0x00]);

        AssertFailed(bytes, "expected a point coordinate presence byte");
    }

    [Fact]
    public void Decode_ElementCountErrors_NameTheElement()
    {
        // The element kind ("polygon", "multi-line string") is part of the
        // diagnostic: blanking it must break these assertions.
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x03], [0x00], GeometryCodecTests.Int32(-1)),
            "invalid polygon element count");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x03], [0x00], GeometryCodecTests.Int32(100_000)),
            "polygon declares 100000 elements");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x05], [0x00], GeometryCodecTests.Int32(-1)),
            "invalid multi-line string element count");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x05], [0x00], GeometryCodecTests.Int32(100_000)),
            "multi-line string declares 100000 elements");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x04], [0x00], GeometryCodecTests.Int32(-1)),
            "invalid multi-point element count");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x04], [0x00], GeometryCodecTests.Int32(100_000)),
            "multi-point declares 100000 elements");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x06], [0x00], GeometryCodecTests.Int32(-1)),
            "invalid multi-polygon element count");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x06], [0x00], GeometryCodecTests.Int32(100_000)),
            "multi-polygon declares 100000 elements");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x07], [0x00], GeometryCodecTests.Int32(-1)),
            "invalid geometry collection element count");
        AssertFailed(
            GeometryCodecTests.Payload([0x00], [0x07], [0x00], GeometryCodecTests.Int32(100_000)),
            "geometry collection declares 100000 elements");
    }

    [Fact]
    public void Decode_CollectionChildFailure_NamesThePart()
    {
        // A collection child that fails to decode is reported as "part {i}":
        // blanking the fallback element name must break this assertion. The
        // child below is a point node cut off before its presence flag.
        var bytes = GeometryCodecTests.Payload(
            [0x00], [0x07], [0x00], GeometryCodecTests.Int32(1),
            [0x00], [0x01], [0x00]);

        AssertFailed(bytes, "part 0:");
    }

    [Fact]
    public void Decode_InvalidUtf8Authority_NamesTheAuthorityString()
    {
        // 0xC3 0x28 is not valid UTF-8: the failure must blame the authority
        // string specifically, not fall through to a later stage. A following
        // valid code string keeps the failure located at the authority read.
        var bytes = GeometryCodecTests.Payload(
            [0x00], [0x01], [0x01],
            GeometryCodecTests.Int32(2), [0xC3, 0x28],
            GeometryCodecTests.Int32(3), System.Text.Encoding.UTF8.GetBytes("EPS"));

        AssertFailed(bytes, "expected a CRS authority string");
    }

    private static IGeometry WrapInCollections(IGeometry tail, int levels)
    {
        var geometry = tail;
        for (var i = 0; i < levels; i++)
        {
            geometry = GeometryFactory.CreateGeometryCollection(geometry);
        }

        return geometry;
    }

    private static IGeometry CreateTail(string kind) => kind switch
    {
        "multipoint" => GeometryFactory.CreateMultiPoint(GeometryFactory.CreatePoint(1, 2)),
        "multilinestring" => GeometryFactory.CreateMultiLineString(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1)])),
        "polygon" => GeometryFactory.CreatePolygon(SquareRing()),
        "multipolygon" => GeometryFactory.CreateMultiPolygon(GeometryFactory.CreatePolygon(SquareRing())),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown container kind."),
    };

    private static LineString SquareRing() => GeometryFactory.CreateLineString(
        [new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 1), new Coordinate(0, 0)]);

    private static byte[] PrefixWithCollections(IGeometry tail, int levels)
    {
        var tailNode = GeometryCodec.Encode(tail).AsSpan(GeometryCodec.HeaderLength).ToArray();
        var chunks = new List<byte[]>();
        for (var i = 0; i < levels; i++)
        {
            chunks.Add([0x00]); // layout Xy
            chunks.Add([0x07]); // GeometryCollection
            chunks.Add([0x00]); // no CRS
            chunks.Add(GeometryCodecTests.Int32(1)); // one child
        }

        chunks.Add(tailNode);
        return GeometryCodecTests.Payload(chunks.ToArray());
    }

    private static byte[] BuildNestedCollectionWithEmptyPoint(int levels)
    {
        // GeometryCodecTests.BuildNestedCollection terminates in a truncated point
        // (no presence byte), which only suits failure cases where the nesting guard
        // trips first. The well-formed empty point here lets the at-limit decode succeed.
        var chunks = new List<byte[]>();
        for (var i = 0; i < levels; i++)
        {
            chunks.Add([0x00]); // layout Xy
            chunks.Add([0x07]); // GeometryCollection
            chunks.Add([0x00]); // no CRS
            chunks.Add(GeometryCodecTests.Int32(1)); // one child
        }

        chunks.Add([0x00]); // layout Xy
        chunks.Add([0x01]); // Point
        chunks.Add([0x00]); // no CRS
        chunks.Add([0x00]); // hasCoordinate = 0 → empty
        return GeometryCodecTests.Payload(chunks.ToArray());
    }

    private static void AssertFailed(byte[] bytes, string expectedFragment)
    {
        Assert.False(GeometryCodec.TryDecode(bytes, out var geometry, out var error));
        Assert.Null(geometry);
        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error, StringComparison.Ordinal);
    }
}
