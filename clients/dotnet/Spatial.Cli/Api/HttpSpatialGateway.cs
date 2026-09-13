using System.Net.Http.Json;
using Spatial.Client;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli;

/// <summary>
/// The HTTP implementation of <see cref="ISpatialGateway"/> (ADR-0052): a
/// single <see cref="HttpClient"/> over the host's public API, with the .NET
/// client SDK for the typed routes. Host readiness is read directly because
/// the SDK has no health method; a local file or an <c>http(s)</c> URL is
/// streamed into <c>POST /api/ingest</c>.
/// </summary>
public sealed class HttpSpatialGateway : ISpatialGateway
{
    private readonly HttpClient _http;
    private readonly SpatialClient _client;

    /// <summary>Creates a gateway over an existing client (used by tests over a test server).</summary>
    public HttpSpatialGateway(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _client = new SpatialClient(_http);
    }

    /// <summary>Creates a gateway for the configured host.</summary>
    public HttpSpatialGateway(CliSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _http = new HttpClient
        {
            BaseAddress = new Uri(settings.Host, UriKind.Absolute),
            Timeout = settings.Timeout,
        };
        _client = new SpatialClient(_http);
    }

    /// <inheritdoc />
    public async Task<HostHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/health/ready", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new SpatialClientException(
                (int)response.StatusCode,
                "store.unavailable",
                $"The host at {_http.BaseAddress} is not ready (HTTP {(int)response.StatusCode}).");
        }

        var body = await response.Content.ReadFromJsonAsync<ReadyResponse>(HostApiJson.Options, cancellationToken);
        return new HostHealth(body?.Status ?? "ready", body?.Stores ?? []);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<DatasetSummary>> ListDatasetsAsync(string store, string? pattern, CancellationToken cancellationToken = default) =>
        _client.ListCatalogueAsync(store, pattern, cancellationToken);

    /// <inheritdoc />
    public Task<DatasetDescription> DescribeDatasetAsync(string dataset, string store, CancellationToken cancellationToken = default) =>
        _client.DescribeDatasetAsync(dataset, store, cancellationToken);

    /// <inheritdoc />
    public async Task<IngestOutcome> IngestAsync(string source, IngestUpload upload, string? adminToken, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentNullException.ThrowIfNull(upload);
        await using var stream = await OpenSourceAsync(source, cancellationToken);
        return await _client.IngestAsync(stream, upload, adminToken, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Publication>> ListPublicationsAsync(CancellationToken cancellationToken = default) =>
        _client.ListPublicationsAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<Publication?> FindPublicationAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _client.GetPublicationAsync(name, cancellationToken);
        }
        catch (SpatialClientException exception) when (exception.Code == "not.found")
        {
            return null;
        }
    }

    /// <inheritdoc />
    public Task<Publication> PutPublicationAsync(Publication publication, string? adminToken, CancellationToken cancellationToken = default) =>
        _client.PutPublicationAsync(publication, adminToken, cancellationToken);

    /// <inheritdoc />
    public Task<bool> DeletePublicationAsync(string name, string? adminToken, CancellationToken cancellationToken = default) =>
        _client.DeletePublicationAsync(name, adminToken, cancellationToken);

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    private async Task<Stream> OpenSourceAsync(string source, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return await _http.GetStreamAsync(uri, cancellationToken);
        }

        return File.OpenRead(source);
    }

    private sealed record ReadyResponse(string? Status, IReadOnlyList<string>? Stores);
}
