using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;

namespace Spatial.Runtime.Tests.Fixtures;

/// <summary>
/// The plan's in-memory example capability provider (Epic D): it hosts
/// <c>spatial.feature.count@1</c> (counts features), <c>spatial.feature.envelope@1</c>
/// (envelope of all geometry attributes; requires <c>spatial.feature.read</c>),
/// the <c>spatial.fixture.sleep@1</c> runtime test fixture (long-running,
/// cancellable, reports progress) and the Phase 4 fixtures: <c>spatial.fixture.mint@1</c>
/// (opaque resource handles), <c>spatial.fixture.peek@1</c> (resource-local
/// resolution), <c>spatial.fixture.stream@1</c> (bounded streaming with
/// backpressure) and <c>spatial.fixture.jobstream@1</c> (long streams are jobs
/// with progress, ADR-0008).
/// </summary>
public sealed class ExampleFeatureProvider : CapabilityProviderBase
{
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("example@1");

    public static readonly CapabilityId CountCapability = CapabilityId.Parse("spatial.feature.count@1");
    public static readonly CapabilityId EnvelopeCapability = CapabilityId.Parse("spatial.feature.envelope@1");
    public static readonly CapabilityId SleepCapability = CapabilityId.Parse("spatial.fixture.sleep@1");
    public static readonly CapabilityId MintCapability = CapabilityId.Parse("spatial.fixture.mint@1");
    public static readonly CapabilityId PeekCapability = CapabilityId.Parse("spatial.fixture.peek@1");
    public static readonly CapabilityId StreamCapability = CapabilityId.Parse("spatial.fixture.stream@1");
    public static readonly CapabilityId JobStreamCapability = CapabilityId.Parse("spatial.fixture.jobstream@1");

    public static readonly Permission ReadPermission = Permission.Parse("spatial.feature.read");

    public static readonly ResourceKind DatasetKind = ResourceKind.Parse("fixture.dataset");
    public static readonly ResourceKind StreamKind = ResourceKind.Parse("fixture.stream");

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
        : invocation.Capability == MintCapability ? MintAsync(invocation)
        : invocation.Capability == PeekCapability ? PeekAsync(invocation)
        : invocation.Capability == StreamCapability ? StreamAsync(invocation)
        : invocation.Capability == JobStreamCapability ? JobStreamAsync(invocation)
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

        new CapabilityDescriptor(
            MintCapability,
            "Creates a runtime-owned resource handle owned by the example provider — the Phase 4 fixture for opaque handles, leases and resource-local resolution.",
            new SchemaDescriptor("none", "No arguments."),
            new SchemaDescriptor("resource", "A ResourceHandle owned by the example provider."),
            [new ErrorVariant("invalid.arguments", "The invocation carries no runtime facilities, so no handle can be minted.")],
            [],
            CapabilityTraits.Cancellable,
            []),

        new CapabilityDescriptor(
            PeekCapability,
            "Returns the provider id that served the invocation — the Phase 4 fixture for resource-local provider resolution.",
            new SchemaDescriptor("none", "No arguments."),
            new SchemaDescriptor("scalar", "The provider id that served the invocation."),
            [new ErrorVariant("invalid.arguments", "The invocation is malformed.")],
            [],
            CapabilityTraits.Cancellable,
            []),

        new CapabilityDescriptor(
            StreamCapability,
            "Streams 'chunks' string items through a bounded stream of 'capacity' items and returns the stream handle — the Phase 4 fixture for bounded streaming and backpressure.",
            new SchemaDescriptor("scalar", "int 'chunks' (default 5), int 'capacity' (default 2), int 'delayMilliseconds' (default 0)."),
            new SchemaDescriptor("stream.chunk", "A bounded stream of string chunks."),
            [new ErrorVariant("invalid.arguments", "A 'chunks', 'capacity' or 'delayMilliseconds' argument is not a non-negative int32.")],
            [],
            CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
            []),

        new CapabilityDescriptor(
            JobStreamCapability,
            "Streams 'chunks' string items through a bounded stream with progress and optional delay — the long-running streaming job fixture (long streams are jobs with progress, ADR-0008).",
            new SchemaDescriptor("scalar", "int 'chunks' (default 5), int 'capacity' (default 2), int 'delayMilliseconds' (default 30)."),
            new SchemaDescriptor("stream.chunk", "A bounded stream of string chunks."),
            [new ErrorVariant("invalid.arguments", "A 'chunks', 'capacity' or 'delayMilliseconds' argument is not a non-negative int32.")],
            [],
            CapabilityTraits.LongRunning | CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
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

    private static ValueTask<CapabilityResult> MintAsync(CapabilityInvocation invocation)
    {
        if (invocation.Facilities is not { } facilities)
        {
            return new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.InvalidArguments(
                "spatial.fixture.mint@1 needs the runtime facilities; only the runtime can mint handles.")));
        }

