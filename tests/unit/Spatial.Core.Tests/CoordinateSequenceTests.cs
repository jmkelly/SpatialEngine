using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

public class PackedCoordinateSequenceTests
{
    [Fact]
    public void Constructor_copies_values()
    {
        var values = new[] { 1.0, 2.0, 3.0, 4.0 };
        var sequence = new PackedCoordinateSequence(values, CoordinateLayout.Xy);
        values[0] = 999;
        Assert.Equal(1.0, sequence.GetOrdinate(0, Ordinate.X));
        Assert.Equal(2, sequence.Count);
        Assert.Equal(CoordinateLayout.Xy, sequence.Layout);
    }

    [Theory]
    [InlineData(CoordinateLayout.Xy, 3)] // 3 values, stride 2
    [InlineData(CoordinateLayout.Xyz, 4)] // 4 values, stride 3
    public void Constructor_rejects_non_multiple_lengths(CoordinateLayout layout, int length)
    {
        var exception = Assert.Throws<ArgumentException>(() => new PackedCoordinateSequence(new double[length], layout));
        Assert.Contains("not a multiple of stride", exception.Message);
    }

    [Fact]
    public void FromCoordinates_infers_layout_and_pads_absent_ordinates_with_nan()
    {
        var sequence = PackedCoordinateSequence.FromCoordinates([
            new Coordinate(1, 2, Z: 5),
            new Coordinate(3, 4),
        ]);

        Assert.Equal(CoordinateLayout.Xyz, sequence.Layout);
        Assert.Equal(2, sequence.Count);
        Assert.Equal(5, sequence.GetOrdinate(0, Ordinate.Z));
        Assert.Equal(double.NaN, sequence.GetOrdinate(1, Ordinate.Z));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.M));
    }

    [Fact]
    public void FromCoordinates_with_explicit_layout_packs_m_and_rejects_absent_z()
    {
        var sequence = PackedCoordinateSequence.FromCoordinates(
            [new Coordinate(1, 2, M: 6), new Coordinate(3, 4, M: 7)],
            CoordinateLayout.Xym);

        Assert.Equal(CoordinateLayout.Xym, sequence.Layout);
        Assert.Equal(6, sequence.GetOrdinate(0, Ordinate.M));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.Z));

        var exception = Assert.Throws<ArgumentException>(() =>
            PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, Z: 5)], CoordinateLayout.Xy));
        Assert.Contains("does not store Z", exception.Message);
    }

    [Fact]
    public void GetOrdinate_validates_index_and_ordinate()
    {
        var sequence = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var indexException = Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(2, Ordinate.X));
        Assert.Contains("out of range", indexException.Message);
        var ordinateException = Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, (Ordinate)99));
        Assert.Contains("Unknown ordinate", ordinateException.Message);
    }

    [Fact]
    public void GetCoordinate_surfaces_layout()
    {
        var xyz = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, Z: 5), new Coordinate(3, 4)]);
        ICoordinateSequence xyzView = xyz;
        Assert.Equal(new Coordinate(1, 2, Z: 5), xyzView.GetCoordinate(0));
        Assert.Equal(new Coordinate(3, 4, Z: double.NaN), xyzView.GetCoordinate(1));

        var xy = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2)]);
        ICoordinateSequence xyView = xy;
        Assert.Equal(new Coordinate(1, 2, Z: null, M: null), xyView.GetCoordinate(0));
    }

    [Fact]
    public void Default_struct_is_an_empty_sequence()
    {
        var sequence = default(PackedCoordinateSequence);
        Assert.Equal(0, sequence.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.X));
    }
}

public class ArrayCoordinateSequenceTests
{
    [Fact]
    public void Constructor_copies_coordinates_and_normalises_to_layout()
    {
        var coordinates = new[] { new Coordinate(1, 2), new Coordinate(3, 4) };
        var sequence = new ArrayCoordinateSequence(coordinates, CoordinateLayout.Xyz);
        coordinates[0] = new Coordinate(99, 99);

        Assert.Equal(2, sequence.Count);
        Assert.Equal(CoordinateLayout.Xyz, sequence.Layout);
        Assert.Equal(1, sequence.GetOrdinate(0, Ordinate.X));
        Assert.Equal(double.NaN, sequence.GetOrdinate(0, Ordinate.Z));
    }

