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

        if (!CapabilityId.TryParse(request.Capability, out var capability))
        {
            error = $"'{request.Capability}' is not a valid capability id; expected 'name@version' such as 'spatial.geometry.buffer@1'.";
            return false;
        }

        if (!TryDecodeArguments(request.Arguments, out var arguments, out error))
        {
            return false;
        }

        if (!TryParsePermissions(request.Permissions, out var permissions, out error))
        {
            return false;
        }

        if (!TryBuildOptions(request, runtime, out options, out error))
        {
            return false;
        }

        invocation = CapabilityInvocation.Create(capability, arguments) with
        {
            GrantedPermissions = permissions,
            Deadline = request.Deadline,
            // Jobs live beyond the request: their own cancellation and the
            // deadline bound them (JobRunner), never the request token.
            CancellationToken = CancellationToken.None,
        };
        return true;
    }

    private static bool TryDecodeArguments(
        IReadOnlyDictionary<string, JsonNode?>? arguments,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, object?>? decoded,
        [NotNullWhen(false)] out string? error)
    {
        decoded = null;
        error = null;
        if (arguments is null || arguments.Count == 0)
        {
            decoded = new Dictionary<string, object?>();
            return true;
        }

        var map = new Dictionary<string, object?>(arguments.Count);
        foreach (var (name, node) in arguments)
        {
            try
            {
                map[name] = ValueCodec.Decode(node);
            }
            catch (ValueCodecException exception)
            {
                error = $"the argument '{name}' cannot be decoded: {exception.Message}";
                return false;
            }
        }

        decoded = map;
        return true;
    }

    private static bool TryParsePermissions(
        IReadOnlyList<string>? permissions,
        [NotNullWhen(true)] out IReadOnlySet<Permission>? parsed,
        [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        error = null;
        var set = new HashSet<Permission>();
        foreach (var name in permissions ?? [])
        {
            if (!Permission.TryParse(name, out var permission))
            {
                error = $"'{name}' is not a valid permission; expected a dotted lowercase name such as 'spatial.feature.read'.";
                return false;
            }

            set.Add(permission);
        }

        parsed = set;
        return true;
    }

    private static bool TryBuildOptions(
        InvocationRequest request,
        CapabilityRuntime runtime,
        out InvocationOptions options,
        [NotNullWhen(false)] out string? error)
    {
        options = InvocationOptions.None;
        error = null;

        if (!TryParseProvider(request.Provider, out var provider, out error))
        {
            return false;
        }

        if (!TryResolveResource(request.Resource, runtime, out var resource, out error))
        {
            return false;
        }

        options = BuildOptions(provider, resource);
        return true;
    }

    /// <summary>Parses an explicit provider pin; null is "no pin" and always succeeds.</summary>
    private static bool TryParseProvider(
        string? providerText,
        out ProviderId? provider,
        [NotNullWhen(false)] out string? error)
    {
        provider = null;
        error = null;
        if (providerText is null)
        {
            return true;
        }

        if (!ProviderId.TryParse(providerText, out var parsedProvider))
        {
            error = $"'{providerText}' is not a valid provider id; expected 'name@version' such as 'nts@1'.";
            return false;
        }

        provider = parsedProvider;
        return true;
    }

    /// <summary>Resolves a resource token to its live handle; null is "no resource" and always succeeds.</summary>
    private static bool TryResolveResource(
        string? tokenText,
        CapabilityRuntime runtime,
        out ResourceHandle? resource,
        [NotNullWhen(false)] out string? error)
    {
        resource = null;
        error = null;
        if (tokenText is null)
        {
            return true;
        }

        if (!Guid.TryParse(tokenText, out var token) || !runtime.Resources.TryGetHandle(new ResourceId(token), out var handle))
        {
            error = $"'{tokenText}' is not a live resource token.";
            return false;
        }

        resource = handle;
        return true;
    }

    /// <summary>An invocation carries routing options only when the caller supplied a pin or resource.</summary>
    private static InvocationOptions BuildOptions(ProviderId? provider, ResourceHandle? resource) =>
        provider is not null || resource is not null
            ? new InvocationOptions(ExplicitProvider: provider, Resource: resource)
            : InvocationOptions.None;
}
