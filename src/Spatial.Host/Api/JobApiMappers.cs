using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Jobs;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Resources;

namespace Spatial.Host.Api;

/// <summary>
/// Job- and resource-surface mapping into the SDK's HTTP shapes (ADR-0022/
/// 0024): job snapshots, append-only event DTOs (progress, published
/// resources, diagnostics) and resource metadata. Kept apart from the
/// capability and plugin mappers so no mapper exceeds the metrics coupling
/// ceiling.
/// </summary>
internal static class JobApiMappers
{
    public static JobResponse ToJobResponse(CapabilityJob job) =>
        new(
            job.Id.ToString(),
            job.Capability.ToString(),
            job.State,
            job.CreatedAt,
            job.StartedAt,
            job.CompletedAt,
            job.Deadline,
            job.Resolved?.Provider.Id.ToString(),
            job.Resolved?.Step.ToString(),
            TerminalErrorCode(job));

    public static JobEventsResponse ToJobEventsResponse(CapabilityJob job, ResourceRegistry resources) =>
        new(
            job.Id.ToString(),
            job.State,
            job.Events.Select(jobEvent => JobEventMappers.ToEventDto(jobEvent, resources)).ToArray());

    /// <summary>The stable error code of a terminal failure, or null for a job without one.</summary>
    private static string? TerminalErrorCode(CapabilityJob job)
    {
        foreach (var jobEvent in job.Events)
        {
            if (jobEvent.Error is { } error)
            {
                return error.Code;
            }
        }

        return null;
    }
}

/// <summary>Append-only job events into the SDK's HTTP shape (ADR-0024).</summary>
internal static class JobEventMappers
{
    public static JobEventDto ToEventDto(JobEvent jobEvent, ResourceRegistry resources) =>
        new(
            jobEvent.Kind,
            jobEvent.Timestamp,
            jobEvent.Progress?.Fraction,
            jobEvent.Progress?.Message,
            jobEvent.Resource is { } resource
                ? ResourceApiMappers.ToResourceDto(resource, resources.GetState(resource))
                : null,
            jobEvent.Note,
            jobEvent.Error is { } error ? CapabilityApiMappers.ToErrorDto(error) : null);
}

/// <summary>Resource metadata mapping (ADR-0022).</summary>
internal static class ResourceApiMappers
{
    public static ResourceDto ToResourceDto(ResourceHandle handle, ResourceState state) =>
        new(handle.Id.ToString(), handle.Kind.Name, handle.Owner.ToString(), handle.CreatedAt, state);
}
