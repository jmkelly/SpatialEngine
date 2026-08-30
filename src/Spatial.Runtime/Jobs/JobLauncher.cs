using Spatial.PluginSdk.Capabilities;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Resources;

namespace Spatial.Runtime.Jobs;

/// <summary>
/// Starts a long-running invocation as a tracked job (ADR-0008): creates the
/// job in the registry, attaches the runtime facilities for the serving
/// provider and hands the run to the <see cref="JobRunner"/>. The routing
/// facade delegates here so the job mechanics stay out of the invocation
/// layer.
/// </summary>
internal static class JobLauncher
{
    /// <summary>Whether an invocation of this capability should run as a job (the LongRunning trait).</summary>
    public static bool IsLongRunning(ResolvedProvider resolved) =>
        (resolved.Descriptor.Traits & CapabilityTraits.LongRunning) != 0;

    public static CapabilityJob Launch(
        JobRegistry jobs,
        ResourceRegistry resources,
        CapabilityInvocation invocation,
        ResolvedProvider resolved)
    {
        var job = jobs.Create(invocation);
        var facilities = CapabilityFacilities.Create(resources, resolved.Provider.Id, job);
        _ = JobRunner.RunAsync(resources, facilities, job, resolved, invocation);
        return job;
    }
}