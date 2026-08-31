using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;

namespace Spatial.Core.Tests;

/// <summary>
/// Seeded pseudo-random round trips: a deterministic generator builds
/// arbitrarily nested geometries with mixed layouts, CRSs, empties and NaN
/// ordinates; every generated value must survive an encode → decode →
/// re-encode cycle byte-for-byte. Seeds are fixed so the suite is
/// reproducible in CI.
/// </summary>
public class GeometryCodecPropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(42)]
    [InlineData(1_337)]
    [InlineData(20_260_214)]
    [InlineData(987_654_321)]
    public void Random_geometries_round_trip_exactly(int seed)
    {
        var random = new Random(seed);
        for (var iteration = 0; iteration < 250; iteration++)
        {
            var geometry = RandomGeometry(random, depth: 3);
            var bytes = GeometryCodec.Encode(geometry);
            var decoded = GeometryCodec.Decode(bytes);

            Assert.True(
                GeometryComparer.Equals(geometry, decoded),
                $"Seed {seed} iteration {iteration} failed: original {Describe(geometry)} decoded as {Describe(decoded)}.");
            Assert.Equal(geometry.GetHashCode(), decoded.GetHashCode());
            Assert.Equal(bytes, GeometryCodec.Encode(decoded));
        }
    }

    private static IGeometry RandomGeometry(Random random, int depth)
    {
        var crs = MaybeCrs(random);
        if (depth == 0 || random.Next(4) == 0)
        {
            return random.Next(2) == 0 ? RandomPoint(random, crs) : RandomLineString(random, crs);
        }

        return random.Next(4) switch
        {
            0 => new Polygon(RandomLineString(random, null), RandomRings(random), crs),
            1 => new MultiPoint(RandomParts(random, () => RandomPoint(random, MaybeCrs(random))), crs),
            2 => new MultiLineString(RandomParts(random, () => RandomLineString(random, MaybeCrs(random))), crs),
            _ => new GeometryCollection(RandomParts(random, () => RandomGeometry(random, depth - 1)), crs),
        };
    }

    private static Point RandomPoint(Random random, CoordinateReference? crs)
    {
        var (hasZ, hasM) = (random.Next(2) == 0, random.Next(2) == 0);
        if (random.Next(5) == 0)
        {
            return GeometryFactory.CreateEmptyPoint(crs);
        }

        return new Point(new Coordinate(
            RandomDouble(random),
            RandomDouble(random),
            hasZ ? RandomDouble(random) : null,
            hasM ? RandomDouble(random) : null), crs);
    }

    private static LineString RandomLineString(Random random, CoordinateReference? crs)
    {
        var (hasZ, hasM) = (random.Next(2) == 0, random.Next(2) == 0);
        var layout = (hasZ, hasM) switch
        {
            (true, true) => CoordinateLayout.Xyzm,
            (true, false) => CoordinateLayout.Xyz,
            (false, true) => CoordinateLayout.Xym,
            (false, false) => CoordinateLayout.Xy,
        };

        var coordinates = new Coordinate[random.Next(6)];
        for (var i = 0; i < coordinates.Length; i++)
        {
            coordinates[i] = new Coordinate(
                RandomDouble(random),
                RandomDouble(random),
                hasZ ? RandomDouble(random) : null,
                hasM ? RandomDouble(random) : null);
        }

        return GeometryFactory.CreateLineString(coordinates, layout, crs);
    }

    private static LineString[] RandomRings(Random random) =>
        RandomParts(random, () => RandomLineString(random, MaybeCrs(random)));

    private static T[] RandomParts<T>(Random random, Func<T> factory)
        where T : IGeometry
    {
        var parts = new T[random.Next(4)];
        for (var i = 0; i < parts.Length; i++)
        {
            parts[i] = factory();
        }

        return parts;
    }

    private static CoordinateReference? MaybeCrs(Random random) =>
        random.Next(4) == 0 ? CoordinateReference.Epsg(random.Next(1024, 10_000)) : null;

    private static double RandomDouble(Random random) =>
        random.Next(3) == 0 ? double.NaN : (random.NextDouble() * 360) - 180;

    private static string Describe(IGeometry geometry)
    {
        var types = string.Join(",", geometry.DepthFirst().Select(g => g.Type.ToString()));
        return $"{geometry.GetType().Name}[{types}]";
    }
}
