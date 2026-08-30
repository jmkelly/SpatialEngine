using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Structured provenance for one invocation (plan §9 "provenance fields"):
/// which capability, which provider served it, which resolution step picked
/// that provider, when it started, how long it took, the deadline that
/// bounded it and — when routed through the job model — the job id
/// (ADR-0008). Failures that never reached a provider carry a null provider
/// and step.
/// </summary>
public sealed record InvocationProvenance(
    CapabilityId Capability,
    DateTimeOffset StartedAt,
    TimeSpan Duration,
    ProviderId? Provider,
    ResolutionStep? Step,
    DateTimeOffset? Deadline,
    JobId? JobId = null)
{
    public override string ToString() =>
        Provider is { } provider
            ? $"{Capability} served by {provider} via {Step} in {Duration.TotalMilliseconds:0.##} ms"
                + (JobId is { } job ? $" (job {job})" : string.Empty)
            : $"{Capability}: no provider ({(Step?.ToString() ?? "unresolved")})";
}
