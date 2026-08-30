using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;

namespace Spatial.Plugin.Fixtures;

/// <summary>
/// The Phase 5 fault fixtures (plan §16 "crash, timeout and cancellation
/// fault fixtures") as an out-of-process capability provider. Serves
/// <c>spatial.fixture.peek@1</c>, <c>mint@1</c> (resource handles across the
/// worker boundary), <c>sleep@1</c> (cancellation + progress), <c>timeout@1</c>
/// (outlives a caller deadline), <c>crash@1</c> (process crash) and
/// <c>stream@1</c> (bounded streaming across the boundary). The descriptor
/// surface must match the package manifest the tests generate
/// (<c>FixtureManifest.V1</c>) — the worker host refuses to start when the
/// manifest and the loaded provider diverge.
/// </summary>
public sealed class FixtureProviderV1 : CapabilityProviderBase
{
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("fixture@1");

    public static readonly CapabilityId PeekCapability = CapabilityId.Parse("spatial.fixture.peek@1");
    public static readonly CapabilityId MintCapability = CapabilityId.Parse("spatial.fixture.mint@1");
    public static readonly CapabilityId SleepCapability = CapabilityId.Parse("spatial.fixture.sleep@1");
    public static readonly CapabilityId TimeoutCapability = CapabilityId.Parse("spatial.fixture.timeout@1");
    public static readonly CapabilityId CrashCapability = CapabilityId.Parse("spatial.fixture.crash@1");
    public static readonly CapabilityId StreamCapability = CapabilityId.Parse("spatial.fixture.stream@1");

    public static readonly ResourceKind DatasetKind = ResourceKind.Parse("fixture.dataset");
    public static readonly ResourceKind StreamKind = ResourceKind.Parse("fixture.stream");

    public FixtureProviderV1()
        : base(BuildDescriptors())
    {
    }

    public override ProviderId Id => ProviderIdentifier;

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        invocation.Capability == PeekCapability ? PeekAsync()
        : invocation.Capability == MintCapability ? MintAsync(invocation)
        : invocation.Capability == SleepCapability ? FixtureSleep.SleepFor(invocation, SleepCapability)
        : invocation.Capability == TimeoutCapability ? FixtureSleep.SleepFor(invocation, TimeoutCapability)
        : invocation.Capability == CrashCapability ? CrashAsync(invocation)
        : invocation.Capability == StreamCapability ? StreamAsync(invocation)
        : new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.ContractViolation(
            $"The fixture provider does not serve {invocation.Capability}.")));

    private static IReadOnlyList<CapabilityDescriptor> BuildDescriptors() =>
    [
        new CapabilityDescriptor(
            PeekCapability,
            "Returns the provider id that served the invocation.",
            new SchemaDescriptor("none"),
            new SchemaDescriptor("scalar"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.None,
            []),

        new CapabilityDescriptor(
            MintCapability,
            "Mints a runtime-owned resource handle through the invocation facilities.",
            new SchemaDescriptor("none"),
            new SchemaDescriptor("resource"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.None,
            []),

        new CapabilityDescriptor(
            SleepCapability,
            "Sleeps for the requested milliseconds, reporting progress — cancellable.",
            new SchemaDescriptor("scalar"),
            new SchemaDescriptor("scalar"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.LongRunning | CapabilityTraits.Cancellable,
            []),

        new CapabilityDescriptor(
            TimeoutCapability,
            "Sleeps far beyond a caller deadline so timeout attribution can be observed.",
            new SchemaDescriptor("scalar"),
            new SchemaDescriptor("scalar"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.LongRunning | CapabilityTraits.Cancellable,
            []),

        new CapabilityDescriptor(
            CrashCapability,
            "Crashes the worker process immediately — the crash fault fixture.",
            new SchemaDescriptor("none"),
            new SchemaDescriptor("scalar"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.None,
            []),

        new CapabilityDescriptor(
            StreamCapability,
            "Streams string items through a bounded stream across the worker boundary.",
            new SchemaDescriptor("scalar"),
            new SchemaDescriptor("stream.chunk"),
            [new ErrorVariant("invalid.arguments", "An argument is missing or of the wrong kind.")],
            [],
            CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
            []),
    ];

    private static ValueTask<CapabilityResult> PeekAsync() =>
        new(CapabilityResult.Success(ProviderIdentifier));

    private static ValueTask<CapabilityResult> MintAsync(CapabilityInvocation invocation)
    {
        if (invocation.Facilities is not { } facilities)
        {
            return new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.InvalidArguments(
                "spatial.fixture.mint@1 needs the runtime facilities; only the supervisor can mint handles.")));
        }

        return new ValueTask<CapabilityResult>(CapabilityResult.Success(facilities.Resources.Create(DatasetKind)));
    }

    private static ValueTask<CapabilityResult> CrashAsync(CapabilityInvocation invocation)
    {
        Environment.FailFast(
            $"fixture crash@{invocation.Capability} invoked by worker process {Environment.ProcessId}");
        return new ValueTask<CapabilityResult>(CapabilityResult.Success(0L));
    }

    private static async ValueTask<CapabilityResult> StreamAsync(CapabilityInvocation invocation)
    {
        var streamError = ReadStreamArguments(invocation, out var chunks, out var capacity, out var delayMilliseconds);
        if (streamError is not null)
        {
            return CapabilityResult.Failure(streamError);
        }

        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(CapabilityError.InvalidArguments(
                "spatial.fixture.stream@1 needs the runtime facilities; only the supervisor can create streams."));
        }

        var channel = facilities.Streams.Create(StreamKind, capacity);
        _ = EmitAsync(channel, chunks, delayMilliseconds, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async Task EmitAsync(StreamChannel channel, int chunks, int delayMilliseconds, CancellationToken cancellationToken)
    {
        try
        {
            await WriteChunksAsync(channel, chunks, delayMilliseconds, cancellationToken);
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

    /// <summary>Writes one chunk per iteration, pausing between chunks when the caller asks for backpressure/telemetry delay.</summary>
    private static async Task WriteChunksAsync(StreamChannel channel, int chunks, int delayMilliseconds, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= chunks; i++)
        {
            await channel.Writer.WriteAsync($"chunk-{i - 1}", cancellationToken);
            if (delayMilliseconds > 0)
            {
                await Task.Delay(delayMilliseconds, cancellationToken);
            }
        }
    }

    private static CapabilityError? ReadStreamArguments(
        CapabilityInvocation invocation,
        out int chunks,
        out int capacity,
        out int delayMilliseconds)
    {
        chunks = ReadInt(invocation, "chunks", 5, out var chunksError);
        capacity = ReadInt(invocation, "capacity", 2, out var capacityError);
        delayMilliseconds = ReadInt(invocation, "delayMilliseconds", 0, out var delayError);
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

    private static int ReadInt(CapabilityInvocation invocation, string name, int fallback, out CapabilityError? error)
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
