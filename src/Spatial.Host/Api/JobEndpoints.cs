using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Jobs;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Jobs;

namespace Spatial.Host.Api;

/// <summary>
/// Maps <c>/api/jobs</c>: the trackable job surface (ADR-0008) — one job per
/// long-running invocation, polled by id, cancellable on demand, with an
/// append-only event log (progress, published resources, diagnostics and the
/// terminal outcome). The events endpoint serves a pollable JSON snapshot, or
/// a server-sent event stream when the client asks for
/// <c>text/event-stream</c>.
/// </summary>
internal static class JobEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/jobs/{id}", Get)
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPost("/api/jobs/{id}/cancel", Cancel)
            .Produces<JobResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/jobs/{id}/events", Events)
            .Produces<JobEventsResponse>();
    }

    /// <summary>One job's snapshot: lifecycle, serving provider and terminal error code.</summary>
    internal static Results<Ok<JobResponse>, NotFound> Get(string id, SpatialHostRuntime host)
    {
        if (!TryGetJob(id, host.Runtime, out var job))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(JobApiMappers.ToJobResponse(job));
    }

    /// <summary>Requests cancellation of a running job (jobs are always cancellable, ADR-0008).</summary>
    internal static async Task<Results<Ok<JobResponse>, NotFound>> Cancel(
        string id, SpatialHostRuntime host, CancellationToken cancellationToken)
    {
        if (!TryGetJob(id, host.Runtime, out var job))
        {
            return TypedResults.NotFound();
        }

        await job.CancelAsync(cancellationToken);
        return TypedResults.Ok(JobApiMappers.ToJobResponse(job));
    }

    /// <summary>
    /// The job's append-only events: a JSON snapshot when the client did not
    /// request <c>text/event-stream</c>, otherwise a live SSE stream that
    /// replays the past events and pushes new ones until the job terminates.
    /// </summary>
    internal static IResult Events(string id, SpatialHostRuntime host, HttpContext context)
    {
        if (!TryGetJob(id, host.Runtime, out var job))
        {
            return TypedResults.NotFound();
        }

        if (string.Equals(
                context.Request.Headers.Accept,
                "text/event-stream",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SseJobEventsResult(job, host.Runtime.Resources);
        }

        return TypedResults.Ok(JobApiMappers.ToJobEventsResponse(job, host.Runtime.Resources));
    }

    private static bool TryGetJob(string id, CapabilityRuntime runtime, out CapabilityJob job)
    {
        if (Guid.TryParse(id, out var token) && runtime.Jobs.TryGet(new JobId(token), out var found))
        {
            job = found;
            return true;
        }

        job = null!;
        return false;
    }
}

/// <summary>
/// The server-sent event stream of one job's events: replays the events that
/// already happened, then pushes new ones every 250 ms (the job registry has
/// no per-event push signal — the completion task bounds the poll) until the
/// job reaches a terminal state, then emits <c>event: done</c>. Client
/// disconnect stops the stream.
/// </summary>
internal sealed class SseJobEventsResult : IResult
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly CapabilityJob _job;
    private readonly Spatial.Runtime.Resources.ResourceRegistry _resources;

    public SseJobEventsResult(CapabilityJob job, Spatial.Runtime.Resources.ResourceRegistry resources)
    {
        _job = job ?? throw new ArgumentNullException(nameof(job));
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        var cancellationToken = httpContext.RequestAborted;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";

        try
        {
            await PollUntilTerminalAsync(response, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The client went away; nothing to signal.
        }
    }

    private async Task PollUntilTerminalAsync(HttpResponse response, CancellationToken cancellationToken)
    {
        var state = new PollState();
        while (await WriteNextPollAsync(response, _job, state, cancellationToken))
        {
        }

        await response.WriteAsync("event: done\ndata: {}\n\n", cancellationToken);
    }

    private async Task<bool> WriteNextPollAsync(
        HttpResponse response, CapabilityJob job, PollState state, CancellationToken cancellationToken)
    {
        await WriteNewEventsAsync(response, job.Events, state, cancellationToken);
        if (IsTerminal(job.State))
        {
            return false;
        }

        await Task.Delay(PollInterval, cancellationToken);
        return true;
    }

    private async Task WriteNewEventsAsync(
        HttpResponse response, IReadOnlyList<JobEvent> events, PollState state, CancellationToken cancellationToken)
    {
        for (; state.Seen < events.Count; state.Seen++)
        {
            await WriteEventAsync(response, events[state.Seen], cancellationToken);
        }
    }

    /// <summary>Per-request write cursor (async methods cannot take ref parameters).</summary>
    private sealed class PollState
    {
        public int Seen;
    }

    private async Task WriteEventAsync(HttpResponse response, JobEvent jobEvent, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(JobEventMappers.ToEventDto(jobEvent, _resources), HostApiJson.Options);
        var kind = jobEvent.Kind.ToString().ToLowerInvariant();
        await response.WriteAsync($"event: {kind}\ndata: {line}\n\n", cancellationToken);
    }

    private static bool IsTerminal(JobState state) =>
        state is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.TimedOut;
}
