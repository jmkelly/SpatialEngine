using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Resources;
using Spatial.Runtime.Capabilities;
using Spatial.Runtime.Resources;

namespace Spatial.Host.Api;

/// <summary>
/// Maps <c>/api/resources</c> (plan §12, ADR-0022/0023): runtime-owned opaque
/// handles — their metadata, their disposal, and the bounded stream read
/// surface that carries streaming results (feature scan/query batches,
/// catalogue metadata, job outputs) to clients as line-delimited JSON of
/// codec-encoded items.
/// </summary>
internal static class ResourceEndpoints
{
    /// <summary>How many items one stream batch read delivers (each item becomes one NDJSON line).</summary>
    internal const int ReadBatchSize = 32;

    /// <summary>The lease width for one HTTP stream read — reads run to completion under it.</summary>
    internal static readonly TimeSpan ReadLeaseDuration = TimeSpan.FromHours(1);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/resources/{id}/metadata", Metadata)
            .Produces<ResourceDto>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapDelete("/api/resources/{id}", Delete)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapGet("/api/resources/{id}/stream", Stream)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    /// <summary>One resource's metadata: token, kind, owner, creation time and registry state.</summary>
    internal static Results<Ok<ResourceDto>, NotFound> Metadata(string id, SpatialHostRuntime host)
    {
        if (!TryGetResource(id, host.Runtime, out var handle, out var state))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(ApiMappers.ToResourceDto(handle, state));
    }

    /// <summary>Disposes a resource: closed state, leases dropped and the payload (stream) disposed.</summary>
    internal static async Task<Results<NoContent, NotFound>> Delete(string id, SpatialHostRuntime host)
    {
        if (!TryGetResource(id, host.Runtime, out var handle, out _))
        {
            return TypedResults.NotFound();
        }

        await host.Runtime.Resources.CloseAsync(handle);
        return TypedResults.NoContent();
    }

    /// <summary>
    /// The stream read surface: <c>application/x-ndjson</c>, one codec-encoded
    /// item per line (<c>$bytes</c> for canonical binary batches, JSON for
    /// metadata text items). Reads run to completion — when the producer
    /// finishes normally the stream ends cleanly; when it fails, a final
    /// <c>$error</c> line carries the structured failure. Consuming a stream
    /// to its end (or abandoning it) closes the resource, which unblocks a
    /// backpressured producer.
    /// </summary>
    internal static IResult Stream(string id, SpatialHostRuntime host)
    {
        if (!TryGetResource(id, host.Runtime, out var handle, out _))
        {
            return TypedResults.NotFound();
        }

        if (!host.Runtime.Resources.IsStream(handle))
        {
            return TypedResults.BadRequest("the resource exists but does not carry a readable stream.");
        }

        return new NdjsonStreamResult(host.Runtime.Resources, handle);
    }

    private static bool TryGetResource(
        string id, CapabilityRuntime runtime, out ResourceHandle handle, out ResourceState state)
    {
        if (Guid.TryParse(id, out var token))
        {
            var resourceId = new ResourceId(token);
            if (runtime.Resources.TryGetHandle(resourceId, out var found))
            {
                handle = found;
                state = runtime.Resources.GetState(handle);
                return true;
            }
        }

        handle = null!;
        state = ResourceState.Closed;
        return false;
    }
}

/// <summary>
/// Streams a bounded stream resource to the response as NDJSON of
/// codec-encoded items. A lease is held for the read; when the read ends —
/// normally, at completion, or by client disconnect — the lease is released
/// and the resource closed, so backpressured producers are always released.
/// </summary>
internal sealed class NdjsonStreamResult : IResult
{
    private readonly ResourceRegistry _resources;
    private readonly ResourceHandle _handle;

    public NdjsonStreamResult(ResourceRegistry resources, ResourceHandle handle)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        var response = httpContext.Response;
        var cancellationToken = httpContext.RequestAborted;

        if (!_resources.TryAcquireLease(_handle, ResourceEndpoints.ReadLeaseDuration, out var lease))
        {
            response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        if (!_resources.TryOpenStream(_handle, lease, out var stream))
        {
            _resources.ReleaseLease(lease);
            response.StatusCode = StatusCodes.Status409Conflict;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/x-ndjson";

        try
        {
            while (true)
            {
                var items = await stream.ReadBatchAsync(ResourceEndpoints.ReadBatchSize, cancellationToken);
                if (items.Count > 0)
                {
                    foreach (var item in items)
                    {
                        await WriteItemAsync(response, item, cancellationToken);
                    }

                    continue;
                }

                var completion = await stream.WaitForCompletionAsync(cancellationToken);
                if (completion.Error is { } failure)
                {
                    await WriteErrorLineAsync(response, failure, cancellationToken);
                }

                return;
            }
        }
        finally
        {
            _resources.ReleaseLease(lease);
            await _resources.CloseAsync(_handle);
        }
    }

    private static async Task WriteItemAsync(HttpResponse response, object? item, CancellationToken cancellationToken)
    {
        var node = ValueCodec.Encode(item);
        await response.WriteAsync(JsonSerializer.Serialize(node, HostApiJson.Options) + "\n", cancellationToken);
    }

    private static async Task WriteErrorLineAsync(
        HttpResponse response, ICapabilityError error, CancellationToken cancellationToken)
    {
        var node = new JsonObject
        {
            ["$error"] = new JsonObject
            {
                ["kind"] = error.Kind.ToString(),
                ["code"] = error.Code,
                ["message"] = error.Message,
            },
        };
        await response.WriteAsync(JsonSerializer.Serialize(node, HostApiJson.Options) + "\n", cancellationToken);
    }
}
