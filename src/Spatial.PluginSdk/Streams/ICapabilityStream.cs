namespace Spatial.PluginSdk.Streams;

/// <summary>
/// The consumer's read side of a bounded stream (plan §8): reads honour
/// backpressure (a read waits for the next item instead of spinning), the
/// bounded buffer and cancellation. Consumers obtain the stream from the
/// runtime's resource registry against an active lease of the stream's
/// handle — streams are resources.
/// </summary>
public interface ICapabilityStream
{
    /// <summary>
    /// Reads up to <paramref name="maxItems"/> items, waiting for at least
    /// one item while the buffer is empty; returns an empty list once the
    /// stream is complete. Honors <paramref name="cancellationToken"/>.
    /// </summary>
    ValueTask<IReadOnlyList<object?>> ReadBatchAsync(int maxItems, CancellationToken cancellationToken = default);

    /// <summary>Reads up to <paramref name="maxItems"/> items without blocking; false when the buffer is empty.</summary>
    bool TryReadBatch(int maxItems, out IReadOnlyList<object?> items);

    /// <summary>Waits until the producer completes and returns the terminal state.</summary>
    ValueTask<StreamCompletion> WaitForCompletionAsync(CancellationToken cancellationToken = default);
}
