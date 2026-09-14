using BenchmarkDotNet.Attributes;
using NetTopologySuite.Operation.Overlay;
using NetTopologySuite.Operation.OverlayNG;
using NetTopologySuite.Simplify;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using NtsFactory = NetTopologySuite.Geometries.GeometryFactory;

namespace Spatial.Performance;

#pragma warning disable CA1859 // The engine path is deliberately exercised through the
// IGeometryOperations contract, exactly as the host resolves it; devirtualizing
// the call would erase the dispatch cost under measurement.

// Shared fixtures: one jittered-circle polygon family and one sine line, built
// once per bench class in both core and raw-NTS form.
internal static class GeometryFixtures
{
    public static Polygon Circle(double cx, double cy, double radius, int vertices) =>
        GeometryFactory.CreatePolygon(Ring(cx, cy, radius, vertices));

    private static Coordinate[] Ring(double cx, double cy, double radius, int vertices)
    {
        var ring = new Coordinate[vertices + 1];
        for (var i = 0; i < vertices; i++)
        {
            var angle = 2 * Math.PI * i / vertices;
            ring[i] = new Coordinate(cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }

        // Bit-identical closure: sin/cos(2π) is not bit-identical to sin/cos(0),
        // and NTS LinearRing demands exact closure.
        ring[vertices] = ring[0];
        return ring;
    }

    public static LineString SineLine(int points)
    {
        var coordinates = new Coordinate[points];
        for (var i = 0; i < points; i++)
        {
            coordinates[i] = new Coordinate(i, 10 * Math.Sin(i * 0.1));
        }

        return GeometryFactory.CreateLineString(coordinates);
    }

    public static NetTopologySuite.Geometries.Geometry RawCircle(
        NtsFactory factory, double cx, double cy, double radius, int vertices)
    {
        var points = new NetTopologySuite.Geometries.Coordinate[vertices + 1];
        for (var i = 0; i < vertices; i++)
        {
            var angle = 2 * Math.PI * i / vertices;
            points[i] = new NetTopologySuite.Geometries.Coordinate(
                cx + radius * Math.Cos(angle), cy + radius * Math.Sin(angle));
        }

        points[vertices] = new NetTopologySuite.Geometries.Coordinate(points[0].X, points[0].Y);
        return factory.CreatePolygon(factory.CreateLinearRing(points));
    }

    public static NetTopologySuite.Geometries.Geometry RawSineLine(NtsFactory factory, int points)
    {
        var result = new NetTopologySuite.Geometries.Coordinate[points];
        for (var i = 0; i < points; i++)
        {
            result[i] = new NetTopologySuite.Geometries.Coordinate(i, 10 * Math.Sin(i * 0.1));
        }

        return factory.CreateLineString(result);
    }
}

/// <summary>
/// Buffer micro (T-076): the engine verb via <see cref="Spatial.PluginSdk.IGeometryOperations"/>
/// against the same operation executed straight on NTS types. The delta is the
/// Core↔NTS adapter cost (ToNts + ToCore) plus validation.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class BufferBenchmarks
{
    private readonly NtsGeometryOperations _operations = new();

    private IGeometry _polygon = null!;
    private NetTopologySuite.Geometries.Geometry _rawPolygon = null!;

    [GlobalSetup]
    public void Setup()
    {
        _polygon = GeometryFixtures.Circle(0, 0, 100, 64);
        _rawPolygon = GeometryFixtures.RawCircle(new NtsFactory(), 0, 0, 100, 64);
    }

    [Benchmark(Description = "Buffer via IGeometryOperations (64-gon, d=10)")]
    public object Buffer_Engine() => _operations.Buffer(_polygon, 10);

    [Benchmark(Description = "Buffer raw NTS (64-gon, d=10)", Baseline = true)]
    public object Buffer_Raw() => _rawPolygon.Buffer(10, 8);
}

/// <summary>
/// Intersection micro (T-076): engine verb vs raw
/// <c>OverlayNGRobust</c> on two overlapping 64-gons.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class IntersectionBenchmarks
{
    private readonly NtsGeometryOperations _operations = new();

    private IGeometry _left = null!;
    private IGeometry _right = null!;
    private NetTopologySuite.Geometries.Geometry _rawLeft = null!;
    private NetTopologySuite.Geometries.Geometry _rawRight = null!;

    [GlobalSetup]
    public void Setup()
    {
        _left = GeometryFixtures.Circle(0, 0, 100, 64);
        _right = GeometryFixtures.Circle(60, 0, 100, 64);
        var factory = new NtsFactory();
        _rawLeft = GeometryFixtures.RawCircle(factory, 0, 0, 100, 64);
        _rawRight = GeometryFixtures.RawCircle(factory, 60, 0, 100, 64);
    }

    [Benchmark(Description = "Intersection via IGeometryOperations (64-gons)")]
    public object Intersection_Engine() => _operations.Intersection(_left, _right);

    [Benchmark(Description = "Intersection raw NTS (64-gons)", Baseline = true)]
    public object Intersection_Raw() =>
        OverlayNGRobust.Overlay(_rawLeft, _rawRight, SpatialFunction.Intersection);
}

/// <summary>
/// Simplify micro (T-076): engine verb vs raw
/// <c>DouglasPeuckerSimplifier</c> on a 512-point sine line.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class SimplifyBenchmarks
{
    private readonly NtsGeometryOperations _operations = new();

    private IGeometry _line = null!;
    private NetTopologySuite.Geometries.Geometry _rawLine = null!;

    [GlobalSetup]
    public void Setup()
    {
        _line = GeometryFixtures.SineLine(512);
        _rawLine = GeometryFixtures.RawSineLine(new NtsFactory(), 512);
    }

    [Benchmark(Description = "Simplify via IGeometryOperations (512-pt line, tol=1)")]
    public object Simplify_Engine() => _operations.Simplify(_line, 1.0);

    [Benchmark(Description = "Simplify raw NTS (512-pt line, tol=1)", Baseline = true)]
    public object Simplify_Raw() => DouglasPeuckerSimplifier.Simplify(_rawLine, 1.0);
}
