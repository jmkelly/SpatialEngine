namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The structured failure categories an invocation can produce (plan §9
/// "error variants", "structured errors"). A
/// <see cref="CapabilityError"/> always carries one of these kinds plus a
/// stable code and an actionable message.
/// </summary>
public enum CapabilityErrorKind
{
    /// <summary>The invocation arguments do not satisfy the capability's input contract.</summary>
    InvalidArguments,

    /// <summary>The caller is missing one or more required permissions.</summary>
    PermissionDenied,

    /// <summary>The invocation deadline passed before the capability completed.</summary>
    DeadlineExceeded,

    /// <summary>The invocation was cancelled before completing.</summary>
    Cancelled,

    /// <summary>No provider serves the requested capability.</summary>
    CapabilityNotFound,

    /// <summary>A provider for the capability exists but cannot serve (unhealthy or explicitly requested but incompatible).</summary>
    ProviderUnavailable,

    /// <summary>The provider broke the invocation contract (returned no result, or a wrong result type).</summary>
    ContractViolation,

    /// <summary>The provider threw an unhandled exception while serving.</summary>
    ProviderFailure,
}
