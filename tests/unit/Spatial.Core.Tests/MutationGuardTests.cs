using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// Guard-clause assertions that pin behaviour a mutation pass can flip in the
/// composite geometry model and the canonical codec: null-child rejection,
/// actionable byte-offset errors and exact exception messages.
/// </summary>
public class MutationGuardTests
{
    [Fact]
    public void Composite_constructors_reject_null_children()
    {
        Assert.Throws<ArgumentNullException>(() => new MultiPoint((IEnumerable<Point>)null!));
        Assert.Throws<ArgumentNullException>(() => new MultiLineString((IEnumerable<LineString>)null!));
        Assert.Throws<ArgumentNullException>(() => new MultiPolygon((IEnumerable<Polygon>)null!));
        Assert.Throws<ArgumentNullException>(() => new GeometryCollection((IEnumerable<IGeometry>)null!));
    }

    [Fact]
    public void GeometryComparer_get_hash_code_reports_unknown_types()
    {
        var exception = Assert.Throws<ArgumentException>(() => GeometryComparer.GetHashCode(new ForeignGeometry()));
        Assert.Contains("Unknown geometry type '200'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Codec_element_count_errors_are_actionable()
    {
        // Negative line-string count.
        var negative = GeometryCodecTests.Payload([0x00], [0x02], [0x00], GeometryCodecTests.Int32(-1));
        Assert.False(GeometryCodec.TryDecode(negative, out _, out var negativeError));
        Assert.Contains("must be non-negative", negativeError, StringComparison.Ordinal);

        // More elements than the input can possibly hold (each element needs >= 3 bytes).
        var huge = GeometryCodecTests.Payload([0x00], [0x02], [0x00], GeometryCodecTests.Int32(1_000_000));
        Assert.False(GeometryCodec.TryDecode(huge, out _, out var hugeError));
        Assert.Contains("each element needs at least 3 bytes", hugeError, StringComparison.Ordinal);

        // Decode nesting limit carries the limit value in its message.
        var nested = GeometryCodecTests.BuildNestedCollection(levels: GeometryCodec.MaxNestingDepth + 2);
        Assert.False(GeometryCodec.TryDecode(nested, out _, out var nestingError));
        Assert.Contains($"nesting exceeds the canonical format limit of {GeometryCodec.MaxNestingDepth} levels", nestingError, StringComparison.Ordinal);
    }

    [Fact]
    public void Sequence_guard_messages_pin_offsets()
    {
        var packed = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var packedIndex = Assert.Throws<ArgumentOutOfRangeException>(() => packed.GetOrdinate(2, Ordinate.X));
        Assert.Contains("Index 2 is out of range for a sequence of 2 coordinates", packedIndex.Message, StringComparison.Ordinal);
        var packedOrdinate = Assert.Throws<ArgumentOutOfRangeException>(() => packed.GetOrdinate(0, (Ordinate)99));
        Assert.Contains("Unknown ordinate '99'", packedOrdinate.Message, StringComparison.Ordinal);

        var array = new ArrayCoordinateSequence([new Coordinate(1, 2)], CoordinateLayout.Xym);
        var arrayIndex = Assert.Throws<ArgumentOutOfRangeException>(() => array.GetOrdinate(1, Ordinate.X));
        Assert.Contains("Index 1 is out of range for a sequence of 1 coordinates", arrayIndex.Message, StringComparison.Ordinal);
        var arrayOrdinate = Assert.Throws<ArgumentOutOfRangeException>(() => array.GetOrdinate(0, Ordinate.Z));
        Assert.Contains("Layout Xym does not store Z", arrayOrdinate.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_sequence_rejects_ordoinates_the_layout_does_not_store_with_message()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ArrayCoordinateSequence([new Coordinate(1, 2, M: 7)], CoordinateLayout.Xy));
        Assert.Contains("does not store M", exception.Message, StringComparison.Ordinal);
    }

    private sealed class ForeignGeometry : IGeometry
    {
        public GeometryType Type => (GeometryType)200;
        public CoordinateLayout Layout => CoordinateLayout.Xy;
        public CoordinateReference? CoordinateReference => null;
        public bool IsEmpty => true;
        public int CoordinateCount => 0;
        public Envelope? Envelope => null;
    }
}
