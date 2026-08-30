using Spatial.PluginSdk.Resources;

namespace Spatial.PluginSdk.Streams;

/// <summary>
/// What a provider receives when it creates a bounded stream: the transferable
/// <see cref="Handle"/> to return as the invocation value and the
/// <see cref="Writer"/> to fill. The runtime keeps the consumer side behind
/// the handle; readers open it through the resource registry with a lease.
/// </summary>
public sealed record StreamChannel(ResourceHandle Handle, IStreamWriter Writer);
