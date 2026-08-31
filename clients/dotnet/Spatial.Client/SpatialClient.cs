using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;

namespace Spatial.Client;

/// <summary>
/// The .NET client SDK for the public spatial host API (plan §12): every
/// endpoint of the versioned HTTP contract, the shared value codec for
/// inline arguments and results, and typed helpers for the streaming
/// surfaces (canonical feature batches, text metadata items). One
/// <see cref="System.Net.Http.HttpClient"/> per client, configured with the
/// host's base address.
/// </summary>
public sealed class SpatialClient
{
    private readonly HttpClient _http;

    /// <summary>Creates a client over an existing <see cref="HttpClient"/> whose base address is the host.</summary>
    public SpatialClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Creates a client talking to the host at <paramref name="baseAddress"/>.</summary>
    public SpatialClient(string baseAddress)
        : this(new HttpClient { BaseAddress = new Uri(baseAddress, UriKind.Absolute) })
    {
    }

    // ---- Capabilities ----

    /// <summary>Every registered capability with its serving providers (ordered by id).</summary>
    public Task<IReadOnlyList<CapabilitySummaryDto>> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<CapabilitySummaryDto>>("/api/capabilities", cancellationToken);

    /// <summary>The full declaration of one capability; not found when no provider serves it.</summary>
    public Task<CapabilityDetailDto> GetCapabilityAsync(string capabilityId, CancellationToken cancellationToken = default) =>
        GetAsync<CapabilityDetailDto>($"/api/capabilities/{Uri.EscapeDataString(capabilityId)}", cancellationToken);

    // ---- Invocations ----

    /// <summary>
    /// Invokes a capability exactly as the request describes: inline
    /// capabilities complete in the response; long-running (or
    /// <c>wait: false</c>) invocations come back as a started job to poll via
    /// <see cref="GetJobAsync"/>.
    /// </summary>
    public Task<InvocationResponse> InvokeAsync(InvocationRequest request, CancellationToken cancellationToken = default) =>
        PostAsync<InvocationResponse>("/api/invocations", request, cancellationToken);

    /// <summary>A convenience for the common inline shape: codec-encoded wire values as arguments.</summary>
    public Task<InvocationResponse> InvokeAsync(
        string capability,
        IReadOnlyDictionary<string, object?> arguments,
        IReadOnlySet<string>? permissions = null,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var encoded = arguments.ToDictionary(
            entry => entry.Key,
            entry => ValueCodec.Encode(entry.Value),
            StringComparer.Ordinal);
        return InvokeAsync(
            new InvocationRequest(
                capability,
                Arguments: encoded,
                Permissions: permissions?.ToArray(),
                Provider: provider),
            cancellationToken);
    }

    // ---- Jobs ----

    /// <summary>One job's public snapshot (state, serving provider, terminal error).</summary>
    public Task<JobResponse> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        GetAsync<JobResponse>($"/api/jobs/{Uri.EscapeDataString(jobId)}", cancellationToken);

    /// <summary>Requests cancellation of a running job and returns its updated snapshot.</summary>
    public Task<JobResponse> CancelJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        PostAsync<JobResponse>($"/api/jobs/{Uri.EscapeDataString(jobId)}/cancel", content: null, cancellationToken);

    /// <summary>The append-only event log of one job (progress, published resources, diagnostics).</summary>
    public Task<JobEventsResponse> GetJobEventsAsync(string jobId, CancellationToken cancellationToken = default) =>
        GetAsync<JobEventsResponse>($"/api/jobs/{Uri.EscapeDataString(jobId)}/events", cancellationToken);

    /// <summary>
    /// Polls a job until it reaches a terminal state and returns its final
    /// snapshot. The poll interval starts at 50 ms and backs off to 500 ms.
    /// </summary>
    public async Task<JobResponse> WaitForJobAsync(
        string jobId, TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        while (true)
        {
            var job = await GetJobAsync(jobId, cancellationToken);
            if (IsTerminal(job.State))
            {
                return job;
            }

            await Task.Delay(interval, cancellationToken);
            if (interval < TimeSpan.FromMilliseconds(500))
            {
                interval += TimeSpan.FromMilliseconds(50);
            }
        }
    }

