using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Http;
using Spatial.Runtime.Capabilities;

namespace Spatial.Host.Api;

/// <summary>
/// Capability-surface mapping: errors, provenance, outcome responses and
/// capability contract declarations into the SDK's HTTP shapes (plan §16
/// Phase 9). One small mapper per API surface keeps every mapper's fan-out
/// under the metrics coupling ceiling.
/// </summary>
internal static class CapabilityApiMappers
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

    private static CapabilityDescriptor FirstDescriptor(CapabilityRegistry registry, CapabilityId capability) =>
        registry.GetProviders(capability)[0].Descriptors.First(descriptor => descriptor.Id == capability);

    private static string[] Traits(CapabilityTraits traits) =>
        Enum.GetValues<CapabilityTraits>()
            .Where(flag => flag != CapabilityTraits.None && traits.HasFlag(flag))
            .Select(flag => flag.ToString())
            .ToArray();
}
