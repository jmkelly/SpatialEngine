using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Caller-side routing hints for one invocation. Both are optional: an
/// <see cref="ExplicitProvider"/> pins the provider (a hard constraint — the
/// invocation fails when that provider cannot serve), while a
/// <see cref="ResourceLocalProvider"/> is preferred when it can serve and
/// falls through otherwise. Phase 4 lets resource-local be derived from the
/// resource that owns the invocation's data.
/// </summary>
public sealed record InvocationOptions(
    ProviderId? ExplicitProvider = null,
    ProviderId? ResourceLocalProvider = null)
{
    public static InvocationOptions None { get; } = new();

    public override string ToString() =>
        $"InvocationOptions(explicit: {ExplicitProvider?.ToString() ?? "none"}, resource-local: {ResourceLocalProvider?.ToString() ?? "none"})";
}
