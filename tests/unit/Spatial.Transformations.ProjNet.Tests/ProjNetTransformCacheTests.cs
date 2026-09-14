using Spatial.Core.Geometry;

namespace Spatial.Transformations.ProjNet.Tests;

/// <summary>
/// Hot-path pin (T-087): single-point transforms must not rebuild the
/// ProjNet math transform per call. The per-(source,target) math transform
/// is cached, so repeating a pair reuses one instance while results stay
/// identical across pairs. These tests use CRS pairs no other test class
/// transforms through (32610, 32612, 25833, 4277→27700), because xUnit runs
/// classes in parallel against the shared static cache.
/// </summary>
public sealed class ProjNetTransformCacheTests
{
    [Fact]
    public void Repeated_transforms_reuse_the_cached_math_transform()
    {
        var service = new ProjNetTransforms();
        var point = GeometryFactory.CreatePoint(-122.33, 47.61, CoordinateReference.Epsg(4326));

        var first = (Point)service.Transform(point, source: null, target: "EPSG:32610");
        Assert.True(ProjNetTransforms.TryGetCachedMathTransform(4326, 32610, out var cachedBefore));

        var second = (Point)service.Transform(point, source: null, target: "EPSG:32610");
        Assert.True(ProjNetTransforms.TryGetCachedMathTransform(4326, 32610, out var cachedAfter));

        Assert.Same(cachedBefore, cachedAfter);
        Assert.Equal(first.X!.Value, second.X!.Value);
        Assert.Equal(first.Y!.Value, second.Y!.Value);
    }

    [Fact]
    public void Distinct_pairs_cache_independently_without_changing_results()
    {
        var service = new ProjNetTransforms();
        var point = GeometryFactory.CreatePoint(-114.0, 47.61, CoordinateReference.Epsg(4326));

        var zone12 = (Point)service.Transform(point, source: null, target: "EPSG:32612");
        var etrs33 = (Point)service.Transform(point, source: null, target: "EPSG:25833");

        Assert.NotEqual(zone12.X!.Value, etrs33.X!.Value);
        Assert.True(ProjNetTransforms.TryGetCachedMathTransform(4326, 32612, out var zone12Math));
        Assert.True(ProjNetTransforms.TryGetCachedMathTransform(4326, 25833, out var etrs33Math));
        Assert.NotSame(zone12Math, etrs33Math);
    }

    [Fact]
    public void Cached_transforms_stay_safe_under_parallel_use()
    {
        var service = new ProjNetTransforms();
        var point = GeometryFactory.CreatePoint(-1.5, 52.5, CoordinateReference.Epsg(4277));
        var expected = (Point)service.Transform(point, source: null, target: "EPSG:27700");

        Parallel.For(0, 64, _ =>
        {
            var actual = (Point)service.Transform(point, source: null, target: "EPSG:27700");
            Assert.Equal(expected.X!.Value, actual.X!.Value);
            Assert.Equal(expected.Y!.Value, actual.Y!.Value);
        });
    }
}
