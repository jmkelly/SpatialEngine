using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;
using Spatial.PluginSdk.Resources;
using Spatial.PluginSdk.Streams;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Resources;
using Spatial.Runtime.Streams;
using Spatial.Runtime.Tests.Fixtures;

namespace Spatial.Runtime.Tests;

/// <summary>
/// Phase 4 bounded streaming (Epic E "streams and backpressure", plan §8
/// "streams for feature batches … with bounded buffering and backpressure"):
/// streaming capabilities must expose a stream (enforced), reads respect the
/// bounded buffer and cancellation, a full buffer makes writes wait until the
/// consumer reads, and long streams are jobs with progress (ADR-0008).
/// </summary>
public sealed class StreamTests
{
    [Fact]
    public async Task Stream_capability_returns_a_stream_handle_with_all_chunks()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(StreamInvocation(3));
        Assert.True(outcome.TryGetValue(out var value));
        var handle = Assert.IsType<ResourceHandle>(value);
        Assert.Equal(1, host.Resources.OpenCount);
        Assert.True(host.Resources.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        Assert.True(host.Resources.TryOpenStream(handle, lease, out var stream));

        var items = await DrainAsync(stream);
        Assert.Equal(["chunk-0", "chunk-1", "chunk-2"], items);
        var completion = await stream.WaitForCompletionAsync();
        Assert.False(completion.IsFailed);

        Assert.True(host.Resources.ReleaseLease(lease));
        await host.Resources.CloseAsync(handle);
        Assert.Equal(0, host.Resources.OpenCount);
    }

    [Fact]
    public async Task Read_batches_are_bounded_and_completion_is_drained()
    {
        var host = InMemoryComponentHost.Create();

        var handle = Assert.IsType<ResourceHandle>(await InvokeStream(host, 4));
        Assert.True(host.Resources.TryAcquireLease(handle, null, out var lease));
        Assert.True(host.Resources.TryOpenStream(handle, lease, out var stream));

        // A bounded read returns at most the requested count (FIFO first item).
        var first = await stream.ReadBatchAsync(1);
        Assert.Equal(["chunk-0"], first);

        var rest = await DrainAsync(stream);
        Assert.Equal(["chunk-1", "chunk-2", "chunk-3"], rest);
        Assert.False(stream.TryReadBatch(1, out _));
    }

    [Fact]
    public async Task Reading_requires_an_active_lease()
    {
        var host = InMemoryComponentHost.Create();
        var handle = Assert.IsType<ResourceHandle>(await InvokeStream(host, 2));

        Assert.False(host.Resources.TryOpenStream(handle, null, out _));

        Assert.True(host.Resources.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        Assert.True(host.Resources.TryOpenStream(handle, lease, out _));
    }

    [Fact]
    public async Task An_expired_lease_does_not_open_the_stream()
    {
        var now = DateTimeOffset.UtcNow;
        var host = CreateHostWithClock(() => now);
        var handle = Assert.IsType<ResourceHandle>(await InvokeStream(host, 2));

        Assert.True(host.Resources.TryAcquireLease(handle, TimeSpan.FromSeconds(10), out var lease));
        now = now.AddSeconds(11);

        Assert.False(host.Resources.TryOpenStream(handle, lease, out _));
    }

    [Fact]
    public async Task Backpressure_blocks_writes_until_the_consumer_reads()
    {
        var stream = new BoundedStream(1);

        await stream.WriteAsync("first");
        var blocked = stream.WriteAsync("second");
        Assert.False(blocked.IsCompleted);

        var items = await stream.ReadBatchAsync(1);
        Assert.Equal(["first"], items);

        await blocked;
        Assert.Equal(["second"], await stream.ReadBatchAsync(1));
    }

    [Fact]
    public async Task TryWrite_is_non_blocking_and_respects_the_buffer_and_disposal()
    {
        var stream = new BoundedStream(1);

        Assert.True(stream.TryWrite("a"));
        Assert.False(stream.TryWrite("b"));
        Assert.True(stream.TryReadBatch(1, out var first));
        Assert.Equal(["a"], first);

        await stream.DisposeAsync();
        Assert.False(stream.TryWrite("c"));
    }

    [Fact]
    public async Task A_failed_stream_completes_with_the_error()
    {
        var stream = new BoundedStream(2);
        await stream.WriteAsync("a");

        stream.Complete(CapabilityError.ProviderFailure("worker died"));
        Assert.False(stream.TryWrite("b"));

        var completion = await stream.WaitForCompletionAsync();
        Assert.True(completion.IsFailed);
        Assert.Equal("provider.failure", completion.Error?.Code);
        Assert.Contains("worker died", completion.Error?.Message);
        Assert.Equal(["a"], await stream.ReadBatchAsync(10));
    }

    [Fact]
    public async Task Completing_a_stream_is_idempotent()
    {
        var stream = new BoundedStream(2);
        stream.Complete();
        stream.Complete(CapabilityError.ProviderFailure("late failure"));

        var completion = await stream.WaitForCompletionAsync();
        Assert.False(completion.IsFailed);
    }

    [Fact]
    public async Task Reading_respects_cancellation()
    {
        var stream = new BoundedStream(2);
        using var cts = new CancellationTokenSource();

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => stream.ReadBatchAsync(1, cts.Token).AsTask());
    }

