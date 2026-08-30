using Spatial.PluginSdk.Resources;

namespace Spatial.PluginSdk.Streams;

/// <summary>
/// The provider-side streaming surface (plan §8): creates a bounded stream
/// owned by the current provider — a registered resource whose payload is a
/// cancellable, backpressured pipe of core-typed items. The returned
/// <see cref="StreamChannel.Handle"/> is what providers return for streaming
/// capabilities; the <see cref="StreamChannel.Writer"/> is the write side.
/// </summary>
public interface IStreamFactory
{
    /// <summary>
    /// Creates a bounded stream with the given buffer <paramref name="capacity"/>.
    /// Capacity must be at least 1; writes block (backpressure) while the
    /// buffer is full.
    /// </summary>
    StreamChannel Create(ResourceKind kind, int capacity);
}
