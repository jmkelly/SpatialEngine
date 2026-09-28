using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;
using Spatial.Contracts.Transformations;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Client;

/// <summary>
/// The .NET client SDK for the typed spatial host API (ADR-0033): one
/// method per route, core geometry values in and out (canonical SGEOM/SFBAT
/// Base64 on the wire), structured <see cref="SpatialClientException"/>
/// failures. One <see cref="HttpClient"/> per client, configured with the
/// host's base address; HTTP mechanics and wire codecs live in
/// <see cref="SpatialClientTransport"/>.
/// </summary>
/// <remarks>
/// The routes are grouped into sub-clients — <see cref="Auth"/>,
/// <see cref="Geometry"/>, <see cref="Data"/>, <see cref="Render"/>,
/// <see cref="Maps"/> and <see cref="Tiles"/> — so each group carries its own
/// wire types instead of this one type naming all of them (ADR-0040). Every
/// route is still reachable directly on the client; the methods here delegate
/// to the group that owns the route.
/// </remarks>
public sealed class SpatialClient
{
    private readonly SpatialClientTransport _transport;
    private readonly SpatialClientSession _session;

    /// <summary>The tile surface (ADR-0046), split so this type's fan-out stays deliberate (ADR-0040).</summary>
    public SpatialTileClient Tiles { get; }

    /// <summary>The auth surface (ADR-0071), split so this type's fan-out stays deliberate (ADR-0040).</summary>
    public SpatialAuthClient Auth { get; }

    /// <summary>The geometry and coordinate surface, split so this type's fan-out stays deliberate (ADR-0040).</summary>
    public SpatialGeometryClient Geometry { get; }

    /// <summary>The catalogue, dataset, feature and transaction surface (ADR-0040).</summary>
    public SpatialDataClient Data { get; }

    /// <summary>The render surface (ADR-0044), split so this type's fan-out stays deliberate (ADR-0040).</summary>
    public SpatialRenderClient Render { get; }

    /// <summary>The map and ingest surface (ADR-0053), split so this type's fan-out stays deliberate (ADR-0040).</summary>
    public SpatialMapClient Maps { get; }

    /// <summary>Creates a client over an existing <see cref="HttpClient"/> whose base address is the host.</summary>
    public SpatialClient(HttpClient http)
    {
        _transport = new SpatialClientTransport(http);
        _session = new SpatialClientSession();
        Tiles = new SpatialTileClient(_transport);
        Auth = new SpatialAuthClient(_transport, _session);
        Geometry = new SpatialGeometryClient(_transport);
        Data = new SpatialDataClient(_transport);
        Render = new SpatialRenderClient(_transport);
        Maps = new SpatialMapClient(_transport, _session);
    }

    /// <summary>Creates a client talking to the host at <paramref name="baseAddress"/>.</summary>
    public SpatialClient(string baseAddress)
        : this(new HttpClient { BaseAddress = new Uri(baseAddress, UriKind.Absolute) })
    {
    }

    // ---- auth (ADR-0071) ----

    /// <summary>Authenticates a configured local user and returns an opaque bearer.</summary>
    public Task<AuthToken> LoginAsync(string username, string password, CancellationToken cancellationToken = default) =>
        Auth.LoginAsync(username, password, cancellationToken);

    /// <summary>Revokes an opaque bearer.</summary>
    public Task LogoutAsync(string token, CancellationToken cancellationToken = default) =>
        Auth.LogoutAsync(token, cancellationToken);

    /// <summary>Rotates an opaque bearer.</summary>
    public Task<AuthToken> RefreshAsync(string token, CancellationToken cancellationToken = default) =>
        Auth.RefreshAsync(token, cancellationToken);

    /// <summary>Reads the authenticated identity and roles.</summary>
    public Task<AuthIdentity> MeAsync(string token, CancellationToken cancellationToken = default) =>
        Auth.MeAsync(token, cancellationToken);

    // ---- geometry ----

    /// <summary>Buffers a geometry by a distance in the host's working units.</summary>
    public Task<IGeometry> BufferAsync(IGeometry geometry, double distance, int quadrantSegments = 8, CancellationToken cancellationToken = default) =>
        Geometry.BufferAsync(geometry, distance, quadrantSegments, cancellationToken);

    /// <summary>Intersects two geometries.</summary>
    public Task<IGeometry> IntersectionAsync(IGeometry left, IGeometry right, CancellationToken cancellationToken = default) =>
        Geometry.IntersectionAsync(left, right, cancellationToken);

