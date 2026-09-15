using System.Globalization;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// Quality-loop pass 3: pin <c>MapGenerateRenderer.Format</c> (CRAP 23.8,
/// cx 7 — pure value map, so tests are the cheap lever) and
/// <c>FeatureQueryEngine.Overlaps</c> (CRAP 21.75, cx 5) with real
/// Core polygons through the NTS operations (no mocks).
/// </summary>
public sealed class QualityLoopPass3Tests
{
    private static readonly NtsGeometryOperations Operations = new();

    [Theory]
    [InlineData(42L, "42")]
    [InlineData(-7L, "-7")]
    public void Format_renders_integers_invariantly(long number, string expected) =>
        Assert.Equal(expected, MapGenerateRenderer.Format(AttributeValue.FromInt64(number)));

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(-0.25, "-0.25")]
    public void Format_renders_doubles_invariantly(double number, string expected) =>
        Assert.Equal(expected, MapGenerateRenderer.Format(AttributeValue.FromDouble(number)));

    [Fact]
    public void Format_renders_strings_verbatim() =>
        Assert.Equal("abc", MapGenerateRenderer.Format(AttributeValue.FromString("abc")));

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void Format_renders_booleans_lowercase(bool flag, string expected) =>
        Assert.Equal(expected, MapGenerateRenderer.Format(AttributeValue.FromBoolean(flag)));

    [Fact]
    public void Format_renders_timestamps_as_unix_milliseconds()
    {
        var value = AttributeValue.FromDateTimeOffset(
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("1704067200000", MapGenerateRenderer.Format(value));
    }

    [Fact]
    public void Format_renders_guids()
    {
        var guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef", CultureInfo.InvariantCulture);
        Assert.Equal(guid.ToString(), MapGenerateRenderer.Format(AttributeValue.FromGuid(guid)));
    }

    [Fact]
    public void Format_renders_null_and_geometry_as_empty()
    {
        Assert.Equal(string.Empty, MapGenerateRenderer.Format(AttributeValue.Null));
        Assert.Equal(
            string.Empty,
            MapGenerateRenderer.Format(AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))));
    }

    [Fact]
    public void Overlaps_accepts_partially_overlapping_polygons() =>
        Assert.True(Overlaps(Square(0, 0, 2, 2), Square(1, 1, 3, 3)));

    [Fact]
    public void Overlaps_rejects_disjoint_polygons() =>
        Assert.False(Overlaps(Square(0, 0, 1, 1), Square(2, 2, 3, 3)));

    [Fact]
    public void Overlaps_rejects_edge_touching_polygons() =>
        Assert.False(Overlaps(Square(0, 0, 2, 2), Square(2, 0, 4, 2)));

    [Fact]
    public void Overlaps_rejects_contained_polygons() =>
        Assert.False(Overlaps(Square(0, 0, 4, 4), Square(1, 1, 2, 2)));

    [Fact]
    public void Overlaps_rejects_identical_polygons() =>
        Assert.False(Overlaps(Square(0, 0, 2, 2), Square(0, 0, 2, 2)));

    [Fact]
    public void Overlaps_rejects_mixed_dimensions()
    {
        Assert.False(Overlaps(GeometryFactory.CreatePoint(1, 1), Square(0, 0, 2, 2)));
        Assert.False(Overlaps(
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(3, 3)]),
            Square(0, 0, 2, 2)));
    }

    private static bool Overlaps(IGeometry left, Polygon right) =>
        FeatureSpatialMatcher.Overlaps(
            left, right, left.Envelope!.Value, right.Envelope!.Value, Operations, CancellationToken.None);

    private static Polygon Square(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
        [
            new Coordinate(minX, minY),
            new Coordinate(maxX, minY),
            new Coordinate(maxX, maxY),
            new Coordinate(minX, maxY),
            new Coordinate(minX, minY),
        ]);
}
