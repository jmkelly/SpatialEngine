using System.Net;
using System.Net.Http.Json;
using Spatial.Core.Features;
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
    private readonly SpatialStreamReader _streams;

    /// <summary>Creates a client over an existing <see cref="HttpClient"/> whose base address is the host.</summary>
    public SpatialClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _streams = new SpatialStreamReader(_http);
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

    // ---- Resources ----

    /// <summary>One resource's metadata (token, kind, owner, state).</summary>
    public Task<ResourceDto> GetResourceAsync(string resourceToken, CancellationToken cancellationToken = default) =>
        GetAsync<ResourceDto>($"/api/resources/{Uri.EscapeDataString(resourceToken)}/metadata", cancellationToken);

    /// <summary>Disposes a resource (closes a stream, releases its leases).</summary>
    public Task DeleteResourceAsync(string resourceToken, CancellationToken cancellationToken = default) =>
        DeleteAsync($"/api/resources/{Uri.EscapeDataString(resourceToken)}", cancellationToken);

    /// <summary>Reads a bounded stream resource as decoded items (see <see cref="SpatialStreamReader"/>).</summary>
    public IAsyncEnumerable<object?> ReadStreamAsync(
        string resourceToken, CancellationToken cancellationToken = default) =>
        _streams.ReadStreamAsync(resourceToken, cancellationToken);

    /// <summary>Reads a feature stream (scan/query) as typed canonical feature batches.</summary>
    public IAsyncEnumerable<FeatureBatch> ReadFeatureBatchesAsync(
        string resourceToken, CancellationToken cancellationToken = default) =>
        _streams.ReadFeatureBatchesAsync(resourceToken, cancellationToken);

    // ---- Plugins ----

    /// <summary>The activated plugin worker packages (empty when none are configured).</summary>
    public Task<IReadOnlyList<PluginDto>> GetPluginsAsync(CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<PluginDto>>("/api/plugins", cancellationToken);

    /// <summary>One plugin worker package by provider id (for example <c>nts@1</c>).</summary>
    public Task<PluginDto> GetPluginAsync(string providerId, CancellationToken cancellationToken = default) =>
        GetAsync<PluginDto>($"/api/plugins/{Uri.EscapeDataString(providerId)}", cancellationToken);

    /// <summary>
    /// Routes new work to the provider for every capability it serves
    /// (active-preference routing, ADR-0031 — the browser replacement
    /// demonstration) and returns its updated state.
    /// </summary>
    public Task<PluginDto> RouteNewWorkAsync(string providerId, CancellationToken cancellationToken = default) =>
        PostAsync<PluginDto>(
            $"/api/plugins/{Uri.EscapeDataString(providerId)}/route-new-work",
            content: null,
            cancellationToken);

    /// <summary>
    /// Drains the plugin worker: stops routing new work, waits for in-flight
    /// invocations, reclaims its resources and stops the process. The host
    /// keeps running (ADR-0031). Returns the worker's terminal state.
    /// </summary>
    public Task<PluginDto> DrainPluginAsync(string providerId, CancellationToken cancellationToken = default) =>
        PostAsync<PluginDto>(
            $"/api/plugins/{Uri.EscapeDataString(providerId)}/drain",
            content: null,
            cancellationToken);

    /// <summary>
    /// Rolls new work back to the provider: reactivates its package when it
    /// was drained or failed, then routes new work to it again (ADR-0031).
    /// </summary>
    public Task<PluginDto> RollbackPluginAsync(string providerId, CancellationToken cancellationToken = default) =>
        PostAsync<PluginDto>(
            $"/api/plugins/{Uri.EscapeDataString(providerId)}/rollback",
            content: null,
            cancellationToken);

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
        SpatialClientHelpers.EnsureSuccess(response);
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
}
