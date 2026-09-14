using BenchmarkDotNet.Attributes;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;
using Spatial.Core.Geometry;
using Spatial.Transformations.ProjNet;

namespace Spatial.Performance;

/// <summary>
/// Point transform micro (T-076): the engine verb
/// (<c>ProjNetTransforms.Transform</c>, EPSG:4326→EPSG:3857, including CRS
/// lookup, stamp validation and per-coordinate adaptation) against the same
/// math executed straight on a cached ProjNet <c>MathTransform</c>.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class TransformPointBenchmarks
{
    private readonly ProjNetTransforms _transforms = new();

    private IGeometry _point = null!;
    private MathTransform _rawMath = null!;
    private double _rawX;
    private double _rawY;

    [GlobalSetup]
    public void Setup()
    {
        _rawX = -122.4194;
        _rawY = 37.7749;
        _point = GeometryFactory.CreatePoint(_rawX, _rawY, CoordinateReference.Epsg(4326));
        _rawMath = new CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, ProjectedCoordinateSystem.WebMercator)
            .MathTransform;
    }

    [Benchmark(Description = "Transform point via ProjNetTransforms (4326->3857)")]
    public object TransformPoint_Engine() => _transforms.Transform(_point, source: null, target: "EPSG:3857");

    [Benchmark(Description = "Transform point raw ProjNet (WGS84->WebMercator)", Baseline = true)]
    public double TransformPoint_Raw()
    {
        var result = _rawMath.Transform([_rawX, _rawY]);
        return result[0] + result[1];
    }
}

/// <summary>
/// Line transform micro (T-076): engine verb vs raw ProjNet over a
/// 256-point line. The delta over the point micro isolates the per-vertex
/// adapter cost from the per-call lookup cost.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class TransformLineBenchmarks
{
    private readonly ProjNetTransforms _transforms = new();

    private IGeometry _line = null!;
    private MathTransform _rawMath = null!;
    private double[] _rawXs = [];
    private double[] _rawYs = [];

    [GlobalSetup]
    public void Setup()
    {
        const int points = 256;
        var coordinates = new Coordinate[points];
        _rawXs = new double[points];
        _rawYs = new double[points];
        for (var i = 0; i < points; i++)
        {
            var lon = -122.5 + (0.2 * i / points);
            var lat = 37.7 + (0.2 * i / points);
            coordinates[i] = new Coordinate(lon, lat);
            _rawXs[i] = lon;
            _rawYs[i] = lat;
        }

        _line = GeometryFactory.CreateLineString(coordinates, CoordinateReference.Epsg(4326));
        _rawMath = new CoordinateTransformationFactory()
            .CreateFromCoordinateSystems(GeographicCoordinateSystem.WGS84, ProjectedCoordinateSystem.WebMercator)
            .MathTransform;
    }

    [Benchmark(Description = "Transform 256-pt line via ProjNetTransforms (4326->3857)")]
    public object TransformLine_Engine() => _transforms.Transform(_line, source: null, target: "EPSG:3857");

    [Benchmark(Description = "Transform 256-pt line raw ProjNet (WGS84->WebMercator)", Baseline = true)]
    public double TransformLine_Raw()
    {
        var checksum = 0.0;
        for (var i = 0; i < _rawXs.Length; i++)
        {
            var result = _rawMath.Transform([_rawXs[i], _rawYs[i]]);
            checksum += result[0] + result[1];
        }

        return checksum;
    }
}
