using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

public class EnvelopeTests
{
    [Fact]
    public void Empty_is_empty_and_has_zero_extent()
    {
        Assert.True(Envelope.Empty.IsEmpty);
        Assert.Equal(0, Envelope.Empty.Width);
        Assert.Equal(0, Envelope.Empty.Height);
    }

    [Fact]
    public void Empty_contains_and_intersects_nothing()
    {
        Assert.False(Envelope.Empty.Contains(0, 0));
        Assert.False(Envelope.Empty.Intersects(new Envelope(0, 0, 1, 1)));
        Assert.False(Envelope.Empty.Contains(new Envelope(0, 0, 1, 1)));
    }

    [Theory]
    [InlineData(3, 1, 1, 3)] // min exceeds max
    [InlineData(1, 3, 3, 1)] // y min exceeds y max
    public void Invalid_bounds_throw(double minX, double minY, double maxX, double maxY) =>
        Assert.Throws<ArgumentException>(() => new Envelope(minX, minY, maxX, maxY));

    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.NaN, 1, 1)]
    [InlineData(0, 0, double.PositiveInfinity, 1)]
    [InlineData(double.NegativeInfinity, 0, 1, 1)]
    [InlineData(0, 0, 1, double.NaN)]
    public void Non_finite_bounds_throw(double minX, double minY, double maxX, double maxY) =>
        Assert.Throws<ArgumentException>(() => new Envelope(minX, minY, maxX, maxY));

    [Fact]
    public void FromCoordinates_skips_nan_and_returns_empty_for_none()
    {
        Assert.Equal(Envelope.Empty, Envelope.FromCoordinates([]));
        Assert.Equal(Envelope.Empty, Envelope.FromCoordinates([new Coordinate(double.NaN, 5), new Coordinate(2, double.NaN)]));
    }

    [Fact]
    public void FromCoordinates_computes_minima_and_maxima()
    {
        var envelope = Envelope.FromCoordinates([
            new Coordinate(5, -2),
            new Coordinate(-3, 4),
            new Coordinate(double.NaN, 99),
            new Coordinate(1, 1, Z: 7),
        ]);

        Assert.False(envelope.IsEmpty);
        Assert.Equal(-3, envelope.MinX);
        Assert.Equal(-2, envelope.MinY);
        Assert.Equal(5, envelope.MaxX);
        Assert.Equal(4, envelope.MaxY);
    }

    [Fact]
    public void FromCoordinates_ignores_z_and_m()
    {
        var envelope = Envelope.FromCoordinates([new Coordinate(1, 2, Z: 100, M: 200)]);
        Assert.Equal(new Envelope(1, 2, 1, 2), envelope);
    }

    [Fact]
    public void FromSequence_matches_FromCoordinates()
    {
        var sequence = PackedCoordinateSequence.FromCoordinates([
            new Coordinate(0, 0),
            new Coordinate(10, 20),
        ]);
        Assert.Equal(Envelope.FromCoordinates([new Coordinate(0, 0), new Coordinate(10, 20)]), Envelope.FromSequence(sequence));
    }

    [Fact]
    public void FromSequence_returns_empty_for_no_or_all_skipped_coordinates()
    {
        Assert.Equal(Envelope.Empty, Envelope.FromSequence(PackedCoordinateSequence.FromCoordinates([])));
        Assert.Equal(Envelope.Empty, Envelope.FromSequence(PackedCoordinateSequence.FromCoordinates([
            new Coordinate(double.NaN, 5),
            new Coordinate(2, double.NaN),
        ])));
    }

    [Fact]
    public void FromSequence_rejects_null() =>
        Assert.Throws<ArgumentNullException>(() => Envelope.FromSequence(null!));

    [Fact]
    public void Union_expands_to_cover_both()
    {
        var left = new Envelope(0, 0, 5, 5);
        var right = new Envelope(3, -2, 8, 4);
        Assert.Equal(new Envelope(0, -2, 8, 5), left.Union(right));
        Assert.Equal(left.Union(right), right.Union(left));
    }

    [Fact]
    public void Union_with_empty_is_identity()
    {
        var envelope = new Envelope(1, 2, 3, 4);
        Assert.Equal(envelope, envelope.Union(Envelope.Empty));
        Assert.Equal(envelope, Envelope.Empty.Union(envelope));
        Assert.Equal(Envelope.Empty, Envelope.Empty.Union(Envelope.Empty));
    }

    [Theory]
    [InlineData(4, 4, true)] // inside
    [InlineData(5, 5, true)] // on the corner boundary
    [InlineData(0, 5, true)] // on the edge boundary
    [InlineData(5.1, 5, false)]
    [InlineData(2, -0.1, false)]
    public void Contains_point(double x, double y, bool expected)
    {
        var envelope = new Envelope(0, 0, 5, 5);
        Assert.Equal(expected, envelope.Contains(x, y));
        Assert.Equal(expected, envelope.Contains(new Coordinate(x, y)));
    }

    [Fact]
    public void Contains_envelope()
    {
        var outer = new Envelope(0, 0, 10, 10);
        Assert.True(outer.Contains(new Envelope(1, 1, 9, 9)));
        Assert.True(outer.Contains(new Envelope(0, 0, 10, 10)));
        Assert.False(outer.Contains(new Envelope(-1, 0, 5, 5)));
        Assert.False(outer.Contains(Envelope.Empty));
    }

    [Theory]
    [InlineData(1, 1, 3, 3, true)] // overlap
    [InlineData(5, 0, 8, 5, true)] // touching boundary
    [InlineData(-3, -3, -1, -1, false)] // disjoint
    [InlineData(1, -4, 4, 0, true)] // touching in Y (other._maxY == this._minY)
    [InlineData(-4, 2, 0, 3, true)] // touching in X (other._maxX == this._minX)
    [InlineData(1, 5, 4, 8, true)] // touching in Y (other._minY == this._maxY)
    [InlineData(5, -1, 8, 2, true)] // touching in X (other._minX == this._maxX)
    [InlineData(6, -5, 10, 5, false)] // X disjoint to the right, Y overlaps
    [InlineData(-8, 2, -1, 8, false)] // X disjoint to the left, Y overlaps
    public void Intersects_overlap_touch_and_disjoint(double minX, double minY, double maxX, double maxY, bool expected)
    {
        var envelope = new Envelope(0, 0, 5, 5);
        Assert.Equal(expected, envelope.Intersects(new Envelope(minX, minY, maxX, maxY)));
    }

    [Fact]
    public void Contains_outside_points_are_false()
    {
        var envelope = new Envelope(0, 0, 5, 5);
        Assert.False(envelope.Contains(5.1, 5));
        Assert.False(envelope.Contains(2, -0.1));
        Assert.False(envelope.Contains(-0.1, 2));
        Assert.False(envelope.Contains(2, 5.1));
    }

    [Fact]
    public void Intersects_empty_is_false()
    {
        var envelope = new Envelope(0, 0, 5, 5);
        Assert.False(envelope.Intersects(Envelope.Empty));
    }

    [Fact]
    public void Extent_and_center()
    {
        var envelope = new Envelope(-2, 10, 6, 20);
        Assert.Equal(8, envelope.Width);
        Assert.Equal(10, envelope.Height);
        Assert.Equal(2, envelope.CenterX);
        Assert.Equal(15, envelope.CenterY);
    }

    [Fact]
    public void Degenerate_point_envelope()
    {
        var envelope = new Envelope(3, 4, 3, 4);
        Assert.False(envelope.IsEmpty);
        Assert.Equal(0, envelope.Width);
        Assert.True(envelope.Contains(3, 4));
    }

    [Fact]
    public void Equality_and_hash()
    {
        var a = new Envelope(0, 0, 5, 5);
        var b = new Envelope(0, 0, 5, 5);
        var c = new Envelope(0, 0, 6, 5);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, new Envelope(0, 1, 5, 5)); // differing minY
        Assert.NotEqual(a, new Envelope(1, 0, 5, 5)); // differing minX
        Assert.Equal(Envelope.Empty, Envelope.FromCoordinates([]));
        Assert.Equal(Envelope.Empty.GetHashCode(), Envelope.FromCoordinates([]).GetHashCode());
    }

    [Fact]
    public void Default_value_is_an_origin_degenerate_envelope()
    {
        var envelope = default(Envelope);
        Assert.False(envelope.IsEmpty);
        Assert.True(envelope.Contains(0, 0));
    }
}
