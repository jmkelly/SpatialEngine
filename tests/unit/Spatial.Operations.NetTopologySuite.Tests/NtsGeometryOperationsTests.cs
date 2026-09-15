using System.Reflection;
using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Operations.NetTopologySuite.Tests;

/// <summary>
/// Behaviour of the in-process geometry operations (ADR-0033): success,
/// empty input, unsupported input and diagnostics for every operation, plus
/// the ADR-0005 guard that no NetTopologySuite type is visible on the
/// public surface.
/// </summary>
public sealed class NtsGeometryOperationsTests
{
    private readonly NtsGeometryOperations _operations = new();

    [Fact]
    public void Buffer_of_a_point_returns_an_enclosing_polygon()
    {
        var result = _operations.Buffer(GeometryFactory.CreatePoint(0, 0), 1.0);

        var envelope = result.Envelope!.Value;
        AssertCloseTo(-1, envelope.MinX);
        AssertCloseTo(-1, envelope.MinY);
        AssertCloseTo(1, envelope.MaxX);
        AssertCloseTo(1, envelope.MaxY);
    }

    [Fact]
    public void Buffer_honours_an_explicit_quadrant_segment_count()
    {
        var coarse = _operations.Buffer(GeometryFactory.CreatePoint(0, 0), 1.0, 4);
        var fine = _operations.Buffer(GeometryFactory.CreatePoint(0, 0), 1.0, 16);

        Assert.True(((Polygon)fine).CoordinateCount > ((Polygon)coarse).CoordinateCount);
    }

    [Fact]
    public void A_negative_buffer_distance_erodes()
    {
        var result = _operations.Buffer(RingSquare(-2, -2, 2, 2), -0.5);

        var envelope = result.Envelope!.Value;
        AssertCloseTo(-1.5, envelope.MinX);
        AssertCloseTo(-1.5, envelope.MinY);
        AssertCloseTo(1.5, envelope.MaxX);
        AssertCloseTo(1.5, envelope.MaxY);
    }

