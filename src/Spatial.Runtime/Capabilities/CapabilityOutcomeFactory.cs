using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Jobs;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Builds <see cref="CapabilityOutcome"/> values with their
/// <see cref="InvocationProvenance"/> from the routing facts the runtime
/// already has. Kept separate so <see cref="CapabilityRuntime"/> stays a thin
/// routing facade instead of a fan-out hub.
/// </summary>
internal static class CapabilityOutcomeFactory
{
    /// <summary>An outcome that reached a provider, holding its result (optionally job-routed, ADR-0008).</summary>
    public static CapabilityOutcome Routed(
        CapabilityResult result,
        ResolvedProvider resolved,
        DateTimeOffset startedAt,
        DateTimeOffset? deadline,
        JobId? jobId = null) =>
        new(result, new InvocationProvenance(
            resolved.Descriptor.Id, startedAt, Elapsed(startedAt), resolved.Provider.Id, resolved.Step, deadline, jobId));

    /// <summary>An outcome rejected before any provider ran (deadline already passed).</summary>
    public static CapabilityOutcome Expired(
        CapabilityInvocation invocation,
        DateTimeOffset startedAt,
        DateTimeOffset due,
        JobId? jobId = null) =>
        new(
            CapabilityResult.Failure(CapabilityError.DeadlineExceeded(invocation.Capability)),
            new InvocationProvenance(invocation.Capability, startedAt, Elapsed(startedAt), null, null, due, jobId));

    /// <summary>An unresolved outcome carrying the actionable error.</summary>
    public static CapabilityOutcome Unavailable(
        CapabilityInvocation invocation,
        CapabilityError error,
        DateTimeOffset startedAt,
        JobId? jobId = null) =>
        new(
            CapabilityResult.Failure(error),
            new InvocationProvenance(invocation.Capability, startedAt, Elapsed(startedAt), null, null, invocation.Deadline, jobId));

    private static TimeSpan Elapsed(DateTimeOffset startedAt) => DateTimeOffset.UtcNow - startedAt;
}
