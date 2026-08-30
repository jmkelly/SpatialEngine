using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Spatial.PluginSdk.Capabilities;

/// <summary>
/// Everything a caller sends to the runtime for one inline invocation: the
/// requested capability, typed arguments (core values), the permissions the
/// caller has been granted, an optional deadline, an optional progress sink
/// and the cancellation token. Immutable — the runtime derives the effective
/// token (deadline linked) and hands the invocation to the provider.
/// </summary>
public sealed record CapabilityInvocation(
    CapabilityId Capability,
    IReadOnlyDictionary<string, object?> Arguments,
    IReadOnlySet<Permission> GrantedPermissions,
    DateTimeOffset? Deadline,
    IProgress<ProgressReport>? Progress,
    CancellationToken CancellationToken)
{
    /// <summary>
    /// A minimal invocation: no permissions, no deadline, no progress and an
    /// uncancelled token. Callers add what they need with <c>with</c>.
    /// </summary>
    public static CapabilityInvocation Create(
        CapabilityId capability,
        IReadOnlyDictionary<string, object?> arguments) =>
        new(capability, arguments, ImmutableHashSet<Permission>.Empty, null, null, CancellationToken.None);

    /// <summary>
    /// Reads a typed argument by name. Returns <c>true</c> when the argument
    /// exists and is exactly the requested type; false when it is missing or
    /// of another kind (providers then fail with
    /// <see cref="CapabilityErrorKind.InvalidArguments"/>).
    /// </summary>
    public bool TryGetArgument<T>(string name, [NotNullWhen(true)] out T? value)
    {
        if (Arguments.TryGetValue(name, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }
}