using System.Net;
using System.Net.Http.Json;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Transformations;

namespace Spatial.Client;

/// <summary>
/// The .NET client SDK for the typed spatial host API (ADR-0033): one
/// method per route, core geometry values in and out (canonical SGEOM/SFBAT
/// Base64 on the wire), structured <see cref="SpatialClientException"/>
/// failures. One <see cref="HttpClient"/> per client, configured with the
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

    // ---- geometry ----

    public async Task<IGeometry> BufferAsync(IGeometry geometry, double distance, int quadrantSegments = 8, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await PostAsync<GeometryResponse>(
            "/api/geometry/buffer",
            new BufferRequest(Encode(geometry), distance, quadrantSegments),
            cancellationToken);
        return Decode(response.Geometry);
    }

    public async Task<IGeometry> IntersectionAsync(IGeometry left, IGeometry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var response = await PostAsync<GeometryResponse>(
            "/api/geometry/intersection",
            new IntersectionRequest(Encode(left), Encode(right)),
            cancellationToken);
        return Decode(response.Geometry);
    }

    public async Task<bool> ValidateAsync(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await PostAsync<ValidateResponse>(
            "/api/geometry/validate", new ValidateRequest(Encode(geometry)), cancellationToken);
        return response.Valid;
    }

    public async Task<IGeometry> SimplifyAsync(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await PostAsync<GeometryResponse>(
            "/api/geometry/simplify", new SimplifyRequest(Encode(geometry), tolerance), cancellationToken);
        return Decode(response.Geometry);
    }

    // ---- transforms ----

    public Task<CrsDescription> DescribeAsync(string crs, CancellationToken cancellationToken = default) =>
        PostAsync<CrsDescription>("/api/crs/describe", new DescribeRequest(crs), cancellationToken);

    public async Task<IGeometry> TransformAsync(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await PostAsync<GeometryResponse>(
            "/api/coordinates/transform", new TransformRequest(Encode(geometry), source, target), cancellationToken);
        return Decode(response.Geometry);
    }

    // ---- catalogue / datasets ----

    public async Task<IReadOnlyList<DatasetSummary>> ListCatalogueAsync(
        string store = "demo", string? pattern = null, CancellationToken cancellationToken = default)
    {
        var url = $"/api/catalogue?store={Uri.EscapeDataString(store)}"
            + (pattern is null ? string.Empty : $"&pattern={Uri.EscapeDataString(pattern)}");
        var response = await GetAsync<CatalogueResponse>(url, cancellationToken);
        return response.Datasets;
    }

    public Task<DatasetDescription> DescribeDatasetAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default) =>
        GetAsync<DatasetDescription>(
            $"/api/datasets/{Uri.EscapeDataString(dataset)}?store={Uri.EscapeDataString(store)}", cancellationToken);

    public async Task<string> CreateDatasetAsync(
        string dataset, FeatureBatch sample, int srid, string store = "postgis", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var response = await PostAsync<CreateDatasetResponse>(
            $"/api/datasets?store={Uri.EscapeDataString(store)}",
            new CreateDatasetRequest(dataset, Convert.ToBase64String(FeatureBatchCodec.Encode(sample)), srid),
            cancellationToken);
        return response.Dataset;
    }

    // ---- features ----

    public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<FeatureBatchesResponse>(
            $"/api/features/scan?store={Uri.EscapeDataString(store)}",
            new ScanRequest(dataset), cancellationToken);
        return response.Batches.Select(DecodeBatch).ToArray();
    }

    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, PluginSdk.BoundingBox? bbox = null, string? filter = null,
        string store = "demo", CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<FeatureBatchesResponse>(
            $"/api/features/query?store={Uri.EscapeDataString(store)}",
            new FeatureQueryRequest(dataset, bbox is null ? null : new BboxDto(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY), filter),
            cancellationToken);
        return response.Batches.Select(DecodeBatch).ToArray();
    }

    public async Task<int> WriteAsync(
        string dataset, FeatureBatch batch, string? transaction = null,
        string store = "postgis", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var response = await PostAsync<FeatureWriteResponse>(
            $"/api/features/write?store={Uri.EscapeDataString(store)}",
            new FeatureWriteRequest(dataset, Convert.ToBase64String(FeatureBatchCodec.Encode(batch)), transaction),
            cancellationToken);
        return response.Appended;
    }

    // ---- transactions ----

    public async Task<string> BeginTransactionAsync(string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<BeginTransactionResponse>(
            $"/api/transactions/begin?store={Uri.EscapeDataString(store)}", new object(), cancellationToken);
        return response.Transaction;
    }

    public async Task<bool> CommitTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<TransactionResponse>(
            $"/api/transactions/commit?store={Uri.EscapeDataString(store)}",
            new TransactionRequest(transaction), cancellationToken);
        return response.Ok;
    }

    public async Task<bool> RollbackTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<TransactionResponse>(
            $"/api/transactions/rollback?store={Uri.EscapeDataString(store)}",
            new TransactionRequest(transaction), cancellationToken);
        return response.Ok;
    }

    // ---- demo ----

    public async Task<long> SleepAsync(long milliseconds, CancellationToken cancellationToken = default)
    {
        var response = await PostAsync<SleepResponse>("/api/demo/sleep", new SleepRequest(milliseconds), cancellationToken);
        return response.Slept;
    }

    // ---- transport ----

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<T> PostAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(url, body, HostApiJson.Options, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(HostApiJson.Options, cancellationToken);
            return value ?? throw new SpatialClientException(500, "empty", "The host returned an empty body.");
        }

        throw await FailAsync(response, cancellationToken);
    }

    private static async Task<SpatialClientException> FailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(HostApiJson.Options, cancellationToken);
            if (error is not null)
            {
                return new SpatialClientException((int)response.StatusCode, error.Code, error.Message);
            }
        }
        catch (Exception)
        {
            // Fall through to the status-only failure below.
        }

        return new SpatialClientException((int)response.StatusCode, "http.error", $"The host failed with {(HttpStatusCode)response.StatusCode}.");
    }

    private static string Encode(IGeometry geometry) =>
        Convert.ToBase64String(GeometryCodec.Encode(geometry));

    private static IGeometry Decode(string base64) =>
        GeometryCodec.Decode(Convert.FromBase64String(base64));

    private static FeatureBatch DecodeBatch(string base64) =>
        FeatureBatchCodec.Decode(Convert.FromBase64String(base64));
}
