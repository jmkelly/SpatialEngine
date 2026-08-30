using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Structured provenance for one invocation (plan §9 "provenance fields"):
/// which capability, which provider served it, which resolution step picked
/// that provider, when it started, how long it took and the deadline that
/// bounded it. Failures that never reached a provider carry a null provider
/// and step.
/// </summary>
public sealed record InvocationProvenance(
    CapabilityId Capability,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    ProviderId? Provider,
    ResolutionStep? Step,
    DateTimeOffset? Deadline)
{
    public override string ToString() =>
        Provider is { } provider
            ? $"{Capability} served by {provider} via {Step} in {Duration.TotalMilliseconds:0.##} ms"
            : $"{Capability}: no provider ({(Step?.ToString() ?? "unresolved")})";
}