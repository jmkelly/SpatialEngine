using Spatial.PluginHost.DotNet.Supervision;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Jobs;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Jobs;
using Spatial.Runtime.Resources;

namespace Spatial.Host.Api;

/// <summary>
/// The host API's one shared mapping layer (plan §16 Phase 9): turns runtime
/// surfaces (outcomes, jobs, resources, providers, workers) into the SDK's
/// HTTP contract shapes (<see cref="Spatial.PluginSdk.Http"/>) so every
/// endpoint maps identically and the mapping itself stays unit-testable. The
/// runtime never appears in a response, and a request never reaches the
/// runtime except through the typed invocation surface.
/// </summary>
internal static class ApiMappers
{
    public static CapabilityErrorDto ToErrorDto(ICapabilityError error) =>
        new(error.Kind.ToString(), error.Code, error.Message);

    public static ProvenanceDto ToProvenanceDto(InvocationProvenance provenance) =>
        new(
            provenance.Capability.ToString(),
            provenance.Provider?.ToString(),
            provenance.Step?.ToString(),
            provenance.StartedAt,
            provenance.Duration.TotalMilliseconds,
            provenance.Deadline,
            provenance.JobId?.ToString());

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
            job.Events.Select(jobEvent => ToEventDto(jobEvent, resources)).ToArray());

    public static JobEventDto ToEventDto(JobEvent jobEvent, ResourceRegistry resources) =>
        new(
            jobEvent.Kind,
            jobEvent.Timestamp,
            jobEvent.Progress?.Fraction,
            jobEvent.Progress?.Message,
            jobEvent.Resource is { } resource ? ToResourceDto(resource, resources.GetState(resource)) : null,
            jobEvent.Note,
            jobEvent.Error is { } error ? ToErrorDto(error) : null);

    public static ResourceDto ToResourceDto(ResourceHandle handle, ResourceState state) =>
        new(handle.Id.ToString(), handle.Kind.Name, handle.Owner.ToString(), handle.CreatedAt, state);

    public static CapabilitySummaryDto ToCapabilitySummary(CapabilityRegistry registry, CapabilityId capability)
    {
        var descriptor = FirstDescriptor(registry, capability);
        return new CapabilitySummaryDto(
            capability.ToString(),
            descriptor.Purpose,
            descriptor.Input.Name,
            descriptor.Output.Name,
            Traits(descriptor.Traits),
            descriptor.RequiredPermissions.Select(permission => permission.Name).ToArray(),
            registry.GetProviders(capability).Select(provider => provider.Id.ToString()).ToArray());
    }

    public static CapabilityDetailDto ToCapabilityDetail(CapabilityRegistry registry, CapabilityId capability)
    {
        var descriptor = FirstDescriptor(registry, capability);
        return new CapabilityDetailDto(
            capability.ToString(),
            descriptor.Purpose,
            descriptor.Input.Name,
            descriptor.Output.Name,
            descriptor.Errors.Select(error => new ErrorVariantDto(error.Code, error.Description)).ToArray(),
            descriptor.RequiredPermissions.Select(permission => permission.Name).ToArray(),
            Traits(descriptor.Traits),
            registry.GetProviders(capability)
                .Select(provider => new ProviderOverviewDto(provider.Id.ToString(), provider.Health.ToString()))
                .ToArray());
    }

    public static PluginDto ToPluginDto(WorkerInstance worker)
    {
        var manifest = worker.Package.Manifest;
        return new PluginDto(
            worker.ProviderId.ToString(),
            manifest.DisplayName,
            manifest.Runtime,
            worker.State.ToString(),
            worker.RestartCount,
            worker.ProcessId,
            worker.StartedAt,
            worker.LastHealthyAt,
            worker.LastError,
            manifest.Capabilities
                .Select(capability => new PluginCapabilityDto(
                    capability.Id,
                    capability.Purpose,
                    capability.InputSchema,
                    capability.OutputSchema,
                    capability.Traits,
                    capability.Permissions,
                    capability.Errors
                        .Select(error => new ErrorVariantDto(error.Code, error.Description))
                        .ToArray()))
                .ToArray());
    }

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

    private static CapabilityDescriptor FirstDescriptor(CapabilityRegistry registry, CapabilityId capability) =>
        registry.GetProviders(capability)[0].Descriptors.First(descriptor => descriptor.Id == capability);

    private static string[] Traits(CapabilityTraits traits) =>
        Enum.GetValues<CapabilityTraits>()
            .Where(flag => flag != CapabilityTraits.None && traits.HasFlag(flag))
            .Select(flag => flag.ToString())
            .ToArray();
}