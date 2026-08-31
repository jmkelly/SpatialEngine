using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Capabilities;

namespace Spatial.Host.Api;

/// <summary>
/// Decodes an <see cref="InvocationRequest"/> into a runtime invocation:
/// capability id, codec-encoded arguments, granted permissions, deadline and
/// the routing hints (explicit provider pin, resource-local handle). Every
/// failure is an actionable message the endpoint returns as HTTP 400.
/// </summary>
internal static class InvocationRequestBuilder
{
    public static bool TryBuild(
        InvocationRequest request,
        CapabilityRuntime runtime,
        CancellationToken requestToken,
        [NotNullWhen(true)] out CapabilityInvocation? invocation,
        out InvocationOptions options,
        [NotNullWhen(false)] out string? error)
    {
        invocation = null;
        options = InvocationOptions.None;
        error = null;

        var capability = ParseCapability(request.Capability, out var capabilityError);
        var arguments = ParseArguments(request.Arguments, out var argumentsError);
        var permissions = ParsePermissions(request.Permissions, out var permissionsError);
        options = ParseRouting(request, runtime, out var routingError);

        if (FirstFailure(capabilityError, argumentsError, permissionsError, routingError) is { } failure)
        {
            error = failure;
            return false;
        }

        invocation = CapabilityInvocation.Create(capability!.Value, arguments!) with
        {
            GrantedPermissions = permissions!,
            Deadline = request.Deadline,
            // Jobs live beyond the request: their own cancellation and the
            // deadline bound them (JobRunner), never the request token.
            CancellationToken = CancellationToken.None,
        };
        return true;
    }

    /// <summary>The first failing message in declaration order, or null when every step parsed.</summary>
    private static string? FirstFailure(params string?[] errors) =>
        errors.FirstOrDefault(message => message is not null);

    private static CapabilityId? ParseCapability(string? capabilityText, [NotNullWhen(false)] out string? error)
    {
        if (!CapabilityId.TryParse(capabilityText, out var capability))
        {
            error = $"'{capabilityText}' is not a valid capability id; expected 'name@version' such as 'spatial.geometry.buffer@1'.";
            return null;
        }

        error = null;
        return capability;
    }

    private static Dictionary<string, object?>? ParseArguments(
        IReadOnlyDictionary<string, JsonNode?>? arguments,
        [NotNullWhen(false)] out string? error)
    {
        if (arguments is null or { Count: 0 })
        {
            error = null;
            return new Dictionary<string, object?>();
        }

        return DecodeAll(arguments, out error);
    }

    /// <summary>A decoded argument node: its value, or the codec's reason when it refused.</summary>
    private sealed record DecodedArgument(string Name, object? Value, string? Reason);

    /// <summary>Decodes every argument node; the first codec refusal names the failing argument.</summary>
    private static Dictionary<string, object?>? DecodeAll(
        IReadOnlyDictionary<string, JsonNode?> arguments,
        [NotNullWhen(false)] out string? error)
    {
        var decoded = arguments.Select(pair => DecodeOne(pair.Key, pair.Value)).ToList();
        var failure = decoded.FirstOrDefault(item => item is { Reason: not null });
        if (failure is not null)
        {
            error = $"the argument '{failure.Name}' cannot be decoded: {failure.Reason}";
            return null;
        }

        error = null;
        return decoded.ToDictionary(item => item.Name, item => item.Value);
    }

    /// <summary>Decodes a single wire node, translating a codec refusal into a readable reason.</summary>
    private static DecodedArgument DecodeOne(string name, JsonNode? node)
    {
        try
        {
            return new DecodedArgument(name, ValueCodec.Decode(node), null);
        }
        catch (ValueCodecException exception)
        {
            return new DecodedArgument(name, null, exception.Message);
        }
    }

    private static HashSet<Permission>? ParsePermissions(
        IReadOnlyList<string>? permissions,
        [NotNullWhen(false)] out string? error)
    {
        var set = new HashSet<Permission>();
        var invalid = (permissions ?? []).FirstOrDefault(name => !ParsePermission(name, set));
        if (invalid is not null)
        {
            error = $"'{invalid}' is not a valid permission; expected a dotted lowercase name such as 'spatial.feature.read'.";
            return null;
        }

        error = null;
        return set;
    }

    private static bool ParsePermission(string name, HashSet<Permission> into)
    {
        if (!Permission.TryParse(name, out var permission))
        {
            return false;
        }

        into.Add(permission);
        return true;
    }

    /// <summary>
    /// Builds the routing options: an explicit provider pin, a resource-local
    /// handle, or none. Options are carried only when the caller supplied one.
    /// </summary>
    private static InvocationOptions ParseRouting(
        InvocationRequest request,
        CapabilityRuntime runtime,
        [NotNullWhen(false)] out string? error)
    {
        var provider = ParseProvider(request.Provider, out var providerError);
        var resource = ResolveResource(request.Resource, runtime, out var resourceError);

        if (FirstFailure(providerError, resourceError) is { } failure)
        {
            error = failure;
            return InvocationOptions.None;
        }

        error = null;
        return BuildOptions(provider, resource);
    }

    /// <summary>Parses an explicit provider pin; null is "no pin" and always succeeds.</summary>
    private static ProviderId? ParseProvider(string? providerText, [NotNullWhen(false)] out string? error)
    {
        if (providerText is null)
        {
            error = null;
            return null;
        }

        return ParseProviderText(providerText, out error);
    }

    private static ProviderId? ParseProviderText(string providerText, [NotNullWhen(false)] out string? error)
    {
        if (!ProviderId.TryParse(providerText, out var parsedProvider))
        {
            error = $"'{providerText}' is not a valid provider id; expected 'name@version' such as 'nts@1'.";
            return null;
        }

        error = null;
        return parsedProvider;
    }

    /// <summary>Resolves a resource token to its live handle; null is "no resource" and always succeeds.</summary>
    private static ResourceHandle? ResolveResource(
        string? tokenText,
        CapabilityRuntime runtime,
        [NotNullWhen(false)] out string? error)
    {
        if (tokenText is null)
        {
            error = null;
            return null;
        }

        return ResolveResourceToken(tokenText, runtime, out error);
    }

    private static ResourceHandle? ResolveResourceToken(
        string tokenText,
        CapabilityRuntime runtime,
        [NotNullWhen(false)] out string? error)
    {
        var handle = TokenHandle(tokenText, runtime);
        if (handle is null)
        {
            error = $"'{tokenText}' is not a live resource token.";
            return null;
        }

        error = null;
        return handle;
    }

    private static ResourceHandle? TokenHandle(string tokenText, CapabilityRuntime runtime) =>
        Guid.TryParse(tokenText, out var token)
            ? TryGetHandle(runtime, new ResourceId(token))
            : null;

    private static ResourceHandle? TryGetHandle(CapabilityRuntime runtime, ResourceId id) =>
        runtime.Resources.TryGetHandle(id, out var handle) ? handle : null;

    /// <summary>An invocation carries routing options only when the caller supplied a pin or resource.</summary>
    private static InvocationOptions BuildOptions(ProviderId? provider, ResourceHandle? resource) =>
        (provider, resource) switch
        {
            (null, null) => InvocationOptions.None,
            _ => new InvocationOptions(ExplicitProvider: provider, Resource: resource),
        };
}
