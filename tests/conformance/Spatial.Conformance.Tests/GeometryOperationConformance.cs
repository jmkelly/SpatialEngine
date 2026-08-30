using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Operations;
using Spatial.Runtime.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures of the Phase 6 operation contracts (plan
/// §18, ADR-0026): every provider of a standard capability runs the same
/// fixtures — success, empty input, unsupported input, cancellation and
/// diagnostics — against the contract's shared conformance examples, and
/// every outcome must carry provenance that names the serving provider.
/// The suite is provider-agnostic: the same invoker delegate drives the
/// in-process provider and the isolated worker package, so both must agree
/// on every result shape.
/// </summary>
public static class GeometryOperationConformance
{
    /// <summary>Invokes one capability and awaits its runtime outcome.</summary>
    public delegate Task<CapabilityOutcome> Invoker(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Runs the full success/empty/unsupported/diagnostics matrix for all four contracts.</summary>
    /// <param name="skipWireIncompatible">
    /// When true, examples whose arguments carry non-finite numbers are
    /// skipped: JSON (the worker wire) cannot represent NaN or infinity, so
    /// those input-contract examples run only against in-process providers.
    /// </param>
    public static async Task RunOperationMatrixAsync(Invoker invoke, ProviderId expectedProvider, bool skipWireIncompatible = false)
    {
        await AssertOperationAsync(invoke, expectedProvider, BufferContract.Id, GeometryOperationConformanceExamples.Buffer, skipWireIncompatible);
        await AssertOperationAsync(invoke, expectedProvider, IntersectionContract.Id, GeometryOperationConformanceExamples.Intersection, skipWireIncompatible);
        await AssertOperationAsync(invoke, expectedProvider, ValidateContract.Id, GeometryOperationConformanceExamples.Validate, skipWireIncompatible);
        await AssertOperationAsync(invoke, expectedProvider, SimplifyContract.Id, GeometryOperationConformanceExamples.Simplify, skipWireIncompatible);
    }

    /// <summary>Runs the cancellation fixture: a pre-cancelled invocation is a Cancelled failure.</summary>
    public static async Task AssertCancellationAsync(Invoker invoke, ProviderId expectedProvider)
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var outcome = await invoke(
            BufferContract.Id,
            GeometryOperationConformanceExamples.Arguments(
                GeometryOperationArguments.Geometry, GeometryFactory.CreatePoint(0, 0),
                GeometryOperationArguments.Distance, 1.0),
            cts.Token);

        Assert.True(outcome.Result is CapabilityFailure { Error.Kind: CapabilityErrorKind.Cancelled });
        Assert.Equal("operation.cancelled", outcome.Error!.Code);
        AssertProvenance(outcome, BufferContract.Id, expectedProvider);
    }

    private static async Task AssertOperationAsync(
        Invoker invoke,
        ProviderId expectedProvider,
        CapabilityId capability,
        IReadOnlyList<ConformanceExample> examples,
        bool skipWireIncompatible)
    {
        foreach (var example in examples)
        {
            if (skipWireIncompatible && CarriesNonFiniteNumber(example))
            {
                continue;
            }

            var outcome = await invoke(capability, example.Arguments ?? new Dictionary<string, object?>());
            AssertProvenance(outcome, capability, expectedProvider);
            AssertShape(outcome, capability, example);
        }
    }

    private static bool CarriesNonFiniteNumber(ConformanceExample example)
    {
        if (example.Arguments is null)
        {
            return false;
        }

        return example.Arguments.Values.OfType<double>().Any(value => !double.IsFinite(value));
    }

    private static void AssertShape(CapabilityOutcome outcome, CapabilityId capability, ConformanceExample example)
    {
        if (outcome.Result is CapabilitySuccess success)
        {
            AssertSuccessShape(capability, example, success.Value);
            return;
        }

        AssertInvalidArgumentsShape(outcome, example);
    }

    private static void AssertSuccessShape(CapabilityId capability, ConformanceExample example, object? value)
    {
        if (capability == ValidateContract.Id)
        {
            AssertValidateShape(example.Name, value);
        }
        else if (capability == BufferContract.Id)
        {
            AssertGeometryShape(example.Name, value, BufferShapes);
        }
        else if (capability == IntersectionContract.Id)
        {
            AssertGeometryShape(example.Name, value, IntersectionShapes);
        }
        else
        {
            AssertGeometryShape(example.Name, value, SimplifyShapes);
        }
    }

    private static void AssertValidateShape(string name, object? value)
    {
        switch (name)
        {
            case "valid-square":
            case "valid-line":
            case "empty-input":
                Assert.True(IsTrue(value), $"{name} must report a valid geometry, got '{value ?? "null"}'.");
                break;
            case "self-intersecting-bowtie":
                Assert.False(IsTrue(value), "the bowtie must be reported invalid.");
                break;
            default:
                throw new Xunit.Sdk.XunitException($"unknown validate example '{name}'.");
        }
    }

    private static void AssertGeometryShape(string name, object? value, IReadOnlyDictionary<string, Action<IGeometry>> shapes)
    {
        if (shapes.TryGetValue(name, out var assert))
        {
            assert(GeometryOf(value));
            return;
        }

        throw new Xunit.Sdk.XunitException($"unknown geometry example '{name}'.");
    }

    private static readonly IReadOnlyDictionary<string, Action<IGeometry>> BufferShapes =
        new Dictionary<string, Action<IGeometry>>
        {
            ["point-ring"] = geometry => AssertEnvelope(geometry, -1, -1, 1, 1),
            ["erosion"] = geometry => AssertEnvelope(geometry, -1.5, -1.5, 1.5, 1.5),
            ["segment-count"] = geometry => Assert.False(geometry.IsEmpty),
            ["empty-input"] = geometry => Assert.True(geometry.IsEmpty),
        };

    private static readonly IReadOnlyDictionary<string, Action<IGeometry>> IntersectionShapes =
        new Dictionary<string, Action<IGeometry>>
        {
            ["overlapping-squares"] = geometry => AssertEnvelope(geometry, 1, 1, 2, 2),
            ["touching-squares"] = geometry => Assert.False(geometry.IsEmpty),
            ["disjoint-squares"] = geometry => Assert.True(geometry.IsEmpty),
            ["empty-left"] = geometry => Assert.True(geometry.IsEmpty),
        };

    private static readonly IReadOnlyDictionary<string, Action<IGeometry>> SimplifyShapes =
        new Dictionary<string, Action<IGeometry>>
        {
            ["collinear-line"] = geometry => Assert.Equal(2, geometry.CoordinateCount),
            ["zero-tolerance"] = geometry => Assert.Equal(3, geometry.CoordinateCount),
            ["empty-input"] = geometry => Assert.True(geometry.IsEmpty),
        };

    private static void AssertInvalidArgumentsShape(CapabilityOutcome outcome, ConformanceExample example)
    {
        var error = outcome.Error!;
        Assert.Equal(CapabilityErrorKind.InvalidArguments, error.Kind);
        Assert.Equal("invalid.arguments", error.Code);
        Assert.False(string.IsNullOrWhiteSpace(error.Message), "an invalid.arguments error must be actionable");
    }

    private static void AssertProvenance(CapabilityOutcome outcome, CapabilityId capability, ProviderId expectedProvider)
    {
        Assert.Equal(capability, outcome.Provenance.Capability);
        Assert.Equal(expectedProvider, outcome.Provenance.Provider);
        Assert.NotNull(outcome.Provenance.Step);
        Assert.True(outcome.Provenance.Duration >= TimeSpan.Zero);
        Assert.Null(outcome.Provenance.JobId);
    }

    private static IGeometry GeometryOf(object? value) =>
        value as IGeometry ?? throw new Xunit.Sdk.XunitException($"expected a geometry, got '{value ?? "null"}'.");

    private static bool IsTrue(object? value) =>
        value is bool flag && flag;

    private static void AssertEnvelope(IGeometry geometry, double minX, double minY, double maxX, double maxY)
    {
        var envelope = geometry.Envelope ?? throw new Xunit.Sdk.XunitException("expected a non-empty envelope.");
        AssertCloseTo(minX, envelope.MinX);
        AssertCloseTo(minY, envelope.MinY);
        AssertCloseTo(maxX, envelope.MaxX);
        AssertCloseTo(maxY, envelope.MaxY);
    }

    private static void AssertCloseTo(double expected, double actual) =>
        Assert.True(Math.Abs(expected - actual) < 1e-6, $"expected {expected}, got {actual}.");
}
