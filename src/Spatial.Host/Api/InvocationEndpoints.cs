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
/// HTTP 400 is reserved for requests that cannot be decoded. Request
/// decoding lives in <see cref="InvocationRequestBuilder"/> so this endpoint
/// stays a thin router.
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
        if (!InvocationRequestBuilder.TryBuild(
                request,
                host.Runtime,
                httpContext.RequestAborted,
                out var invocation,
                out var options,
                out var error))
        {
            return TypedResults.BadRequest(error);
        }

        return await InvokeBuiltAsync(invocation, options, request.Wait, host.Runtime);
    }

    private static async Task<Results<Ok<InvocationResponse>, Accepted<InvocationResponse>, BadRequest<string>>> InvokeBuiltAsync(
        CapabilityInvocation invocation,
        InvocationOptions options,
        bool? wait,
        CapabilityRuntime runtime)
    {
        if (ShouldRunInline(invocation.Capability, options, wait, runtime))
        {
            var outcome = await runtime.InvokeAsync(invocation, options);
            return TypedResults.Ok(CompletedResponse(outcome));
        }

        var job = runtime.StartJob(invocation, options);
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

        return RunsInline(runtime.Resolve(capability, options));
    }

    /// <summary>A capability runs inline unless its descriptor declares long-running work.</summary>
    private static bool RunsInline(ResolvedProvider? resolved) =>
        !(resolved?.Descriptor.Traits.HasFlag(CapabilityTraits.LongRunning) ?? false);

    private static InvocationResponse CompletedResponse(CapabilityOutcome outcome) =>
        InvocationOutcomeMapper.ToCompletedResponse(outcome);
}
