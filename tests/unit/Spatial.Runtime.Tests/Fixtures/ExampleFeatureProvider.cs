using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// The plan's in-memory example capability provider (Epic D): it hosts
/// <c>spatial.feature.count@1</c> (counts features), <c>spatial.feature.envelope@1</c>
/// (envelope of all geometry attributes; requires <c>spatial.feature.read</c>)
/// and the <c>spatial.fixture.sleep@1</c> runtime test fixture (long-running,
/// cancellable, reports progress). Serves as the test vehicle for the
/// in-memory component host and as a template for real providers.
/// </summary>
public sealed class ExampleFeatureProvider : CapabilityProviderBase
{
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("example@1");

    public static readonly CapabilityId CountCapability = CapabilityId.Parse("spatial.feature.count@1");
    public static readonly CapabilityId EnvelopeCapability = CapabilityId.Parse("spatial.feature.envelope@1");
    public static readonly CapabilityId SleepCapability = CapabilityId.Parse("spatial.fixture.sleep@1");

    public static readonly Permission ReadPermission = Permission.Parse("spatial.feature.read");

    private static readonly FeatureSchema CountSchema = new(
        [new FieldDefinition("count", AttributeKind.Int64)]);

    private static readonly FeatureSchema EnvelopeSchema = new(
    [
        new FieldDefinition("minx", AttributeKind.Double),
        new FieldDefinition("miny", AttributeKind.Double),
        new FieldDefinition("maxx", AttributeKind.Double),
        new FieldDefinition("maxy", AttributeKind.Double),
    ]);

    public ExampleFeatureProvider()
        : base(BuildDescriptors())
    {
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        invocation.Capability == CountCapability ? CountAsync(invocation)
        : invocation.Capability == EnvelopeCapability ? EnvelopeAsync(invocation)
        : invocation.Capability == SleepCapability ? SleepAsync(invocation)
        : new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.ContractViolation(
            $"The example provider does not serve {invocation.Capability}.")));

    private static IReadOnlyList<CapabilityDescriptor> BuildDescriptors() =>
    [
        new CapabilityDescriptor(
            CountCapability,
            "Counts the features in a feature batch and returns a one-feature batch with an int64 'count'.",
            new SchemaDescriptor("feature.batch", "A feature batch with any schema."),
            new SchemaDescriptor("feature.batch", "A one-feature batch with a single int64 'count' attribute.", CountSchema),
            [new ErrorVariant("invalid.arguments", "The 'batch' argument is missing or is not a FeatureBatch.")],
            [],
            CapabilityTraits.Cancellable,
            [
                new ConformanceExample("three points", "Counts a batch of three point features; expects count 3."),
            ]),

        new CapabilityDescriptor(
            EnvelopeCapability,
            "Computes the envelope of every geometry attribute in a feature batch.",
            new SchemaDescriptor("feature.batch", "A feature batch with any schema."),
            new SchemaDescriptor("feature.batch", "A one-feature batch with minx/miny/maxx/maxy double attributes.", EnvelopeSchema),
            [new ErrorVariant("invalid.arguments", "The 'batch' argument is missing, has no geometry attributes, or has only empty geometries.")],
            [ReadPermission],
            CapabilityTraits.Cancellable,
            [
                new ConformanceExample("unit square points", "Two point features at (0,0) and (1,1); expects 0..1 bounds."),
            ]),

        new CapabilityDescriptor(
            SleepCapability,
            "Sleeps for the requested milliseconds, reporting progress — a runtime test fixture for deadlines, cancellation and progress.",
            new SchemaDescriptor("scalar", "An int64 'milliseconds' argument."),
            new SchemaDescriptor("scalar", "The number of milliseconds slept."),
            [new ErrorVariant("invalid.arguments", "The 'milliseconds' argument is missing, not an int64, or negative.")],
            [],
            CapabilityTraits.LongRunning | CapabilityTraits.Cancellable,
            []),
    ];

    private static ValueTask<CapabilityResult> CountAsync(CapabilityInvocation invocation)
    {
        if (!invocation.TryGetArgument<FeatureBatch>("batch", out var batch))
        {
            return new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.InvalidArguments(
                "spatial.feature.count@1 requires a FeatureBatch argument named 'batch'.")));
        }

        var counted = 0L;
        foreach (var feature in batch.Features)
        {
            invocation.CancellationToken.ThrowIfCancellationRequested();
            counted++;
            if (batch.Count >= 100 && counted % 100 == 0)
            {
                invocation.Progress?.Report(ProgressReport.Create(
                    counted / (double)batch.Count, $"counted {counted} of {batch.Count}"));
            }
        }

        invocation.Progress?.Report(ProgressReport.Create(1, $"counted {counted} features"));
        var result = new FeatureBatch(
            CountSchema,
            [new Feature(new FeatureId("result"), CountSchema, [AttributeValue.FromInt64(counted)])]);
        return new ValueTask<CapabilityResult>(CapabilityResult.Success(result));
    }

    private static ValueTask<CapabilityResult> EnvelopeAsync(CapabilityInvocation invocation)
    {
        if (!invocation.TryGetArgument<FeatureBatch>("batch", out var batch))
        {
            return new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.InvalidArguments(
                "spatial.feature.envelope@1 requires a FeatureBatch argument named 'batch'.")));
        }

        var envelope = Envelope.Empty;
        foreach (var feature in batch.Features)
        {
            invocation.CancellationToken.ThrowIfCancellationRequested();
            foreach (var attribute in feature.Attributes)
            {
                if (attribute.Kind is not AttributeKind.Geometry)
                {
                    continue;
                }

                if (attribute.GeometryValue.Envelope is { } part)
                {
                    envelope = envelope.Union(part);
                }
            }
        }

        if (envelope.IsEmpty)
        {
            return new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.InvalidArguments(
                "spatial.feature.envelope@1 needs at least one feature carrying a non-empty geometry.")));
        }

        var result = new FeatureBatch(
            EnvelopeSchema,
            [
                new Feature(
                    new FeatureId("result"),
                    EnvelopeSchema,
                    [
                        AttributeValue.FromDouble(envelope.MinX),
                        AttributeValue.FromDouble(envelope.MinY),
                        AttributeValue.FromDouble(envelope.MaxX),
                        AttributeValue.FromDouble(envelope.MaxY),
                    ]),
            ]);
        return new ValueTask<CapabilityResult>(CapabilityResult.Success(result));
    }

    private static async ValueTask<CapabilityResult> SleepAsync(CapabilityInvocation invocation)
    {
        if (!invocation.TryGetArgument<long>("milliseconds", out var milliseconds) || milliseconds < 0)
        {
            return CapabilityResult.Failure(CapabilityError.InvalidArguments(
                "spatial.fixture.sleep@1 requires a non-negative int64 argument named 'milliseconds'."));
        }

        const int steps = 10;
        for (var step = 1; step <= steps; step++)
        {
            invocation.CancellationToken.ThrowIfCancellationRequested();
            invocation.Progress?.Report(ProgressReport.Create(step / (double)steps, $"sleep step {step} of {steps}"));
            await Task.Delay((int)(milliseconds / steps), invocation.CancellationToken);
        }

        return CapabilityResult.Success(milliseconds);
    }
}