        var handle = facilities.Resources.Create(DatasetKind);
        return new ValueTask<CapabilityResult>(CapabilityResult.Success(handle));
    }

    private static ValueTask<CapabilityResult> PeekAsync(CapabilityInvocation invocation)
    {
        invocation.CancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<CapabilityResult>(CapabilityResult.Success(ProviderIdentifier));
    }

    private static async ValueTask<CapabilityResult> StreamAsync(CapabilityInvocation invocation)
    {
        if (ReadStreamArguments(invocation, out var chunks, out var capacity, out var delayMilliseconds) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        var channel = CreateStream(invocation, capacity);
        _ = EmitAsync(channel, chunks, delayMilliseconds);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async ValueTask<CapabilityResult> JobStreamAsync(CapabilityInvocation invocation)
    {
        if (ReadStreamArguments(invocation, out var chunks, out var capacity, out var delayMilliseconds) is { } error)
        {
            return CapabilityResult.Failure(error);
        }

        var channel = CreateStream(invocation, capacity);
        try
        {
            for (var i = 1; i <= chunks; i++)
            {
                invocation.CancellationToken.ThrowIfCancellationRequested();
                await channel.Writer.WriteAsync($"chunk-{i - 1}", invocation.CancellationToken);
                invocation.Progress?.Report(ProgressReport.Create(i / (double)chunks, $"streamed {i} of {chunks}"));
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, invocation.CancellationToken);
                }
            }

            channel.Writer.Complete();
        }
        catch (OperationCanceledException)
        {
            channel.Writer.Complete(CapabilityError.Cancelled(JobStreamCapability));
            throw;
        }

        return CapabilityResult.Success(channel.Handle);
    }

    private static async Task EmitAsync(StreamChannel channel, int chunks, int delayMilliseconds)
    {
        try
        {
            for (var i = 1; i <= chunks; i++)
            {
                await channel.Writer.WriteAsync($"chunk-{i - 1}");
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds);
                }
            }

            channel.Writer.Complete();
        }
        catch (OperationCanceledException)
        {
            channel.Writer.Complete(CapabilityError.Cancelled(StreamCapability));
        }
        catch (Exception exception)
        {
            channel.Writer.Complete(CapabilityError.ProviderFailure(exception.Message));
        }
    }

    private static StreamChannel CreateStream(CapabilityInvocation invocation, int capacity)
    {
        if (invocation.Facilities is not { } facilities)
        {
            throw new InvalidOperationException("spatial.fixture.stream@1 needs the runtime facilities.");
        }

        return facilities.Streams.Create(StreamKind, capacity);
    }

    private static CapabilityError? ReadStreamArguments(
        CapabilityInvocation invocation,
        out int chunks,
        out int capacity,
        out int delayMilliseconds)
    {
        chunks = ReadStreamInt(invocation, "chunks", 5, out var chunksError);
        capacity = ReadStreamInt(invocation, "capacity", 2, out var capacityError);
        delayMilliseconds = ReadStreamInt(
            invocation, "delayMilliseconds", JobStreamCapability == invocation.Capability ? 30 : 0, out var delayError);
        var error = chunksError ?? capacityError ?? delayError;
        if (error is null && (chunks < 1 || capacity < 1))
        {
            error = CapabilityError.InvalidArguments(
                "spatial.fixture.stream@1 needs at least one chunk and a capacity of at least one item.");
        }

        if (error is not null)
        {
            chunks = 5;
            capacity = 2;
            delayMilliseconds = 0;
        }

        return error;
    }

    private static int ReadStreamInt(CapabilityInvocation invocation, string name, int fallback, out CapabilityError? error)
    {
        error = null;
        if (invocation.TryGetArgument<int>(name, out var value) && value >= 0)
        {
            return value;
        }

        error = invocation.Arguments.ContainsKey(name)
            ? CapabilityError.InvalidArguments(
                $"spatial.fixture.stream@1 requires '{name}' to be a non-negative int32 when provided.")
            : null;
        return fallback;
    }
}
