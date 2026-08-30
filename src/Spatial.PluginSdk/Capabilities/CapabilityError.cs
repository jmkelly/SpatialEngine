namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// A structured, actionable invocation error: a category
/// (<see cref="CapabilityErrorKind"/>), a stable dotted code and a message
/// that tells the caller exactly what to fix. Factories keep the codes
/// wire-stable and the messages consistent.
/// </summary>
public sealed record CapabilityError(CapabilityErrorKind Kind, string Code, string Message) : ICapabilityError
{
    public static CapabilityError InvalidArguments(string message) =>
        new(CapabilityErrorKind.InvalidArguments, "invalid.arguments", message);

    public static CapabilityError PermissionDenied(IEnumerable<Permission> missing)
    {
        var names = string.Join(", ", missing.Select(permission => permission.Name));
        return new CapabilityError(
            CapabilityErrorKind.PermissionDenied,
            "permission.denied",
            $"The invocation is missing required permission(s): {names}.");
    }

    public static CapabilityError DeadlineExceeded(CapabilityId capability) =>
        new(
            CapabilityErrorKind.DeadlineExceeded,
            "deadline.exceeded",
            $"The deadline passed before {capability} completed; retry with a later deadline or smaller work.");

    public static CapabilityError Cancelled(CapabilityId capability) =>
        new(CapabilityErrorKind.Cancelled, "operation.cancelled", $"The invocation of {capability} was cancelled before completing.");

    public static CapabilityError CapabilityNotFound(string message) =>
        new(CapabilityErrorKind.CapabilityNotFound, "capability.not.found", message);

    public static CapabilityError ProviderUnavailable(string message) =>
        new(CapabilityErrorKind.ProviderUnavailable, "provider.unavailable", message);

    public static CapabilityError ContractViolation(string message) =>
        new(CapabilityErrorKind.ContractViolation, "contract.violation", message);

    public static CapabilityError ProviderFailure(string message) =>
        new(CapabilityErrorKind.ProviderFailure, "provider.failure", message);

    public override string ToString() => $"{Code}: {Message}";
}
