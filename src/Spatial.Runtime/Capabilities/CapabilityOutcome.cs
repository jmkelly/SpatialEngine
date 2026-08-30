using Spatial.PluginSdk.Capabilities;

namespace Spatial.Runtime.Capabilities;

/// <summary>
/// The full result of one invocation through <see cref="CapabilityRuntime"/>:
/// the provider's structured result plus the invocation provenance.
/// </summary>
public sealed record CapabilityOutcome(CapabilityResult Result, InvocationProvenance Provenance)
{
    public bool IsSuccess => Result.IsSuccess;

    /// <summary>The structured error, or null when the invocation succeeded.</summary>
    public CapabilityError? Error => (Result as CapabilityFailure)?.Error;

    /// <summary>The success value, or false when the invocation failed.</summary>
    public bool TryGetValue(out object? value)
    {
        if (Result is CapabilitySuccess success)
        {
            value = success.Value;
            return true;
        }

        value = null;
        return false;
    }
}