    /// <summary>Reports whether a geometry is valid, without throwing on an invalid one.</summary>
    public Task<bool> ValidateAsync(IGeometry geometry, CancellationToken cancellationToken = default) =>
        Geometry.ValidateAsync(geometry, cancellationToken);

    /// <summary>Simplifies a geometry to a tolerance.</summary>
    public Task<IGeometry> SimplifyAsync(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default) =>
        Geometry.SimplifyAsync(geometry, tolerance, cancellationToken);

    /// <summary>Describes a coordinate reference system.</summary>
    public Task<CrsDescription> DescribeAsync(string crs, CancellationToken cancellationToken = default) =>
        Geometry.DescribeAsync(crs, cancellationToken);

    /// <summary>Transforms a geometry between coordinate reference systems.</summary>
    public Task<IGeometry> TransformAsync(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default) =>
        Geometry.TransformAsync(geometry, source, target, cancellationToken);

    // ---- catalogue / datasets ----

    /// <summary>Lists the datasets a store publishes.</summary>
    public Task<IReadOnlyList<DatasetSummary>> ListCatalogueAsync(
        string store = "demo", string? pattern = null, CancellationToken cancellationToken = default) =>
        Data.ListCatalogueAsync(store, pattern, cancellationToken);

    /// <summary>Describes one dataset: its fields, extent and identity column.</summary>
    public Task<DatasetDescription> DescribeDatasetAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default) =>
        Data.DescribeDatasetAsync(dataset, store, cancellationToken);

    /// <summary>Creates a dataset from a sample batch and returns its name.</summary>
    public Task<string> CreateDatasetAsync(
        string dataset, FeatureBatch sample, int srid, string store = "postgis", CancellationToken cancellationToken = default) =>
        Data.CreateDatasetAsync(dataset, sample, srid, store, cancellationToken);

    // ---- features ----

    /// <summary>Scans every feature in a dataset.</summary>
    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(
        string dataset, string store = "demo", CancellationToken cancellationToken = default) =>
        Data.ScanAsync(dataset, store, cancellationToken);

    /// <summary>Queries features by extent, filter expression and store.</summary>
    public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, Contracts.BoundingBox? bbox = null, string? filter = null,
        string store = "demo", CancellationToken cancellationToken = default) =>
        Data.QueryAsync(dataset, bbox, filter, store, cancellationToken);

    /// <summary>Appends features to a dataset, optionally inside a transaction, and returns the count.</summary>
    public Task<int> WriteAsync(
        string dataset, FeatureBatch batch, string? transaction = null,
        string store = "postgis", CancellationToken cancellationToken = default) =>
        Data.WriteAsync(dataset, batch, transaction, store, cancellationToken);

    // ---- transactions ----

    /// <summary>Begins a transaction and returns its handle.</summary>
    public Task<string> BeginTransactionAsync(string store = "postgis", CancellationToken cancellationToken = default) =>
        Data.BeginTransactionAsync(store, cancellationToken);

    /// <summary>Commits a transaction.</summary>
    public Task<bool> CommitTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default) =>
        Data.CommitTransactionAsync(transaction, store, cancellationToken);

    /// <summary>Rolls a transaction back.</summary>
    public Task<bool> RollbackTransactionAsync(string transaction, string store = "postgis", CancellationToken cancellationToken = default) =>
        Data.RollbackTransactionAsync(transaction, store, cancellationToken);

    // ---- rendering (ADR-0044) ----

    /// <summary>Renders a styled vector and imagery request to encoded image bytes.</summary>
    public Task<RasterImage> RenderAsync(RenderRequest request, CancellationToken cancellationToken = default) =>
        Render.RenderAsync(request, cancellationToken);

    /// <summary>Renders a map's datasets using its persisted layer styles (ADR-0053).</summary>
    public Task<RasterImage> RenderMapAsync(
        string name, MapRenderRequestDto request, CancellationToken cancellationToken = default) =>
        Render.RenderMapAsync(name, request, cancellationToken);

    /// <summary>Describes the configured raster formats, pixel cap and imagery sources.</summary>
    public Task<RenderCapabilitiesResponse> RenderCapabilitiesAsync(CancellationToken cancellationToken = default) =>
        Render.RenderCapabilitiesAsync(cancellationToken);

    // ---- demo ----

    /// <summary>Sleeps on the host for the given milliseconds and reports the elapsed time.</summary>
    public async Task<long> SleepAsync(long milliseconds, CancellationToken cancellationToken = default)
    {
        var response = await _transport.PostAsync<SleepResponse>("/api/demo/sleep", new SleepRequest(milliseconds), cancellationToken);
        return response.Slept;
    }

    // ---- maps & ingest (ADR-0053) ----

    /// <summary>Lists every map (declared first, then runtime by name).</summary>
    public Task<IReadOnlyList<Map>> ListMapsAsync(CancellationToken cancellationToken = default) =>
        Maps.ListMapsAsync(cancellationToken);

    /// <summary>Gets one map by name.</summary>
    public Task<Map> GetMapAsync(string name, CancellationToken cancellationToken = default) =>
        Maps.GetMapAsync(name, cancellationToken);

    /// <summary>Creates or replaces a runtime map (requires the admin token).</summary>
    public Task<Map> PutMapAsync(Map map, string? adminToken = null, CancellationToken cancellationToken = default) =>
        Maps.PutMapAsync(map, adminToken, cancellationToken);

    /// <summary>Deletes a runtime map and reports whether it existed (requires the admin token).</summary>
    public Task<bool> DeleteMapAsync(string name, string? adminToken = null, CancellationToken cancellationToken = default) =>
        Maps.DeleteMapAsync(name, adminToken, cancellationToken);

    /// <summary>
    /// Uploads a GeoJSON/NDJSON/CSV stream and loads it atomically into a
    /// dataset, optionally registering a publication in the same call
    /// (requires the admin token).
    /// </summary>
    public Task<IngestOutcome> IngestAsync(
        Stream content,
        IngestUpload upload,
        string? adminToken = null,
        CancellationToken cancellationToken = default) =>
        Maps.IngestAsync(content, upload, adminToken, cancellationToken);

    /// <summary>
    /// Stages a large upload in resumable chunks and ingests it (ADR-0090).
    /// The driver asks the host where the staging got to, continues from there
    /// and ingests once every byte has landed.
    /// </summary>
    public Task<IngestOutcome> IngestResumableAsync(
        Stream content,
        IngestUpload upload,
        ResumableUpload? options = null,
        string? adminToken = null,
        CancellationToken cancellationToken = default) =>
        ResumableIngest.UploadAsync(this, content, upload, options, adminToken, cancellationToken);

    /// <summary>Opens a staged upload (requires the admin token).</summary>
    public Task<UploadState> StartUploadAsync(
        string? uploadId,
        long? totalBytes = null,
        string? sha256 = null,
        string? adminToken = null,
        CancellationToken cancellationToken = default) =>
        Maps.StartUploadAsync(uploadId, totalBytes, sha256, adminToken, cancellationToken);

    /// <summary>How much of a staged upload has landed (requires the admin token).</summary>
    public Task<UploadState> GetUploadAsync(
        string uploadId, string? adminToken = null, CancellationToken cancellationToken = default) =>
        Maps.GetUploadAsync(uploadId, adminToken, cancellationToken);

    /// <summary>Lists every staged upload (requires the admin token).</summary>
    public Task<IReadOnlyList<UploadState>> ListUploadsAsync(
        string? adminToken = null, CancellationToken cancellationToken = default) =>
        Maps.ListUploadsAsync(adminToken, cancellationToken);

    /// <summary>Appends one chunk of a staged upload (requires the admin token).</summary>
    public Task<UploadState> AppendUploadAsync(
        string uploadId,
        Stream content,
        long offset,
        long? totalBytes = null,
        string? sha256 = null,
        string? adminToken = null,
        CancellationToken cancellationToken = default) =>
        Maps.AppendUploadAsync(uploadId, content, offset, totalBytes, sha256, adminToken, cancellationToken);

    /// <summary>Discards a staged upload (requires the admin token).</summary>
    public Task<bool> DeleteUploadAsync(
        string uploadId, string? adminToken = null, CancellationToken cancellationToken = default) =>
        Maps.DeleteUploadAsync(uploadId, adminToken, cancellationToken);

    /// <summary>
    /// Ingests a staged upload rather than a body (requires the admin token).
    /// </summary>
    public Task<IngestOutcome> IngestUploadAsync(
        string uploadId, IngestUpload upload, string? adminToken = null, CancellationToken cancellationToken = default) =>
        Maps.IngestUploadAsync(uploadId, upload, adminToken, cancellationToken);

    /// <summary>
    /// Runs a seed document against a Development host: download, ingest and
    /// publish in one call (ADR-0078). Delegates to the map and ingest group
    /// that owns the route (ADR-0040).
    /// </summary>
    public Task<SeedResponse> SeedAsync(
        SeedRequest request, string? adminToken = null, CancellationToken cancellationToken = default) =>
        Maps.SeedAsync(request, adminToken, cancellationToken);
}
