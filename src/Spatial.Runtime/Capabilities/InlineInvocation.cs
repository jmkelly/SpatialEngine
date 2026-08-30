using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Resources;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The inline invocation mechanics the routing facade delegates: deadline-
/// linked cancellation, runtime facilities, the guarded provider call, the
/// streaming contract check and the outcome with provenance — plus the three
/// pre-check outcome shapes (expired, unavailable, permission denied) both
/// the inline and the job routing paths share. Kept out of
/// <see cref="CapabilityRuntime"/> so the facade stays a thin route.
/// </summary>
internal static class InlineInvocation
{
    public static async Task<CapabilityOutcome> RunAsync(
        ResourceRegistry resources,
        CapabilityInvocation invocation,
        ResolvedProvider resolved,
        DateTimeOffset startedAt)
    {
        using var deadlineCts = CreateDeadlineCts(invocation.Deadline, startedAt, invocation.CancellationToken);
        var effective = invocation with
        {
            CancellationToken = deadlineCts?.Token ?? invocation.CancellationToken,
            Facilities = CapabilityFacilities.Create(resources, resolved.Provider.Id),
        };

        var result = await CapabilityInvoker.InvokeSafelyAsync(resolved, effective);
        result = StreamingContractValidator.Validate(resolved, result, resources);
        return CapabilityOutcomeFactory.Routed(result, resolved, startedAt, invocation.Deadline);
    }

    /// <summary>An outcome rejected before any provider ran (deadline already passed).</summary>
    public static CapabilityOutcome Expired(CapabilityInvocation invocation, DateTimeOffset startedAt, DateTimeOffset due) =>
        CapabilityOutcomeFactory.Expired(invocation, startedAt, due);

    /// <summary>An unresolved outcome carrying the actionable error.</summary>
    public static CapabilityOutcome Unavailable(CapabilityInvocation invocation, CapabilityError error, DateTimeOffset startedAt) =>
        CapabilityOutcomeFactory.Unavailable(invocation, error, startedAt);

    /// <summary>An outcome denied before any provider ran (missing permissions).</summary>
    public static CapabilityOutcome Denied(
        CapabilityInvocation invocation,
        ResolvedProvider resolved,
        CapabilityError error,
        DateTimeOffset startedAt) =>
        CapabilityOutcomeFactory.Routed(CapabilityResult.Failure(error), resolved, startedAt, invocation.Deadline);

    private static CancellationTokenSource? CreateDeadlineCts(
        DateTimeOffset? deadline,
        DateTimeOffset startedAt,
        CancellationToken callerToken)
    {
        if (deadline is not { } due || due <= startedAt)
        {
            return null;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        cts.CancelAfter(due - startedAt);
        return cts;
    }
}