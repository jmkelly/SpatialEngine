using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;
using Spatial.PluginSdk.Providers;
using Spatial.PluginSdk.Transformations;

namespace Spatial.Client;

/// <summary>
/// The .NET client SDK for the typed spatial host API (ADR-0033): one
/// method per route, core geometry values in and out (canonical SGEOM/SFBAT
/// Base64 on the wire), structured <see cref="SpatialClientException"/>
/// failures. One <see cref="HttpClient"/> per client, configured with the
/// host's base address; HTTP mechanics and wire codecs live in
/// <see cref="SpatialClientTransport"/>.
/// </summary>
public sealed class SpatialClient
{
    private readonly SpatialClientTransport _transport;

    /// <summary>The tile surface (ADR-0046), split so this type's fan-out stays deliberate (ADR-0040).</summary>
    public SpatialTileClient Tiles { get; }

    /// <summary>Creates a client over an existing <see cref="HttpClient"/> whose base address is the host.</summary>
    public SpatialClient(HttpClient http)
    {
        _transport = new SpatialClientTransport(http);
        Tiles = new SpatialTileClient(_transport);
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
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/geometry/buffer",
            new BufferRequest(SpatialClientCodec.Encode(geometry), distance, quadrantSegments),
            cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }

    public async Task<IGeometry> IntersectionAsync(IGeometry left, IGeometry right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/geometry/intersection",
            new IntersectionRequest(SpatialClientCodec.Encode(left), SpatialClientCodec.Encode(right)),
            cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }

    public async Task<bool> ValidateAsync(IGeometry geometry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await _transport.PostAsync<ValidateResponse>(
            "/api/geometry/validate", new ValidateRequest(SpatialClientCodec.Encode(geometry)), cancellationToken);
        return response.Valid;
    }

    public async Task<IGeometry> SimplifyAsync(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/geometry/simplify", new SimplifyRequest(SpatialClientCodec.Encode(geometry), tolerance), cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }

    // ---- transforms ----

    public Task<CrsDescription> DescribeAsync(string crs, CancellationToken cancellationToken = default) =>
        _transport.PostAsync<CrsDescription>("/api/crs/describe", new DescribeRequest(crs), cancellationToken);

    public async Task<IGeometry> TransformAsync(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        var response = await _transport.PostAsync<GeometryResponse>(
            "/api/coordinates/transform", new TransformRequest(SpatialClientCodec.Encode(geometry), source, target), cancellationToken);
        return SpatialClientCodec.Decode(response.Geometry);
    }

    // ---- catalogue / datasets ----

    public async Task<IReadOnlyList<DatasetSummary>> ListCatalogueAsync(
        string store = "demo", string? pattern = null, CancellationToken cancellationToken = default)
    {
        var url = $"/api/catalogue?store={Uri.EscapeDataString(store)}"
            + (pattern is null ? string.Empty : $"&pattern={Uri.EscapeDataString(pattern)}");
        var response = await _transport.GetAsync<CatalogueResponse>(url, cancellationToken);
        return response.Datasets;
    }

    public Task<DatasetDescription> DescribeDatasetAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default) =>
        _transport.GetAsync<DatasetDescription>(
            $"/api/datasets/{Uri.EscapeDataString(dataset)}?store={Uri.EscapeDataString(store)}", cancellationToken);

