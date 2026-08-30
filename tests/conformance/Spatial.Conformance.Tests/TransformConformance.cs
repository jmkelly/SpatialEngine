using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Transformations;
using Spatial.Runtime.Capabilities;

namespace Spatial.Conformance.Tests;

/// <summary>
/// The shared conformance fixtures of the Phase 7 transformation contracts
/// (plan §18, ADR-0027): every provider of <c>spatial.coordinate.transform@1</c>
/// and <c>spatial.crs.describe@1</c> runs the same fixtures — success, empty
/// input, unsupported input, cancellation and diagnostics — and every outcome
/// must carry provenance naming the serving provider. The suite is
/// provider-agnostic: the same invoker delegate drives the in-process provider
/// and the isolated worker package, so both must agree on every result shape.
/// The transform fixtures are supplied by <see cref="TransformConformanceExamples"/>;
/// the describe fixtures are the contract's own embedded examples (string-only
/// values, ADR-0027).
/// </summary>
public static class TransformConformance
{
    /// <summary>Invokes one capability and awaits its runtime outcome.</summary>
    public delegate Task<CapabilityOutcome> Invoker(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken = default);

    /// <summary>Runs the full success/empty/unsupported/diagnostics matrix for both transformation contracts.</summary>
    public static async Task RunTransformationMatrixAsync(Invoker invoke, ProviderId expectedProvider)
    {
        await AssertTransformsAsync(invoke, expectedProvider);
        await AssertDescribesAsync(invoke, expectedProvider, CrsDescribeContract.Descriptor.Examples);
    }

    /// <summary>Runs the cancellation fixture: a pre-cancelled invocation is a Cancelled failure.</summary>
    public static async Task AssertCancellationAsync(Invoker invoke, ProviderId expectedProvider)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var outcome = await invoke(
            TransformContract.Id,
            TransformConformanceExamples.Arguments(
                TransformationArguments.Geometry, GeometryFactory.CreatePoint(13.405, 52.52, CoordinateReference.Epsg(4326)),
                TransformationArguments.Source, "EPSG:4326",
                TransformationArguments.Target, "EPSG:32632"),
            cancellation.Token);

        Assert.True(outcome.Result is CapabilityFailure { Error.Kind: CapabilityErrorKind.Cancelled });
        Assert.Equal("operation.cancelled", outcome.Error!.Code);
        AssertProvenance(outcome, TransformContract.Id, expectedProvider);
    }

    private static async Task AssertTransformsAsync(Invoker invoke, ProviderId expectedProvider)
    {
        foreach (var example in TransformConformanceExamples.Transform)
        {
            var outcome = await invoke(
                TransformContract.Id,
                example.Arguments ?? new Dictionary<string, object?>());
            AssertProvenance(outcome, TransformContract.Id, expectedProvider);

            if (outcome.Result is CapabilitySuccess success)
            {
                var geometry = (IGeometry)success.Value!;
                AssertTransformShape(example.Name, geometry);
            }
            else
            {
                AssertInvalidArgumentsShape(outcome);
            }
        }
    }

    private static async Task AssertDescribesAsync(
        Invoker invoke,
        ProviderId expectedProvider,
        IReadOnlyList<ConformanceExample> examples)
    {
        foreach (var example in examples)
        {
            var outcome = await invoke(
                CrsDescribeContract.Id,
                example.Arguments ?? new Dictionary<string, object?>());
            AssertProvenance(outcome, CrsDescribeContract.Id, expectedProvider);

            if (outcome.Result is CapabilitySuccess success)
            {
                AssertDescribeShape(example.Name, Assert.IsType<CrsDescription>(success.Value));
            }
            else
            {
                AssertInvalidArgumentsShape(outcome);
            }
        }
    }

    private static void AssertTransformShape(string name, IGeometry geometry)
    {
        switch (name)
        {
            case "berlin-utm32":
            case "geometry-crs-source":
                // The Berlin control point (PROJ 9 reference, always_xy):
                // any conforming provider reproduces it within a centimetre.
                AssertEnvelope(geometry, 798812.8026, 5827999.9001, 0.01);
                Assert.Equal(new CoordinateReference("EPSG", "32632"), geometry.CoordinateReference);
                break;
            case "empty-input":
                Assert.True(geometry.IsEmpty);
                Assert.Equal(new CoordinateReference("EPSG", "32632"), geometry.CoordinateReference);
                break;
            default:
                throw new Xunit.Sdk.XunitException($"unknown transform example '{name}'.");
        }
    }

    private static void AssertDescribeShape(string name, CrsDescription description)
    {
        switch (name)
        {
            case "wgs84":
                Assert.Equal("WGS 84", description.Name);
                Assert.Equal(CrsKind.Geographic, description.Kind);
                Assert.Equal("Lon", description.Axes[0].Name);
                Assert.Equal("Lat", description.Axes[1].Name);
                Assert.Equal("degree", description.Axes[0].UnitName);
                break;
            case "british-national-grid":
                Assert.Equal("OSGB36 / British National Grid", description.Name);
                Assert.Equal(CrsKind.Projected, description.Kind);
                Assert.Equal("Easting", description.Axes[0].Name);
                Assert.Equal("metre", description.Axes[0].UnitName);
                break;
            case "unknown-crs":
            case "missing-crs":
                throw new Xunit.Sdk.XunitException($"describe error example '{name}' must fail, not succeed.");
            default:
                throw new Xunit.Sdk.XunitException($"unknown describe example '{name}'.");
        }
    }

    private static void AssertInvalidArgumentsShape(CapabilityOutcome outcome)
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

    private static void AssertEnvelope(IGeometry geometry, double x, double y, double tolerance)
    {
        var envelope = geometry.Envelope ?? throw new Xunit.Sdk.XunitException("expected a non-empty envelope.");
        AssertClose(x, envelope.MinX, tolerance);
        AssertClose(y, envelope.MinY, tolerance);
    }

    private static void AssertClose(double expected, double actual, double tolerance) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"expected {expected}, got {actual}.");
}
