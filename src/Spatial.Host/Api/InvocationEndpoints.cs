using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Capabilities;

namespace Spatial.Host.Api;

/// <summary>
/// Maps <c>POST /api/invocations</c> (plan §12): the one entry point that
/// turns a wire-encoded request into a runtime invocation. Inline
/// capabilities complete in the response; long-running capabilities (and any
/// invocation asked to <c>wait: false</c>) start as tracked jobs and answer
/// with the job to poll — the request never blocks on minute-scale work
/// (ADR-0008). Runtime outcomes — success, failure, cancellation, deadlines,
/// even "no such capability" — always return as a 2xx <c>completed</c> body;
/// HTTP 400 is reserved for requests that cannot be decoded.
/// </summary>
internal static class InvocationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/invocations", Invoke)
            .Produces<InvocationResponse>(StatusCodes.Status200OK)
            .Produces<InvocationResponse>(StatusCodes.Status202Accepted)
            .Produces<string>(StatusCodes.Status400BadRequest);
    }

    internal static async Task<Results<Ok<InvocationResponse>, Accepted<InvocationResponse>, BadRequest<string>>> Invoke(
        InvocationRequest request,
        SpatialHostRuntime host,
        HttpContext httpContext)
    {
        if (!TryBuildInvocation(
                request,
                host.Runtime,
                httpContext.RequestAborted,
                out var invocation,
                out var options,
                out var error))
        {
            return TypedResults.BadRequest(error);
        }

        if (ShouldRunInline(invocation.Capability, options, request.Wait, host.Runtime))
        {
            var outcome = await host.Runtime.InvokeAsync(invocation, options);
            return TypedResults.Ok(CompletedResponse(outcome));
        }

        var job = host.Runtime.StartJob(invocation, options);
        var started = new JobStartedDto(job.Id.ToString(), job.State, JobStartedDto.LocationFor(job.Id));
        return TypedResults.Accepted<InvocationResponse>(started.Location, InvocationResponse.JobStarted(invocation.Capability.ToString(), started));
    }

    /// <summary>
    /// An invocation runs inline unless the caller asked for a job
    /// (<c>wait: false</c>) or the serving capability is declared
    /// long-running — jobs are the plan's surface for cancellable, observable
    /// long work (ADR-0008), so a request never parks on it.
    /// </summary>
    private static bool ShouldRunInline(
        CapabilityId capability, InvocationOptions options, bool? wait, CapabilityRuntime runtime)
    {
        if (wait == false)
        {
            return false;
        }

        var resolved = runtime.Resolve(capability, options);
        return resolved is null || (resolved.Descriptor.Traits & CapabilityTraits.LongRunning) == 0;
    }

    private static InvocationResponse CompletedResponse(CapabilityOutcome outcome)
    {
        var capability = outcome.Provenance.Capability.ToString();
        var provenance = ApiMappers.ToProvenanceDto(outcome.Provenance);
        if (outcome.TryGetValue(out var value))
        {
            return InvocationResponse.Completed(capability, ValueCodec.Encode(value), provenance);
        }

        var error = outcome.Error is { } structured
            ? ApiMappers.ToErrorDto(structured)
            : new CapabilityErrorDto("ProviderFailure", "provider.failure", "The invocation failed without a structured error.");
        return InvocationResponse.Failed(capability, error, provenance);
    }

    private static bool TryBuildInvocation(
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

        ProviderId? provider = null;
        if (request.Provider is { } providerText)
        {
            if (!ProviderId.TryParse(providerText, out var parsedProvider))
            {
                error = $"'{providerText}' is not a valid provider id; expected 'name@version' such as 'nts@1'.";
                return false;
            }

            provider = parsedProvider;
        }

        ResourceHandle? resource = null;
        if (request.Resource is not null)
        {
            if (!Guid.TryParse(request.Resource, out var token)
                || !runtime.Resources.TryGetHandle(new ResourceId(token), out var handle))
            {
                error = $"'{request.Resource}' is not a live resource token.";
                return false;
            }

            resource = handle;
        }

        if (provider is not null || resource is not null)
        {
            options = new InvocationOptions(ExplicitProvider: provider, Resource: resource);
        }

        return true;
    }
}
