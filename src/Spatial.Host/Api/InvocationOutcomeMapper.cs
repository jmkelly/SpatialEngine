using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;
using Spatial.Runtime.Capabilities;

namespace Spatial.Host.Api;

/// <summary>Maps a runtime outcome to the wire <c>completed</c> invocation response body.</summary>
internal static class InvocationOutcomeMapper
{
    public static InvocationResponse ToCompletedResponse(CapabilityOutcome outcome)
    {
        var capability = outcome.Provenance.Capability.ToString();
        var provenance = CapabilityApiMappers.ToProvenanceDto(outcome.Provenance);
        if (outcome.TryGetValue(out var value))
        {
            return InvocationResponse.Completed(capability, ValueCodec.Encode(value), provenance);
        }

        var error = outcome.Error is { } structured
            ? CapabilityApiMappers.ToErrorDto(structured)
            : new CapabilityErrorDto("ProviderFailure", "provider.failure", "The invocation failed without a structured error.");
        return InvocationResponse.Failed(capability, error, provenance);
    }
}
