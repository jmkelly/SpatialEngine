namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// The outcome of an inline capability invocation: either a successful value
/// or a structured failure. Providers always return one of the two concrete
/// records; the runtime enforces that (a null or thrown response becomes a
/// <see cref="CapabilityErrorKind.ContractViolation"/> or
/// <see cref="CapabilityErrorKind.ProviderFailure"/>).
/// </summary>
public abstract record CapabilityResult
{
    /// <summary>A successful invocation carrying its result value.</summary>
    public static CapabilitySuccess Success(object? value) => new(value);

    /// <summary>A failed invocation carrying its structured error.</summary>
    public static CapabilityFailure Failure(CapabilityError error) => new(error);

    /// <summary>Whether this result is a <see cref="CapabilitySuccess"/>.</summary>
    public bool IsSuccess => this is CapabilitySuccess;
}

/// <summary>A successful invocation result. The value is a core type or null.</summary>
public sealed record CapabilitySuccess(object? Value) : CapabilityResult;

/// <summary>A failed invocation with a structured error.</summary>
public sealed record CapabilityFailure(CapabilityError Error) : CapabilityResult;