    // ---- Resources ----

    /// <summary>One resource's metadata (token, kind, owner, state).</summary>
    public Task<ResourceDto> GetResourceAsync(string resourceToken, CancellationToken cancellationToken = default) =>
        GetAsync<ResourceDto>($"/api/resources/{Uri.EscapeDataString(resourceToken)}/metadata", cancellationToken);

    /// <summary>Disposes a resource (closes a stream, releases its leases).</summary>
    public Task DeleteResourceAsync(string resourceToken, CancellationToken cancellationToken = default) =>
        DeleteAsync($"/api/resources/{Uri.EscapeDataString(resourceToken)}", cancellationToken);

    /// <summary>
    /// Reads a bounded stream resource as decoded items (scalars, strings,
    /// <c>byte[]</c>, geometry, handles — the SDK value codec). Consuming the
    /// stream to its end closes the resource. A stream that ended with a
    /// structured failure throws <see cref="CapabilityStreamException"/>.
    /// </summary>
    public async IAsyncEnumerable<object?> ReadStreamAsync(
        string resourceToken, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"/api/resources/{Uri.EscapeDataString(resourceToken)}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        EnsureSuccess(response);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var node = JsonNode.Parse(line);
            if (node is JsonObject { } obj && obj.TryGetPropertyValue("$error", out var errorNode))
            {
                var error = errorNode.Deserialize<CapabilityErrorDto>(HostApiJson.Options)
                    ?? new CapabilityErrorDto("ProviderFailure", "provider.failure", "the stream failed without a structured error");
                throw new CapabilityStreamException(error);
            }

            yield return ValueCodec.Decode(node);
        }
    }

    /// <summary>
    /// Reads a feature stream (scan/query) as typed, decoded feature batches —
    /// the canonical binary interchange (ADR-0020) each <c>byte[]</c> item
    /// carries.
    /// </summary>
    public async IAsyncEnumerable<FeatureBatch> ReadFeatureBatchesAsync(
        string resourceToken, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in ReadStreamAsync(resourceToken, cancellationToken).WithCancellation(cancellationToken))
        {
            if (item is not byte[] bytes)
            {
                throw new CapabilityStreamException(
                    "a feature stream item is not canonical binary (byte[]); the interchange contract is violated");
            }

            yield return FeatureBatchCodec.Decode(bytes);
        }
    }

    // ---- Plugins ----

    /// <summary>The activated plugin worker packages (empty when none are configured).</summary>
    public Task<IReadOnlyList<PluginDto>> GetPluginsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PluginDto>>("/api/plugins", cancellationToken);

    /// <summary>One plugin worker package by provider id (for example <c>nts@1</c>).</summary>
    public Task<PluginDto> GetPluginAsync(string providerId, CancellationToken cancellationToken = default) =>
        GetAsync<PluginDto>($"/api/plugins/{Uri.EscapeDataString(providerId)}", cancellationToken);

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(path, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string path, object? content, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(path, content, HostApiJson.Options, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync(path, cancellationToken);
        EnsureSuccess(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new SpatialApiException((int)response.StatusCode, detail);
        }

        return (await response.Content.ReadFromJsonAsync<T>(HostApiJson.Options, cancellationToken))!;
    }

    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new SpatialApiException((int)response.StatusCode, "the request failed");
        }
    }

    private static bool IsTerminal(Spatial.PluginSdk.Jobs.JobState state) =>
        state is Spatial.PluginSdk.Jobs.JobState.Completed
            or Spatial.PluginSdk.Jobs.JobState.Failed
            or Spatial.PluginSdk.Jobs.JobState.Cancelled
            or Spatial.PluginSdk.Jobs.JobState.TimedOut;
}