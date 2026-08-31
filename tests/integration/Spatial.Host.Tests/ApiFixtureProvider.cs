using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;

namespace Spatial.Host.Tests;

/// <summary>
/// The host integration fixture provider: a geometry-free in-memory provider
/// exercising every HTTP/runtime surface — inline completion, structured
/// failures, permission checks, resource minting and routing, bounded
/// streaming, and long-running cancellable jobs that publish progress and
/// stream resources. Registered through <c>SpatialHostRuntime.For</c> in
/// <c>HostApiTests</c> so the endpoints are tested against a real runtime
/// without any plugin process.
/// </summary>
public sealed class ApiFixtureProvider : CapabilityProviderBase
{
    public static readonly ProviderId ProviderIdentifier = ProviderId.Parse("fixture@1");

    public static readonly CapabilityId Echo = CapabilityId.Parse("fixture.echo@1");
    public static readonly CapabilityId Peek = CapabilityId.Parse("fixture.peek@1");
    public static readonly CapabilityId Denied = CapabilityId.Parse("fixture.denied@1");
    public static readonly CapabilityId Fail = CapabilityId.Parse("fixture.fail@1");
    public static readonly CapabilityId Mint = CapabilityId.Parse("fixture.mint@1");
    public static readonly CapabilityId Stream = CapabilityId.Parse("fixture.stream@1");
    public static readonly CapabilityId Bytes = CapabilityId.Parse("fixture.bytes@1");
    public static readonly CapabilityId FailingStream = CapabilityId.Parse("fixture.failingstream@1");
    public static readonly CapabilityId JobStream = CapabilityId.Parse("fixture.jobstream@1");
    public static readonly CapabilityId Sleep = CapabilityId.Parse("fixture.sleep@1");

    public static readonly Permission AdminPermission = Permission.Parse("fixture.admin");

    public static readonly ResourceKind HandleKind = ResourceKind.Parse("fixture.handle");
    public static readonly ResourceKind StreamKind = ResourceKind.Parse("fixture.stream");

    public override ProviderId Id => ProviderIdentifier;

    public ApiFixtureProvider()
        : base(
        [
            new CapabilityDescriptor(
                Echo,
                "Echoes the 'text' argument — an inline fixture for completed invocations.",
                new SchemaDescriptor("scalar", "A 'text' string."),
                new SchemaDescriptor("scalar", "The echoed string."),
                [new ErrorVariant("invalid.arguments", "The 'text' argument is missing or not a string.")],
                [],
                CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                Peek,
                "Returns the provider id that serves the invocation — the resource-local routing fixture.",
                new SchemaDescriptor("none", "No arguments."),
                new SchemaDescriptor("scalar", "The serving provider id."),
                [new ErrorVariant("invalid.arguments", "The invocation is malformed.")],
                [],
                CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                Denied,
                "Requires 'fixture.admin' — the permission gate fixture.",
                new SchemaDescriptor("none", "No arguments."),
                new SchemaDescriptor("scalar", "The answer."),
                [new ErrorVariant("permission.denied", "The caller lacks fixture.admin.")],
                [AdminPermission],
                CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                Fail,
                "Always fails with a structured provider failure — the failure fixture.",
                new SchemaDescriptor("none", "No arguments."),
                new SchemaDescriptor("none", "Never succeeds."),
                [new ErrorVariant("provider.failure", "The fixture always fails.")],
                [],
                CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                Mint,
                "Mints a runtime-owned resource handle — the resource metadata/delete fixture.",
                new SchemaDescriptor("none", "No arguments."),
                new SchemaDescriptor("resource", "A ResourceHandle owned by the fixture."),
                [new ErrorVariant("invalid.arguments", "The invocation carries no runtime facilities.")],
                [],
                CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                Stream,
                "Streams 'chunks' string items through a bounded stream and returns the handle — the stream fixture.",
                new SchemaDescriptor("scalar", "int 'chunks' (default 3), int 'delayMilliseconds' (default 0)."),
                new SchemaDescriptor("stream.string", "A bounded stream of string chunks."),
                [new ErrorVariant("invalid.arguments", "A 'chunks' or 'delayMilliseconds' argument is not a non-negative int32.")],
                [],
                CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                Bytes,
                "Streams byte[] items (canonical binary interchange) through a bounded stream — the $bytes stream fixture.",
                new SchemaDescriptor("scalar", "int 'chunks' (default 2)."),
                new SchemaDescriptor("stream.bytes", "A bounded stream of byte[] chunks."),
                [new ErrorVariant("invalid.arguments", "A 'chunks' argument is not a non-negative int32.")],
                [],
                CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                FailingStream,
                "Writes one item then completes the stream with a structured failure — the stream failure fixture.",
                new SchemaDescriptor("none", "No arguments."),
                new SchemaDescriptor("stream.string", "A bounded stream that fails."),
                [new ErrorVariant("stream.failed", "The fixture fails the stream after one item.")],
                [],
                CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                JobStream,
                "Long-running streaming with progress — publishes the stream on the job and completes it.",
                new SchemaDescriptor("scalar", "int 'chunks' (default 3), int 'delayMilliseconds' (default 20)."),
                new SchemaDescriptor("stream.string", "A bounded stream of string chunks."),
                [new ErrorVariant("invalid.arguments", "A 'chunks' or 'delayMilliseconds' argument is not a non-negative int32.")],
                [],
                CapabilityTraits.LongRunning | CapabilityTraits.Streaming | CapabilityTraits.Cancellable,
                []),
            new CapabilityDescriptor(
                Sleep,
                "Sleeps in chunks reporting progress — the long-running job fixture.",
                new SchemaDescriptor("scalar", "int64 'milliseconds' (default 300)."),
                new SchemaDescriptor("scalar", "The milliseconds slept."),
                [new ErrorVariant("invalid.arguments", "The 'milliseconds' argument is not a non-negative int64.")],
                [],
                CapabilityTraits.LongRunning | CapabilityTraits.Cancellable,
                []),
        ])
    {
    }

