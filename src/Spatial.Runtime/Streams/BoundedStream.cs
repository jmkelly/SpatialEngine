using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Streams;

namespace Spatial.Runtime.Streams;

/// <summary>
/// The runtime's bounded, backpressured stream (plan §8 "bounded buffering
/// and backpressure"): a fixed-capacity pipe of core-typed items created for
/// a streaming capability, written by the provider (<see cref="IStreamWriter"/>)
/// and read by consumers (<see cref="ICapabilityStream"/>) under a resource
/// lease. A full buffer makes writes wait until the consumer reads —
/// backpressure — and writes and reads honour cancellation. One writer,
/// many readers.
/// </summary>
// 'Stream' is the contract vocabulary of plan §8, not a BCL-facing suffix.
[SuppressMessage("Design", "CA1711", Justification = "'Stream' is the documented interchange vocabulary of plan §8.")]
public sealed class BoundedStream : IStreamWriter, ICapabilityStream
{
    private readonly Channel<object?> _channel;
    private readonly int _capacity;
    private readonly TaskCompletionSource<StreamCompletion> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _completed;
    private int _disposed;

    public BoundedStream(int capacity)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "A bounded stream needs a positive buffer capacity.");
        }

        _capacity = capacity;
        _channel = Channel.CreateBounded<object?>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false,
        });
    }

    public int Capacity => _capacity;

    public async ValueTask WriteAsync(object? item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken);
        }
        catch (ChannelClosedException)
        {
            throw new OperationCanceledException("The stream was closed before the write completed.", cancellationToken);
        }
    }

    public bool TryWrite(object? item)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        return _channel.Writer.TryWrite(item);
    }

    public void Complete(ICapabilityError? failure = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();
        _completion.TrySetResult(new StreamCompletion(failure));
    }

    public async ValueTask<IReadOnlyList<object?>> ReadBatchAsync(int maxItems, CancellationToken cancellationToken = default)
    {
        ThrowIfInvalidMaxItems(maxItems);
        var hasItems = await _channel.Reader.WaitToReadAsync(cancellationToken);
        if (!hasItems)
        {
            return [];
        }

        return Drain(maxItems);
    }

    public bool TryReadBatch(int maxItems, out IReadOnlyList<object?> items)
    {
        ThrowIfInvalidMaxItems(maxItems);
        items = Drain(maxItems);
        return items.Count > 0;
    }

    public async ValueTask<StreamCompletion> WaitForCompletionAsync(CancellationToken cancellationToken = default) =>
        await _completion.Task.WaitAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        // Closing the resource ends the stream: blocked writers wake with a
        // ChannelClosedException (surfaced as cancellation) and readers see
        // completion. Writers after dispose fail fast.
        _channel.Writer.TryComplete();
        _completion.TrySetResult(StreamCompletion.Success);
        return ValueTask.CompletedTask;
    }

    private List<object?> Drain(int maxItems)
    {
        var items = new List<object?>(maxItems);
        while (items.Count < maxItems && _channel.Reader.TryRead(out var item))
        {
            items.Add(item);
        }

        return items;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static void ThrowIfInvalidMaxItems(int maxItems)
    {
        if (maxItems < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxItems), maxItems, "A read batch needs a positive maximum.");
        }
    }
}
