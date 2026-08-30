using System.Diagnostics.CodeAnalysis;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Capabilities;

namespace Spatial.Operations.NetTopologySuite.Operations;

/// <summary>
/// Argument parsing shared by the geometry operation runners. Every helper
/// answers with a structured <c>invalid.arguments</c> error that names the
/// offending argument (ADR-0026: shape errors and out-of-range values are
/// input-contract violations). Values arrive from the worker boundary through
/// the canonical-binary codec, so a geometry argument is a real core
/// <see cref="IGeometry"/> and numbers are int32/int64/double depending on
/// the JSON shape — the double reader accepts every numeric wire form.
/// </summary>
internal static class OperationArguments
{
    /// <summary>Reads the named geometry argument.</summary>
    public static bool TryGeometry(
        CapabilityInvocation invocation,
        string name,
        [NotNullWhen(true)] out IGeometry? geometry,
        out CapabilityError? error)
    {
        geometry = null;
        error = null;
        if (invocation.TryGetArgument<IGeometry>(name, out var value))
        {
            geometry = value;
            return true;
        }

        error = CapabilityError.InvalidArguments(
            $"{invocation.Capability} requires '{name}' to carry a spatial geometry (canonical binary interchange).");
        return false;
    }

    /// <summary>Reads the named double argument; accepts any numeric wire form that is finite.</summary>
    public static bool TryFiniteDouble(
        CapabilityInvocation invocation,
        string name,
        out double value,
        out CapabilityError? error)
    {
        value = 0;
        error = null;
        if (!TryNumber(invocation, name, out var raw, out error))
        {
            return false;
        }

        if (!double.IsFinite(raw))
        {
            error = CapabilityError.InvalidArguments($"'{name}' must be a finite number, got {raw}.");
            return false;
        }

        value = raw;
        return true;
    }

    /// <summary>Reads an optional positive integer argument with a fallback when absent; accepts int32 and int64 wire forms.</summary>
    public static bool TryOptionalPositiveInt(
        CapabilityInvocation invocation,
        string name,
        int fallback,
        out int value,
        out CapabilityError? error)
    {
        value = fallback;
        error = null;
        if (!invocation.Arguments.TryGetValue(name, out var raw))
        {
            return true;
        }

        var parsed = raw switch
        {
            int number => (long)number,
            long number => number,
            _ => -1L,
        };
        if (parsed < 1)
        {
            error = CapabilityError.InvalidArguments(
                $"'{name}', when provided, must be a positive int32, got {(raw is null ? "nothing" : $"'{raw.GetType().Name}'")}.");
            return false;
        }

        value = (int)parsed;
        return true;
    }

    private static bool TryNumber(
        CapabilityInvocation invocation,
        string name,
        out double value,
        out CapabilityError? error)
    {
        value = 0;
        error = null;
        if (invocation.Arguments.TryGetValue(name, out var raw) && raw is not null)
        {
            switch (raw)
            {
                case double number:
                    value = number;
                    return true;
                case float number:
                    value = number;
                    return true;
                case int number:
                    value = number;
                    return true;
                case long number:
                    value = number;
                    return true;
            }
        }

        error = CapabilityError.InvalidArguments(
            $"{invocation.Capability} requires '{name}' to be a number, got "
            + $"{(raw is null ? "nothing" : $"'{raw.GetType().Name}'")}.");
        return false;
    }
}
