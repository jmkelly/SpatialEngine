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
    private static string? TerminalErrorCode(CapabilityJob job) =>
        job.Events.Select(jobEvent => jobEvent.Error).FirstOrDefault(error => error is not null)?.Code;
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
            ResourceDto(jobEvent.Resource, resources),
            jobEvent.Note,
            ErrorDto(jobEvent.Error));

    /// <summary>The event's published-resource DTO, or null when the event carries none.</summary>
    private static ResourceDto? ResourceDto(ResourceHandle? resource, ResourceRegistry resources) =>
        resource is { } present
            ? ResourceApiMappers.ToResourceDto(present, resources.GetState(present))
            : null;

    /// <summary>The event's structured error DTO, or null when the event is not a failure.</summary>
    private static CapabilityErrorDto? ErrorDto(ICapabilityError? error) =>
        error is { } present ? CapabilityApiMappers.ToErrorDto(present) : null;
}

/// <summary>Resource metadata mapping (ADR-0022).</summary>
internal static class ResourceApiMappers
{
    public static ResourceDto ToResourceDto(ResourceHandle handle, ResourceState state) =>
        new(handle.Id.ToString(), handle.Kind.Name, handle.Owner.ToString(), handle.CreatedAt, state);
}
