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
    CancellationToken CancellationToken,
    ICapabilityFacilities? Facilities = null) : IInvocationContext
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
    /// exists and can be delivered as the requested type; false when it is
    /// missing or of another kind (providers then fail with
    /// <see cref="CapabilityErrorKind.InvalidArguments"/>).
    /// </summary>
    public bool TryGetArgument<T>(string name, [NotNullWhen(true)] out T? value)
    {
        value = default;
        return Arguments.TryGetValue(name, out var raw) && TryCast(raw, out value);
    }

    private static bool TryCast<T>(object? raw, [NotNullWhen(true)] out T? value)
    {
        if (raw is T typed)
        {
            value = typed;
            return true;
        }

        return TryNumericCast(raw, typeof(T), out value);
    }

    /// <summary>Applies the int&lt;-&gt;long wire conversion when the requested type matches.</summary>
    private static bool TryNumericCast<T>(object? raw, Type target, [NotNullWhen(true)] out T? value)
    {
        var converted = NumericConvert(raw, target);
        if (converted is null)
        {
            value = default;
            return false;
        }

        value = (T)converted;
        return true;
    }

    /// <summary>
    /// Returns the widened or narrowed boxed value, or null when the shape
    /// does not apply. The wire codec decodes JSON integral numbers as int32
    /// while contract arguments declare int64 (the inline codec of ADR-0030
    /// carries scalars as JSON numbers — there is no JSON int32). Widen
    /// int -&gt; long so int64 contracts read HTTP/worker numbers, and narrow
    /// long -&gt; int when representable so int contracts still read
    /// $i64-coded values. Nothing leaks through these casts: int is always
    /// exactly representable as long, and the narrowing is range-checked.
    /// </summary>
    private static object? NumericConvert(object? raw, Type target)
    {
        if (raw is int intValue && target == typeof(long))
        {
            return (long)intValue;
        }

        if (raw is long longValue && target == typeof(int) && longValue is >= int.MinValue and <= int.MaxValue)
        {
            return (int)longValue;
        }

        return null;
    }
}