    public override ValueTask<CapabilityResult> InvokeAsync(CapabilityInvocation invocation) =>
        invocation.Capability switch
        {
            var id when id == Echo => EchoAsync(invocation),
            var id when id == Peek => PeekAsync(invocation),
            var id when id == Denied => DeniedAsync(invocation),
            var id when id == Fail => FailAsync(invocation),
            var id when id == Mint => MintAsync(invocation),
            var id when id == Stream => StreamAsync(invocation),
            var id when id == Bytes => BytesAsync(invocation),
            var id when id == FailingStream => FailingStreamAsync(invocation),
            var id when id == JobStream => JobStreamAsync(invocation),
            var id when id == Sleep => SleepAsync(invocation),
            _ => new ValueTask<CapabilityResult>(CapabilityResult.Failure(
                CapabilityError.ContractViolation($"The fixture provider does not serve {invocation.Capability}."))),
        };

    private static ValueTask<CapabilityResult> EchoAsync(CapabilityInvocation invocation)
    {
        if (!invocation.TryGetArgument<string>("text", out var text))
        {
            return new ValueTask<CapabilityResult>(CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.echo@1 requires a 'text' string argument.")));
        }

        return new ValueTask<CapabilityResult>(CapabilityResult.Success(text));
    }

    private static ValueTask<CapabilityResult> PeekAsync(CapabilityInvocation invocation) =>
        new(CapabilityResult.Success(ProviderIdentifier.ToString()));

    private static ValueTask<CapabilityResult> DeniedAsync(CapabilityInvocation invocation) =>
        new(CapabilityResult.Success("granted"));

    private static ValueTask<CapabilityResult> FailAsync(CapabilityInvocation invocation) =>
        new(CapabilityResult.Failure(CapabilityError.ProviderFailure("the fixture always fails as requested")));

    private static ValueTask<CapabilityResult> MintAsync(CapabilityInvocation invocation)
    {
        if (invocation.Facilities is not { } facilities)
        {
            return new ValueTask<CapabilityResult>(CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.mint@1 carries no runtime facilities.")));
        }

        return new ValueTask<CapabilityResult>(
            CapabilityResult.Success(facilities.Resources.Create(HandleKind)));
    }

    private static async ValueTask<CapabilityResult> StreamAsync(CapabilityInvocation invocation)
    {
        if (NotReadable(invocation, out var chunks, out var delay))
        {
            return CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.stream@1 requires non-negative 'chunks' and 'delayMilliseconds' int32 arguments."));
        }

        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.stream@1 carries no runtime facilities."));
        }

        var channel = facilities.Streams.Create(StreamKind, capacity: 2);
        _ = EmitAsync(invocation, channel, chunks, delay, reportProgress: false);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async ValueTask<CapabilityResult> BytesAsync(CapabilityInvocation invocation)
    {
        var chunks = invocation.TryGetArgument<int>("chunks", out var requested) && requested >= 0 ? requested : 2;
        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.bytes@1 carries no runtime facilities."));
        }

        var channel = facilities.Streams.Create(StreamKind, capacity: 2);
        _ = EmitBytesAsync(channel, chunks, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async ValueTask<CapabilityResult> FailingStreamAsync(CapabilityInvocation invocation)
    {
        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.failingstream@1 carries no runtime facilities."));
        }

        var channel = facilities.Streams.Create(StreamKind, capacity: 2);
        _ = FailStreamAsync(channel, invocation.CancellationToken);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async Task EmitBytesAsync(StreamChannel channel, int chunks, CancellationToken cancellationToken)
    {
        for (var i = 1; i <= chunks; i++)
        {
            await channel.Writer.WriteAsync(new byte[] { (byte)i, (byte)i }, cancellationToken);
        }

        channel.Writer.Complete();
    }

    private static async Task FailStreamAsync(StreamChannel channel, CancellationToken cancellationToken)
    {
        await channel.Writer.WriteAsync("only item", cancellationToken);
        channel.Writer.Complete(CapabilityError.ProviderFailure("the stream fixture failed on purpose"));
    }

    private static async ValueTask<CapabilityResult> JobStreamAsync(CapabilityInvocation invocation)
    {
        if (NotReadable(invocation, out var chunks, out var delay))
        {
            return CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.jobstream@1 requires non-negative 'chunks' and 'delayMilliseconds' int32 arguments."));
        }

        if (invocation.Facilities is not { } facilities)
        {
            return CapabilityResult.Failure(
                CapabilityError.InvalidArguments("fixture.jobstream@1 carries no runtime facilities."));
        }

        var channel = facilities.Streams.Create(StreamKind, capacity: 2);
        await EmitAsync(invocation, channel, chunks, delay, reportProgress: true);
        return CapabilityResult.Success(channel.Handle);
    }

    private static async ValueTask<CapabilityResult> SleepAsync(CapabilityInvocation invocation)
    {
        var milliseconds = invocation.TryGetArgument<long>("milliseconds", out var requested) && requested >= 0
            ? requested
            : 300L;

        var step = Math.Min(milliseconds, 25L);
        for (var elapsed = 0L; elapsed < milliseconds; elapsed += step)
        {
            invocation.CancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(step), invocation.CancellationToken);
            invocation.Progress?.Report(ProgressReport.Create(
                Math.Min(1, (double)(elapsed + step) / milliseconds), $"slept {elapsed + step} of {milliseconds} ms"));
        }

        return CapabilityResult.Success(milliseconds);
    }

    private static async Task EmitAsync(
        CapabilityInvocation invocation, StreamChannel channel, int chunks, int delay, bool reportProgress)
    {
        for (var i = 1; i <= chunks; i++)
        {
            await channel.Writer.WriteAsync($"chunk {i}", invocation.CancellationToken);
            if (delay > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(delay), invocation.CancellationToken);
            }

            if (reportProgress)
            {
                invocation.Progress?.Report(ProgressReport.Create((double)i / chunks, $"wrote {i} of {chunks}"));
            }
        }

        channel.Writer.Complete();
    }

    private static bool NotReadable(CapabilityInvocation invocation, out int chunks, out int delay)
    {
        chunks = invocation.TryGetArgument<int>("chunks", out var requestedChunks) ? requestedChunks : 3;
        delay = invocation.TryGetArgument<int>("delayMilliseconds", out var requestedDelay) ? requestedDelay : 0;
        return chunks < 0 || delay < 0;
    }
}