    [Fact]
    public void Non_null_ordinate_absent_from_layout_is_rejected()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            new ArrayCoordinateSequence([new Coordinate(1, 2, Z: 5)], CoordinateLayout.Xy));
        Assert.Contains("does not store Z", exception.Message);
    }

    [Fact]
    public void GetOrdinate_validates_index_and_ordinate()
    {
        var sequence = new ArrayCoordinateSequence([new Coordinate(1, 2, M: 6)], CoordinateLayout.Xym);
        Assert.Equal(6, sequence.GetOrdinate(0, Ordinate.M));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, Ordinate.Z));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(1, Ordinate.X));
        Assert.Throws<ArgumentOutOfRangeException>(() => sequence.GetOrdinate(0, (Ordinate)99));
    }

    [Fact]
    public void GetCoordinate_surfaces_nan_for_null_ordinates_in_layout()
    {
        var sequence = new ArrayCoordinateSequence([new Coordinate(1, 2, Z: null)], CoordinateLayout.Xyz);
        ICoordinateSequence view = sequence;
        Assert.Equal(new Coordinate(1, 2, Z: double.NaN), view.GetCoordinate(0));
    }
}

public class CoordinateSequenceComparerTests
{
    [Fact]
    public void Packed_and_array_backings_compare_equal()
    {
        var packed = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, Z: 5), new Coordinate(3, 4, Z: double.NaN)]);
        var array = new ArrayCoordinateSequence([new Coordinate(1, 2, Z: 5), new Coordinate(3, 4)], CoordinateLayout.Xyz);

        Assert.True(CoordinateSequenceComparer.Equals(packed, array));
        Assert.Equal(packed.GetHashCode(), array.GetHashCode());
        Assert.True(CoordinateSequenceComparer.ValuesEqual(packed, array));
    }

    [Fact]
    public void Differing_layouts_or_counts_are_unequal()
    {
        var xyz = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, Z: 5)]);
        var xy = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2)]);
        Assert.False(CoordinateSequenceComparer.Equals(xy, xyz)); // layouts differ (Xy vs Xyz)

        var shorter = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2)]);
        var longer = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(3, 4)]);
        Assert.False(CoordinateSequenceComparer.Equals(shorter, longer));
    }

    [Fact]
    public void Same_layout_and_count_with_differing_ordinates_are_unequal()
    {
        var a = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(3, 4)]);
        var b = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(9, 4)]);
        var c = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(3, 9)]);
        Assert.False(CoordinateSequenceComparer.Equals(a, b));
        Assert.False(CoordinateSequenceComparer.Equals(a, c));
        Assert.False(CoordinateSequenceComparer.ValuesEqual(a, b));
        Assert.False(CoordinateSequenceComparer.ValuesEqual(a, c));
    }

    [Fact]
    public void Nan_values_compare_equal()
    {
        var a = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, Z: double.NaN)], CoordinateLayout.Xyz);
        var b = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2, Z: double.NaN)], CoordinateLayout.Xyz);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Null_handling()
    {
        Assert.True(CoordinateSequenceComparer.Equals(null, null));
        Assert.False(CoordinateSequenceComparer.Equals(null, PackedCoordinateSequence.FromCoordinates([])));
        Assert.Throws<ArgumentNullException>(() => CoordinateSequenceComparer.ValuesEqual(null!, PackedCoordinateSequence.FromCoordinates([])));
    }

    [Fact]
    public void AsEnumerable_streams_coordinates()
    {
        var sequence = PackedCoordinateSequence.FromCoordinates([new Coordinate(1, 2), new Coordinate(3, 4)]);
        Assert.Equal([new Coordinate(1, 2), new Coordinate(3, 4)], sequence.AsEnumerable());
    }
}

public class CoordinateSequenceAllocationTests
{
    [Fact]
    public void Packed_sequence_allocates_only_the_double_buffer()
    {
        const int coordinateCount = 200_000;
        var coordinates = new Coordinate[coordinateCount];
        for (var i = 0; i < coordinateCount; i++)
        {
            coordinates[i] = new Coordinate(i, i * 2, Z: i / 3.0);
        }

        // Warm up construction and JIT before measuring.
        PackedCoordinateSequence.FromCoordinates(coordinates.AsSpan(0, 10));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sequence = PackedCoordinateSequence.FromCoordinates(coordinates);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        var expectedBuffer = coordinateCount * 3 * sizeof(double);
        Assert.True(
            allocated <= expectedBuffer + 16_384,
            $"Expected at most the {expectedBuffer}-byte double buffer plus slack, but {allocated} bytes were allocated.");
        Assert.Equal(coordinateCount, sequence.Count);

        // Warm up ordinate access, then assert reads allocate nothing per coordinate.
        _ = sequence.GetOrdinate(0, Ordinate.X);
        before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0.0;
        for (var i = 0; i < sequence.Count; i++)
        {
            sum += sequence.GetOrdinate(i, Ordinate.X) + sequence.GetOrdinate(i, Ordinate.Z);
        }

        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 4_096, $"Ordinate reads allocated {allocated} bytes; expected none (one heap object per coordinate would cost far more).");
        Assert.True(sum > 0);
    }
}
