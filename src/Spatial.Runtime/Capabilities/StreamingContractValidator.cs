using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Resources;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// Enforces the streaming contract (plan §9 "streaming and cancellation
/// behaviour"): a capability that declares <see cref="CapabilityTraits.Streaming"/>
/// must return a stream — a registered resource backed by a bounded stream —
/// and a capability that does not declare the trait must not return one.
/// Providers mint streams through the invocation facilities; anything else
/// becomes a <see cref="CapabilityErrorKind.ContractViolation"/>.
/// </summary>
internal static class StreamingContractValidator
{
    public static CapabilityResult Validate(ResolvedProvider resolved, CapabilityResult result, ResourceRegistry resources)
    {
        if (result is not CapabilitySuccess success)
        {
            // Failures pass through untouched; only success values must match the declared shape.
            return result;
        }

        return success.Value is ResourceHandle handle
            ? ValidateStreamHandle(resolved, handle, resources, result)
            : ValidateNonStreamValue(resolved, result);
    }

    private static CapabilityResult ValidateNonStreamValue(ResolvedProvider resolved, CapabilityResult result) =>
        RequiresStreaming(resolved)
            ? FailureForStreamingRequired(resolved)
            : result;

    private static CapabilityResult ValidateStreamHandle(
        ResolvedProvider resolved,
        ResourceHandle handle,
        ResourceRegistry resources,
        CapabilityResult result)
    {
        var isStream = resources.IsStream(handle);
        if (RequiresStreaming(resolved) && !isStream)
        {
            return FailureForWrongResourceKind(resolved, handle);
        }

        if (!RequiresStreaming(resolved) && isStream)
        {
            return FailureForUnexpectedStream(resolved);
        }

        return result;
    }

    private static bool RequiresStreaming(ResolvedProvider resolved) =>
        (resolved.Descriptor.Traits & CapabilityTraits.Streaming) != 0;

    private static CapabilityFailure FailureForStreamingRequired(ResolvedProvider resolved) =>
        CapabilityResult.Failure(CapabilityError.ContractViolation(
            $"Streaming capability {resolved.Descriptor.Id} returned a value, not a stream; streaming capabilities must return a stream created through invocation.Facilities.Streams."));

    private static CapabilityFailure FailureForWrongResourceKind(ResolvedProvider resolved, ResourceHandle handle) =>
        CapabilityResult.Failure(CapabilityError.ContractViolation(
            $"Streaming capability {resolved.Descriptor.Id} returned resource {handle.Kind}, but it is not backed by a bounded stream; a streaming capability must return a stream handle."));

    private static CapabilityFailure FailureForUnexpectedStream(ResolvedProvider resolved) =>
        CapabilityResult.Failure(CapabilityError.ContractViolation(
            $"Capability {resolved.Descriptor.Id} returned a stream handle but does not declare the Streaming trait; add CapabilityTraits.Streaming to its descriptor or return a value."));
}
