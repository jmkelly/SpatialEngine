using System.Reflection;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;
using Spatial.Runtime.Capabilities;

namespace Spatial.Operations.NetTopologySuite.Tests;

/// <summary>
/// The provider's behaviour at its contract boundary (ADR-0026): success,
/// empty input, unsupported input and diagnostics for every operation, plus
/// the ADR-0005 guard that no NetTopologySuite type is visible on the plugin's
/// public surface. Invocations call the provider directly with crafted
/// arguments (the runtime routing is exercised by the conformance suite).
/// </summary>
public sealed class NtsOperationsProviderTests
{
    private readonly NtsOperationsProvider _provider = new();

    [Fact]
    public void Descriptors_register_cleanly_under_the_runtime_rules()
    {
        var registry = new CapabilityRegistry();
        registry.Register(_provider);
        var registration = Assert.Single(registry.Providers);
        Assert.Equal(NtsOperationsProvider.ProviderIdentifier, registration.Id);
        Assert.Equal(4, registration.Descriptors.Count);
        Assert.Contains(registration.Descriptors, descriptor => descriptor.Id == BufferContract.Id);
    }

    [Fact]
    public async Task Buffer_of_a_point_returns_an_enclosing_polygon()
    {
        var result = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Distance, 1.0)));

        var envelope = EnvelopeOf(result);
        AssertCloseTo(-1, envelope.MinX);
        AssertCloseTo(-1, envelope.MinY);
        AssertCloseTo(1, envelope.MaxX);
        AssertCloseTo(1, envelope.MaxY);
    }

    [Fact]
    public async Task Buffer_honours_an_explicit_quadrant_segment_count()
    {
        var coarse = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Distance, 1.0,
            GeometryOperationArguments.QuadrantSegments, 4L)));
        var fine = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Distance, 1.0,
            GeometryOperationArguments.QuadrantSegments, 16L)));

        Assert.True(AssertSuccess<Polygon>(fine).CoordinateCount > AssertSuccess<Polygon>(coarse).CoordinateCount);
    }

    [Fact]
    public async Task A_negative_buffer_distance_erodes()
    {
        var result = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, RingSquare(-2, -2, 2, 2),
            GeometryOperationArguments.Distance, -0.5)));

        var envelope = EnvelopeOf(result);
        AssertCloseTo(-1.5, envelope.MinX);
        AssertCloseTo(-1.5, envelope.MinY);
        AssertCloseTo(1.5, envelope.MaxX);
        AssertCloseTo(1.5, envelope.MaxY);
    }

    [Fact]
    public async Task Buffering_an_empty_geometry_yields_an_empty_result()
    {
        var result = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreateEmptyPoint(),
            GeometryOperationArguments.Distance, 1.0)));

        Assert.True(AssertSuccess(result).IsEmpty);
    }

    [Fact]
    public async Task Buffer_missing_or_non_numeric_arguments_are_invalid()
    {
        var missing = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0))));
        AssertInvalidArguments(missing, "geometry");

        var nonNumeric = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Distance, "a mile")));
        AssertInvalidArguments(nonNumeric, "distance");

        var notFinite = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Distance, double.NaN)));
        AssertInvalidArguments(notFinite, "finite");

        var badSegments = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Distance, 1.0,
            GeometryOperationArguments.QuadrantSegments, 0L)));
        AssertInvalidArguments(badSegments, "quadrantSegments");
    }

    [Fact]
    public async Task Buffer_of_a_ring_the_algorithm_cannot_process_is_actionable()
    {
        // A one-coordinate ring cannot form a LinearRing (NTS rejects rings
        // with fewer than two points) — an input the algorithm cannot
        // process, reported as invalid.arguments naming the cause.
        var result = await _provider.InvokeAsync(Invoke(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePolygon(
                [new Coordinate(0, 0)]),
            GeometryOperationArguments.Distance, 1.0)));

        AssertInvalidArguments(result, "input geometry");
    }

    [Fact]
    public async Task Intersection_of_overlapping_squares_returns_their_overlap()
    {
        var result = await _provider.InvokeAsync(Invoke(IntersectionContract.Id, Arguments(
            GeometryOperationArguments.Left, RingSquare(0, 0, 2, 2),
            GeometryOperationArguments.Right, RingSquare(1, 1, 3, 3))));

        var envelope = EnvelopeOf(result);
        AssertCloseTo(1, envelope.MinX);
        AssertCloseTo(1, envelope.MinY);
        AssertCloseTo(2, envelope.MaxX);
        AssertCloseTo(2, envelope.MaxY);
    }

    [Fact]
    public async Task Intersection_of_disjoint_inputs_is_an_empty_success()
    {
        var result = await _provider.InvokeAsync(Invoke(IntersectionContract.Id, Arguments(
            GeometryOperationArguments.Left, RingSquare(0, 0, 1, 1),
            GeometryOperationArguments.Right, RingSquare(5, 5, 6, 6))));

        Assert.True(AssertSuccess(result).IsEmpty);
    }

    [Fact]
    public async Task Intersection_missing_the_right_geometry_is_invalid()
    {
        var result = await _provider.InvokeAsync(Invoke(IntersectionContract.Id, Arguments(
            GeometryOperationArguments.Left, RingSquare(0, 0, 1, 1))));

        AssertInvalidArguments(result, "right");
    }

    [Fact]
    public async Task Validate_reports_structural_validity()
    {
        var valid = await _provider.InvokeAsync(Invoke(ValidateContract.Id, Arguments(
            GeometryOperationArguments.Geometry, RingSquare(0, 0, 1, 1))));
        Assert.True(AssertSuccessBool(valid));

        var bowtieResult = await _provider.InvokeAsync(Invoke(ValidateContract.Id, Arguments(
            GeometryOperationArguments.Geometry, Bowtie())));
        Assert.False(AssertSuccessBool(bowtieResult));
    }

    [Fact]
    public async Task Validate_reports_open_and_undersized_rings_as_invalid()
    {
        var openRing = await _provider.InvokeAsync(Invoke(ValidateContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePolygon(
                [new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10)]))));
        Assert.False(AssertSuccessBool(openRing));

        var tinyRing = await _provider.InvokeAsync(Invoke(ValidateContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePolygon(
                [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 0)]))));
        Assert.False(AssertSuccessBool(tinyRing));
    }

    [Fact]
    public async Task Validate_of_an_empty_geometry_is_valid()
    {
        var result = await _provider.InvokeAsync(Invoke(ValidateContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreateEmptyPoint())));

        Assert.True(AssertSuccessBool(result));
    }

    [Fact]
    public async Task Simplify_removes_collinear_points_and_honours_the_tolerance()
    {
        var straight = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 2), new Coordinate(3, 3), new Coordinate(4, 4)]);
        var zigzag = GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 0), new Coordinate(3, 1), new Coordinate(4, 0)]);

        var simplified = await _provider.InvokeAsync(Invoke(SimplifyContract.Id, Arguments(
            GeometryOperationArguments.Geometry, straight,
            GeometryOperationArguments.Tolerance, 0.5)));
        Assert.Equal(2, AssertSuccess<LineString>(simplified).CoordinateCount);

        // A zero tolerance removes nothing (every vertex lies outside the
        // zero-width corridor of a non-collinear line).
        var unchanged = await _provider.InvokeAsync(Invoke(SimplifyContract.Id, Arguments(
            GeometryOperationArguments.Geometry, zigzag,
            GeometryOperationArguments.Tolerance, 0.0)));
        Assert.Equal(5, AssertSuccess<LineString>(unchanged).CoordinateCount);
    }

    [Fact]
    public async Task A_negative_simplify_tolerance_is_invalid()
    {
        var result = await _provider.InvokeAsync(Invoke(SimplifyContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Tolerance, -0.5)));

        AssertInvalidArguments(result, "non-negative");
    }

    [Fact]
    public async Task A_cancelled_invocation_is_an_operation_cancelled_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var invocation = CapabilityInvocation.Create(BufferContract.Id, Arguments(
            GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
            GeometryOperationArguments.Distance, 1.0)) with
        { CancellationToken = cts.Token };

        var result = await _provider.InvokeAsync(invocation);

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.Cancelled, failure.Error.Kind);
        Assert.Equal("operation.cancelled", failure.Error.Code);
    }

    [Fact]
    public async Task An_unknown_capability_is_a_contract_violation()
    {
        var result = await _provider.InvokeAsync(Invoke(
            CapabilityId.Parse("spatial.geometry.unknown@1"), new Dictionary<string, object?>()));

        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.ContractViolation, failure.Error.Kind);
        Assert.Contains("does not serve", failure.Error.Message);
    }

    [Fact]
    public void No_NetTopologySuite_type_appears_on_the_public_surface()
    {
        // ADR-0005: third-party geometry types must never cross a public
        // boundary. Every public type's public members must be core/SDK/BCL
        // types only.
        var violations = new List<string>();
        var assembly = typeof(NtsOperationsProvider).Assembly;
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

    private static IGeometry AssertSuccess(CapabilityResult result)
    {
        var value = Assert.IsType<CapabilitySuccess>(result).Value;
        if (value is not IGeometry geometry)
        {
            throw new InvalidOperationException($"expected a geometry success, got {result}.");
        }

        return geometry;
    }

    private static T AssertSuccess<T>(CapabilityResult result)
        where T : IGeometry =>
        Assert.IsType<T>(AssertSuccess(result));

    private static bool AssertSuccessBool(CapabilityResult result) =>
        Assert.IsType<bool>(Assert.IsType<CapabilitySuccess>(result).Value);

    private static Envelope EnvelopeOf(CapabilityResult result) =>
        AssertSuccess(result).Envelope ?? throw new InvalidOperationException("expected a non-empty envelope.");

    private static void AssertInvalidArguments(CapabilityResult result, string messagePart)
    {
        var failure = Assert.IsType<CapabilityFailure>(result);
        Assert.Equal(CapabilityErrorKind.InvalidArguments, failure.Error.Kind);
        Assert.Equal("invalid.arguments", failure.Error.Code);
        Assert.Contains(messagePart, failure.Error.Message);
    }

    private static void AssertCloseTo(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) < 1e-6, $"expected {expected}, got {actual}.");

    private static CapabilityInvocation Invoke(CapabilityId id, IReadOnlyDictionary<string, object?> arguments) =>
        CapabilityInvocation.Create(id, arguments);

    private static Dictionary<string, object?> Arguments(params object?[] pairs)
    {
        var arguments = new Dictionary<string, object?>(pairs.Length / 2);
        for (var i = 0; i < pairs.Length; i += 2)
        {
            arguments[(string)pairs[i]!] = pairs[i + 1];
        }

        return arguments;
    }

    private static Polygon RingSquare(double minX, double minY, double maxX, double maxY) =>
        GeometryFactory.CreatePolygon(
            [new Coordinate(minX, minY), new Coordinate(maxX, minY), new Coordinate(maxX, maxY), new Coordinate(minX, maxY), new Coordinate(minX, minY)]);

    private static Polygon Bowtie() =>
        GeometryFactory.CreatePolygon(
            [new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(0, 1), new Coordinate(1, 0), new Coordinate(0, 0)]);
}
