using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// Edge-case and boundary assertions for <see cref="Envelope"/> that pin the
/// exact behaviours a mutation pass can flip: message content, each boundary
/// clause of Contains/Intersects, partial-equality components and ToString.
/// </summary>
public class EnvelopeMutationTests
{
    [Theory]
    [InlineData(double.NaN, 0, 1, 1)]
    [InlineData(0, double.NaN, 1, 1)]
    [InlineData(0, 0, double.NaN, 1)]
    [InlineData(0, 0, 1, double.NaN)]
    [InlineData(double.PositiveInfinity, 0, 1, 1)]
    [InlineData(0, 0, double.PositiveInfinity, 1)]
    [InlineData(0, 0, 1, double.NegativeInfinity)]
    public void Non_finite_bounds_throw_the_finite_message(double minX, double minY, double maxX, double maxY)
    {
        var exception = Assert.Throws<ArgumentException>(() => new Envelope(minX, minY, maxX, maxY));
        Assert.Contains("must be finite", exception.Message);
        Assert.Contains("Envelope bounds must be finite", exception.Message);
    }

    [Theory]
    [InlineData(3, 1, 1, 3)] // minX exceeds maxX
    [InlineData(1, 3, 3, 1)] // minY exceeds maxY
    [InlineData(4, 4, 1, 0)] // both axes inverted
    public void Inverted_bounds_throw_the_exceeds_message(double minX, double minY, double maxX, double maxY)
    {
        var exception = Assert.Throws<ArgumentException>(() => new Envelope(minX, minY, maxX, maxY));
        Assert.Contains($"minimum ({minX}, {minY}) exceeds maximum ({maxX}, {maxY})", exception.Message);
    }

    [Theory]
    [InlineData(-1, 2)] // left of the envelope
    [InlineData(2, -1)] // below the envelope
    [InlineData(2, 6)] // above the envelope
    public void Contains_point_outside_each_axis_is_false(double x, double y)
    {
        var envelope = new Envelope(0, 0, 5, 5);
        Assert.False(envelope.Contains(x, y));
        Assert.False(envelope.Contains(new Coordinate(x, y)));
    }

    [Fact]
    public void Contains_envelope_each_clause_checked()
    {
        var outer = new Envelope(0, 0, 10, 10);
        // Inside: every comparison must hold.
        Assert.True(outer.Contains(new Envelope(0, 0, 10, 10)));
        // Touching the outer minimum edge is allowed (>= / <=).
        Assert.True(outer.Contains(new Envelope(0, 5, 5, 9)));
        Assert.True(outer.Contains(new Envelope(5, 0, 9, 5)));
        // Hanging off in each direction is not contained.
        Assert.False(outer.Contains(new Envelope(-1, 5, 5, 9)));  // sticks out on minX
        Assert.False(outer.Contains(new Envelope(5, -1, 9, 5)));  // sticks out on minY
        Assert.False(outer.Contains(new Envelope(5, 5, 11, 9)));  // sticks out on maxX
        Assert.False(outer.Contains(new Envelope(5, 5, 9, 11)));  // sticks out on maxY
        Assert.False(outer.Contains(Envelope.Empty));
    }

    [Theory]
    [InlineData(-3, 1, -1, 4, false)] // disjoint in X, overlapping in Y
    [InlineData(1, -3, 4, -1, false)] // overlapping in X, disjoint in Y
    [InlineData(6, 1, 8, 4, false)]   // entirely to the right, overlapping in Y
    [InlineData(1, 6, 4, 8, false)]   // entirely above, overlapping in X
    [InlineData(-3, 0, 0, 5, true)]   // touches on this.minX
    [InlineData(5, 0, 8, 5, true)]    // touches on this.maxX
    [InlineData(0, -3, 5, 0, true)]   // touches on this.minY
    [InlineData(1, 5, 3, 8, true)]    // touches on this.maxY
    [InlineData(-3, -3, -1, -1, false)] // fully disjoint
    public void Intersects_boundary_and_axis_clauses(double minX, double minY, double maxX, double maxY, bool expected)
    {
        var envelope = new Envelope(0, 0, 5, 5);
        Assert.Equal(expected, envelope.Intersects(new Envelope(minX, minY, maxX, maxY)));
        // Symmetry: intersects is commutative.
        Assert.Equal(expected, new Envelope(minX, minY, maxX, maxY).Intersects(envelope));
    }

    [Fact]
    public void Equality_differs_on_each_single_component()
    {
        var baseEnvelope = new Envelope(1, 2, 3, 4);
        Assert.Equal(baseEnvelope, new Envelope(1, 2, 3, 4));
        Assert.NotEqual(baseEnvelope, new Envelope(0, 2, 3, 4)); // minX differs
        Assert.NotEqual(baseEnvelope, new Envelope(1, 0, 3, 4)); // minY differs
        Assert.NotEqual(baseEnvelope, new Envelope(1, 2, 4, 4)); // maxX differs
        Assert.NotEqual(baseEnvelope, new Envelope(1, 2, 3, 5)); // maxY differs
        Assert.NotEqual(baseEnvelope, (object)new Envelope(0, 0, 0, 0));
        Assert.False(baseEnvelope.Equals((object)"not an envelope"));
    }

    [Fact]
    public void ToString_renders_empty_and_populated()
    {
        Assert.Equal("Envelope [empty]", Envelope.Empty.ToString());
        Assert.Equal("Envelope [0, 0] to [5, 5]", new Envelope(0, 0, 5, 5).ToString());
    }

    [Fact]
    public void FromSequence_surfaces_values_and_empty_same_as_FromCoordinates()
    {
        var coordinates = new Coordinate[] { new(5, -2), new(-3, 4), new(double.NaN, 99), new(1, 1, Z: 7) };
        var sequence = PackedCoordinateSequence.FromCoordinates(coordinates);
        Assert.Equal(Envelope.FromCoordinates(coordinates), Envelope.FromSequence(sequence));
        Assert.Equal(Envelope.Empty, Envelope.FromSequence(PackedCoordinateSequence.FromCoordinates([])));
    }
}