    public async Task<string> CreateDatasetAsync(
        string dataset, FeatureBatch sample, int srid, string store = "postgis", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sample);
        var response = await _transport.PostAsync<CreateDatasetResponse>(
            $"/api/datasets?store={Uri.EscapeDataString(store)}",
            new CreateDatasetRequest(dataset, Convert.ToBase64String(FeatureBatchCodec.Encode(sample)), srid),
            cancellationToken);
        return response.Dataset;
    }

    // ---- features ----

    public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<FeatureBatchesResponse>(
            $"/api/features/scan?store={Uri.EscapeDataString(store)}",
            new ScanRequest(dataset), cancellationToken);
        return response.Batches.Select(SpatialClientCodec.DecodeBatch).ToArray();
    }

    public async Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, PluginSdk.BoundingBox? bbox = null, string? filter = null,
        string store = "demo", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<FeatureBatchesResponse>(
            $"/api/features/query?store={Uri.EscapeDataString(store)}",
            new FeatureQueryRequest(dataset, bbox is null ? null : new BboxDto(bbox.MinX, bbox.MinY, bbox.MaxX, bbox.MaxY), filter),
            cancellationToken);
        return response.Batches.Select(SpatialClientCodec.DecodeBatch).ToArray();
    }

    public async Task<int> WriteAsync(
        string dataset, FeatureBatch batch, string? transaction = null,
        string store = "postgis", CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var response = await _transport.PostAsync<FeatureWriteResponse>(
            $"/api/features/write?store={Uri.EscapeDataString(store)}",
            new FeatureWriteRequest(dataset, Convert.ToBase64String(FeatureBatchCodec.Encode(batch)), transaction),
            cancellationToken);
        return response.Appended;
    }

    // ---- transactions ----

    public async Task<string> BeginTransactionAsync(string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<BeginTransactionResponse>(
            $"/api/transactions/begin?store={Uri.EscapeDataString(store)}", new object(), cancellationToken);
        return response.Transaction;
    }

    public async Task<bool> CommitTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<TransactionResponse>(
            $"/api/transactions/commit?store={Uri.EscapeDataString(store)}",
            new TransactionRequest(transaction), cancellationToken);
        return response.Ok;
    }

    public async Task<bool> RollbackTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<TransactionResponse>(
            $"/api/transactions/rollback?store={Uri.EscapeDataString(store)}",
            new TransactionRequest(transaction), cancellationToken);
        return response.Ok;
    }

    // ---- rendering (ADR-0044) ----

    /// <summary>Renders a styled vector and imagery request to encoded image bytes.</summary>
    public async Task<RasterImage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transport.PostForImageAsync("/api/render", request, cancellationToken);
    }

    /// <summary>Renders a map's datasets using its persisted layer styles (ADR-0052).</summary>
    public async Task<RasterImage> RenderMapAsync(
        string name, MapRenderRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transport.PostForImageAsync(
            $"/api/maps/{Uri.EscapeDataString(name)}/render", request, cancellationToken);
    }

    /// <summary>Describes the configured raster formats, pixel cap and imagery sources.</summary>
    public Task<RenderCapabilitiesResponse> RenderCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        _transport.GetAsync<RenderCapabilitiesResponse>("/api/render/capabilities", cancellationToken);

    // ---- demo ----

    public async Task<long> SleepAsync(long milliseconds, CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<SleepResponse>("/api/demo/sleep", new SleepRequest(milliseconds), cancellationToken);
        return response.Slept;
    }

    // ---- maps & ingest (ADR-0052) ----

    /// <summary>Lists every map (declared first, then runtime by name).</summary>
    public Task<IReadOnlyList<Map>> ListMapsAsync(CancellationToken cancellationToken = default) =>
        _transport.SendAsync<IReadOnlyList<Map>>(HttpMethod.Get, "/api/maps", null, null, cancellationToken);

    /// <summary>Gets one map by name.</summary>
    public Task<Map> GetMapAsync(string name, CancellationToken cancellationToken = default) =>
        _transport.SendAsync<Map>(HttpMethod.Get, $"/api/maps/{Uri.EscapeDataString(name)}", null, null, cancellationToken);

    /// <summary>Creates or replaces a runtime map (requires the admin token).</summary>
    public Task<Map> PutMapAsync(
        Map map, string? adminToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(map);
        return _transport.SendAsync<Map>(
            HttpMethod.Put,
            $"/api/maps/{Uri.EscapeDataString(map.Name)}",
            SpatialClientCodec.Json(map),
            adminToken,
            cancellationToken);
    }

    /// <summary>Deletes a runtime map and reports whether it existed (requires the admin token).</summary>
    public async Task<bool> DeleteMapAsync(
        string name, string? adminToken = null, CancellationToken cancellationToken = default)
    {
        return await _transport.SendAsync<bool>(
            HttpMethod.Delete, $"/api/maps/{Uri.EscapeDataString(name)}", null, adminToken, cancellationToken);
    }

    /// <summary>
    /// Uploads a GeoJSON/NDJSON/CSV stream and loads it atomically into a
    /// dataset, optionally registering a publication in the same call
    /// (requires the admin token).
    /// </summary>
    public async Task<IngestOutcome> IngestAsync(
        Stream content,
        IngestUpload upload,
        string? adminToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(upload);
        var query = new Dictionary<string, string?>
        {
            ["store"] = upload.Store,
            ["dataset"] = upload.Dataset,
            ["srid"] = upload.Srid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["format"] = upload.Format,
            ["identity"] = upload.Identity,
            ["identityField"] = upload.IdentityField,
            ["publish"] = upload.Publish,
            ["sourceSrid"] = upload.SourceSrid?.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var queryString = string.Join('&', query
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StreamContent(content), "file", upload.FileName);
        return await _transport.SendAsync<IngestOutcome>(
            HttpMethod.Post, $"/api/ingest?{queryString}", multipart, adminToken, cancellationToken);
    }
}