    [Fact]
    public void Buffering_an_empty_geometry_yields_an_empty_result()
    {
        var result = _operations.Buffer(GeometryFactory.CreateEmptyPoint(), 1.0);

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Buffer_rejects_bad_arguments()
    {
        var nan = Assert.Throws<SpatialException>(() =>
            _operations.Buffer(GeometryFactory.CreatePoint(0, 0), double.NaN));
        Assert.Equal(SpatialException.InvalidArguments, nan.Code);
        Assert.Contains("finite", nan.Message);

        var segments = Assert.Throws<SpatialException>(() =>
            _operations.Buffer(GeometryFactory.CreatePoint(0, 0), 1.0, 0));
        Assert.Equal(SpatialException.InvalidArguments, segments.Code);
        Assert.Contains("quadrantSegments", segments.Message);
    }

    [Fact]
    public void Buffer_of_a_ring_the_algorithm_cannot_process_is_actionable()
    {
        var exception = Assert.Throws<SpatialException>(() =>
            _operations.Buffer(GeometryFactory.CreatePolygon([new Coordinate(0, 0)]), 1.0));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("input geometry", exception.Message);
    }

    [Fact]
    public void Intersection_of_overlapping_squares_returns_their_overlap()
    {
        var result = _operations.Intersection(RingSquare(0, 0, 2, 2), RingSquare(1, 1, 3, 3));

        var envelope = result.Envelope!.Value;
        AssertCloseTo(1, envelope.MinX);
        AssertCloseTo(1, envelope.MinY);
        AssertCloseTo(2, envelope.MaxX);
        AssertCloseTo(2, envelope.MaxY);
    }

    [Fact]
    public void Intersection_of_disjoint_inputs_is_an_empty_success()
    {
        var result = _operations.Intersection(RingSquare(0, 0, 1, 1), RingSquare(5, 5, 6, 6));

        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void Validate_reports_structural_validity()
    {
        Assert.True(_operations.Validate(RingSquare(0, 0, 1, 1)));
        Assert.False(_operations.Validate(Bowtie()));
    }

    [Fact]
    public void Validate_reports_open_and_undersized_rings_as_invalid()
    {
        Assert.False(_operations.Validate(GeometryFactory.CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10)])));
        Assert.False(_operations.Validate(GeometryFactory.CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 0)])));
    }

    [Fact]
    public void Validate_reports_a_degenerate_hole_as_invalid()
    {
        var polygon = GeometryFactory.CreatePolygon(
            RingSquare(0, 0, 10, 10).ExteriorRing,
            [GeometryFactory.CreateLineString([new Coordinate(4, 4), new Coordinate(6, 6)])]);

        Assert.False(_operations.Validate(polygon));
    }

    [Fact]
    public void Measure_ordinates_survive_a_round_trip_through_the_engine()
    {
        var point = GeometryFactory.CreatePoint(1, 2, 3, 4);
        var buffered = _operations.Buffer(point, 1.0);
        Assert.Equal(0, buffered.Envelope!.Value.MinX, 1e-6);

        var line = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0, M: 1), new Coordinate(4, 0, M: 2)],
            CoordinateLayout.Xym);
        var simplified = Assert.IsType<LineString>(_operations.Simplify(line, 0.0));
        Assert.Equal(2, simplified.Sequence.GetOrdinate(1, Ordinate.M));
    }

    [Fact]
    public void Validate_of_an_empty_geometry_is_valid()
    {
        Assert.True(_operations.Validate(GeometryFactory.CreateEmptyPoint()));
    }

    [Fact]
    public void Simplify_removes_collinear_points_and_honours_the_tolerance()
    {
        var straight = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 2), new Coordinate(3, 3), new Coordinate(4, 4)]);
        var zigzag = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 0), new Coordinate(3, 1), new Coordinate(4, 0)]);

        Assert.Equal(2, _operations.Simplify(straight, 0.5).CoordinateCount);
        Assert.Equal(5, _operations.Simplify(zigzag, 0.0).CoordinateCount);
    }

    [Fact]
    public void A_negative_simplify_tolerance_is_invalid()
    {
        var exception = Assert.Throws<SpatialException>(() =>
            _operations.Simplify(GeometryFactory.CreatePoint(0, 0), -0.5));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("non-negative", exception.Message);
    }

    [Fact]
    public void A_non_finite_simplify_tolerance_is_invalid()
    {
        var exception = Assert.Throws<SpatialException>(() =>
            _operations.Simplify(GeometryFactory.CreatePoint(0, 0), double.NaN));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("finite", exception.Message);
    }

    [Fact]
    public void A_cancelled_call_throws_operation_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            _operations.Buffer(GeometryFactory.CreatePoint(0, 0), 1.0, 8, cts.Token));
    }

    [Fact]
    public void No_NetTopologySuite_type_appears_on_the_public_surface()
    {
        var violations = new List<string>();
        var assembly = typeof(NtsGeometryOperations).Assembly;
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                var signatureTypes = SignatureTypes(member).Concat([member.DeclaringType!]);
                foreach (var candidate in signatureTypes)
                {
                    if (NamesNetTopologySuite(candidate))
                    {
                        violations.Add($"{type.FullName}.{member.Name} exposes {candidate.FullName}.");
                    }
                }
            }
        }

        Assert.Empty(violations);
    }

    private static IEnumerable<Type> SignatureTypes(MemberInfo member) => member switch
    {
        ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
        MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType)
            .Append(method.ReturnType),
        PropertyInfo property => [property.PropertyType],
        FieldInfo field => [field.FieldType],
        _ => [],
    };

    private static bool NamesNetTopologySuite(Type type)
    {
        var current = type.IsByRef ? type.GetElementType() : type;
        if (current?.FullName?.StartsWith("NetTopologySuite", StringComparison.Ordinal) == true)
        {
            return true;
        }

        return current is { IsGenericType: true }
            && current.GetGenericArguments().Any(NamesNetTopologySuite);
    }

    private static void AssertCloseTo(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) < 1e-6, $"expected {expected}, got {actual}.");

    private static Polygon RingSquare(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
            [new Coordinate(minX, minY), new Coordinate(maxX, minY), new Coordinate(maxX, maxY), new Coordinate(minX, maxY), new Coordinate(minX, minY)]);

    private static Polygon Bowtie() =>
        GeometryFactory.CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 1), new Coordinate(1, 0), new Coordinate(0, 0)]);
}