    [Fact]
    public async Task Writing_after_disposal_fails()
    {
        var stream = new BoundedStream(2);
        await stream.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => stream.WriteAsync("late").AsTask());
        Assert.False(stream.TryWrite("late"));
    }

    [Fact]
    public async Task A_closed_stream_truncates_a_blocked_writer()
    {
        var stream = new BoundedStream(1);
        await stream.WriteAsync("a");
        var blocked = stream.WriteAsync("b");
        Assert.False(blocked.IsCompleted);

        await stream.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await blocked);
    }

    [Fact]
    public void Stream_capacity_and_arguments_are_validated()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BoundedStream(0));
        var stream = new BoundedStream(3);
        Assert.Equal(3, stream.Capacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => stream.TryReadBatch(0, out _));
    }

    [Fact]
    public async Task A_streaming_capability_returning_a_value_is_a_contract_violation()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [ExampleFeatureProvider.StreamCapability],
                handler: _ => new ValueTask<CapabilityResult>(CapabilityResult.Success("nope")),
                traits: CapabilityTraits.Streaming | CapabilityTraits.Cancellable));

        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.StreamCapability, new Dictionary<string, object?>()));

        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error?.Kind);
        Assert.Contains("not a stream", outcome.Error?.Message);
    }

    [Fact]
    public async Task A_streaming_capability_returning_a_plain_resource_is_a_contract_violation()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [ExampleFeatureProvider.StreamCapability],
                handler: invocation => new ValueTask<CapabilityResult>(
                    CapabilityResult.Success(invocation.Facilities!.Resources.Create(ResourceKind.Parse("dataset")))),
                traits: CapabilityTraits.Streaming | CapabilityTraits.Cancellable));

        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.StreamCapability, new Dictionary<string, object?>()));

        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error?.Kind);
        Assert.Contains("not backed by a bounded stream", outcome.Error?.Message);
    }

    [Fact]
    public async Task A_non_streaming_capability_returning_a_stream_handle_is_a_contract_violation()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [ExampleFeatureProvider.MintCapability],
                handler: invocation =>
                {
                    var channel = invocation.Facilities!.Streams.Create(ResourceKind.Parse("fixture.stream"), 2);
                    return new ValueTask<CapabilityResult>(CapabilityResult.Success(channel.Handle));
                }));

        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.MintCapability, new Dictionary<string, object?>()));

        Assert.Equal(CapabilityErrorKind.ContractViolation, outcome.Error?.Kind);
        Assert.Contains("Streaming trait", outcome.Error?.Message);
    }

    [Fact]
    public async Task A_failed_streaming_invocation_passes_through_unmodified()
    {
        var (_, runtime) = CreateRuntime(
            new StubProvider(
                "alpha", 1, [ExampleFeatureProvider.StreamCapability],
                handler: _ => new ValueTask<CapabilityResult>(CapabilityResult.Failure(CapabilityError.InvalidArguments("bad"))),
                traits: CapabilityTraits.Streaming | CapabilityTraits.Cancellable));

        var outcome = await runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.StreamCapability, new Dictionary<string, object?>()));

        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);
    }

    [Fact]
    public async Task A_long_stream_is_a_job_that_publishes_the_handle_and_reports_progress()
    {
        var host = InMemoryComponentHost.Create();

        var job = host.Runtime.StartJob(JobStreamInvocation(5, 2, 30));
        var handle = await WaitForPublishedStreamAsync(job);
        Assert.Equal(1, host.Resources.OpenCount);
        Assert.True(host.Resources.TryAcquireLease(handle, null, out var lease));
        Assert.True(host.Resources.TryOpenStream(handle, lease, out var stream));

        // Consume two chunks: the bounded buffer (capacity 2) now holds the
        // producer's next writes and the job cannot finish until we drain —
        // that is backpressure holding the job.
        await ReadExactlyAsync(stream, 2);
        await Task.Delay(300);
        Assert.Equal(JobState.Running, job.State);

        var rest = await DrainAsync(stream);
        Assert.Equal(["chunk-2", "chunk-3", "chunk-4"], rest);

        var result = await job.WaitForCompletionAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(handle, Assert.IsType<CapabilitySuccess>(result).Value);
        Assert.Equal(JobState.Completed, job.State);
        Assert.NotNull(job.CompletedAt);

        var progressEvents = job.Events.Where(jobEvent => jobEvent.Kind == JobEventKind.Progress).ToArray();
        Assert.NotEmpty(progressEvents);
        Assert.All(progressEvents, jobEvent => Assert.InRange(jobEvent.Progress!.Fraction!.Value, 0, 1));
        Assert.Contains(job.Events, jobEvent => jobEvent.Kind == JobEventKind.Resource);
        Assert.Equal(JobEventKind.Created, job.Events[0].Kind);

        Assert.True(host.Resources.ReleaseLease(lease));
        await host.Resources.CloseAsync(handle);
    }

    [Fact]
    public async Task Cancelling_a_long_stream_job_fails_the_stream()
    {
        var host = InMemoryComponentHost.Create();
        var job = host.Runtime.StartJob(JobStreamInvocation(50, 2, 10));
        var handle = await WaitForPublishedStreamAsync(job);
        Assert.True(host.Resources.TryAcquireLease(handle, null, out var lease));
        Assert.True(host.Resources.TryOpenStream(handle, lease, out var stream));

        await job.CancelAsync();

        var result = await job.WaitForCompletionAsync();
        Assert.False(result.IsSuccess);
        Assert.Equal(CapabilityErrorKind.Cancelled, Assert.IsType<CapabilityFailure>(result).Error.Kind);
        Assert.Equal(JobState.Cancelled, job.State);
        Assert.True((await stream.WaitForCompletionAsync()).IsFailed);

        await host.Resources.CloseAsync(handle);
    }

    [Fact]
    public async Task Stream_arguments_reject_bad_values()
    {
        var host = InMemoryComponentHost.Create();

        var outcome = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.StreamCapability, new Dictionary<string, object?> { ["chunks"] = "three" }));

        Assert.Equal(CapabilityErrorKind.InvalidArguments, outcome.Error?.Kind);

        var zero = await host.Runtime.InvokeAsync(
            CapabilityInvocation.Create(ExampleFeatureProvider.StreamCapability, new Dictionary<string, object?> { ["capacity"] = 0 }));
        Assert.Equal(CapabilityErrorKind.InvalidArguments, zero.Error?.Kind);
    }

    private static CapabilityInvocation StreamInvocation(int chunks, int capacity = 2) =>
        CapabilityInvocation.Create(
            ExampleFeatureProvider.StreamCapability,
            new Dictionary<string, object?> { ["chunks"] = chunks, ["capacity"] = capacity });

    private static CapabilityInvocation JobStreamInvocation(int chunks, int capacity, int delay) =>
        CapabilityInvocation.Create(
            ExampleFeatureProvider.JobStreamCapability,
            new Dictionary<string, object?> { ["chunks"] = chunks, ["capacity"] = capacity, ["delayMilliseconds"] = delay });

    private static async Task<object?> InvokeStream(InMemoryComponentHost host, int chunks)
    {
        var outcome = await host.Runtime.InvokeAsync(StreamInvocation(chunks));
        Assert.True(outcome.TryGetValue(out var value));
        return value;
    }

    private static async Task<ResourceHandle> WaitForPublishedStreamAsync(CapabilityJob job)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var published = job.Events
                .Where(jobEvent => jobEvent.Kind == JobEventKind.Resource)
                .Select(jobEvent => jobEvent.Resource)
                .FirstOrDefault(handle => handle is not null);
            if (published is not null)
            {
                return published;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Job {job.Id} never published a stream handle.");
    }

    private static async Task ReadExactlyAsync(ICapabilityStream stream, int count)
    {
        var read = 0;
        while (read < count)
        {
            read += (await stream.ReadBatchAsync(count - read)).Count;
        }
    }

    private static async Task<List<object?>> DrainAsync(ICapabilityStream stream)
    {
        var all = new List<object?>();
        while (true)
        {
            var batch = await stream.ReadBatchAsync(10);
            if (batch.Count == 0)
            {
                return all;
            }

            all.AddRange(batch);
        }
    }

    private static (CapabilityRegistry Registry, CapabilityRuntime Runtime) CreateRuntime(params ICapabilityProvider[] providers)
    {
        var registry = new CapabilityRegistry();
        foreach (var provider in providers)
        {
            registry.Register(provider);
        }

        return (registry, new CapabilityRuntime(registry));
    }

    private static InMemoryComponentHost CreateHostWithClock(Func<DateTimeOffset> clock)
    {
        var registry = new CapabilityRegistry();
        registry.Register(new ExampleFeatureProvider());
        var runtime = new CapabilityRuntime(registry, resourceRegistry: new ResourceRegistry(clock));
        return new InMemoryComponentHost(registry, runtime);
    }
}
