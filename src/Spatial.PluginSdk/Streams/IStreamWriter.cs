using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Streams;

/// <summary>
/// The provider's write side of a bounded stream (plan §8 "streams with
/// bounded buffering and backpressure"): writing into a full buffer waits
/// until the consumer reads (backpressure) and honours cancellation; the
/// producer ends the stream with <see cref="Complete"/>, optionally failing
/// it with an <see cref="ICapabilityError"/>. Runtime-backed — providers get
/// a stream (and its write side) through
/// <c>invocation.Facilities.Streams.Create(kind, capacity)</c> and return the
/// channel's handle as the invocation value.
/// </summary>
public interface IStreamWriter
{
    /// <summary>The bounded buffer capacity in items.</summary>
    int Capacity { get; }

    /// <summary>
    /// Writes one item, waiting (backpressure) while the bounded buffer is
    /// full and honouring <paramref name="cancellationToken"/>.
    /// </summary>
    ValueTask WriteAsync(object? item, CancellationToken cancellationToken = default);

    /// <summary>Writes one item when the buffer has room; false when full.</summary>
    bool TryWrite(object? item);

    /// <summary>
    /// Ends the stream. Pass an <see cref="ICapabilityError"/> to fail the
    /// stream instead of completing it successfully. Idempotent.
    /// </summary>
    void Complete(ICapabilityError? failure = null);
}
