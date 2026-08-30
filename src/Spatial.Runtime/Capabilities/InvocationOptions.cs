using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Caller-side routing hints for one invocation. All are optional: an
/// <see cref="ExplicitProvider"/> pins the provider (a hard constraint — the
/// invocation fails when that provider cannot serve), a resource (whose
/// owning provider becomes the resource-local preference) and a
/// <see cref="ResourceLocalProvider"/> are soft and fall through when they
/// cannot serve.
/// </summary>
public sealed record InvocationOptions(
    ProviderId? ExplicitProvider = null,
    ProviderId? ResourceLocalProvider = null,
    ResourceHandle? Resource = null)
{
    public static InvocationOptions None { get; } = new();

    public override string ToString() =>
        $"InvocationOptions(explicit: {ExplicitProvider?.ToString() ?? "none"}, "
        + $"resource-local: {ResourceLocalProvider?.ToString() ?? "none"}, "
        + $"resource: {Resource?.ToString() ?? "none"})";
}
