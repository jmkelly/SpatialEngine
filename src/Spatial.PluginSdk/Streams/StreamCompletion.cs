using Spatial.PluginSdk.Capabilities;

namespace Spatial.PluginSdk.Streams;

/// <summary>
/// The terminal state of a bounded stream: a null
/// <see cref="ICapabilityError"/> means the producer completed normally (all
/// items written); an error means the producer failed or the stream was
/// closed early. Consumers receive this from
/// <see cref="ICapabilityStream.WaitForCompletionAsync"/>.
/// </summary>
public sealed record StreamCompletion(ICapabilityError? Error)
{
    public static StreamCompletion Success { get; } = new(Error: null);

    public bool IsFailed => Error is not null;
}
