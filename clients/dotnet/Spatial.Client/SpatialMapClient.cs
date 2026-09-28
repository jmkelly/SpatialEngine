using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Contracts.Providers;

namespace Spatial.Client;

/// <summary>
/// The map and ingest surface of the .NET client (ADR-0053): the runtime map
/// registry and the stream ingest that can publish a map in the same call.
/// Both admin routes default to the bearer the client last authenticated
/// with. Split from <see cref="SpatialClient"/> so the client's fan-out stays
/// deliberate (ADR-0040); reach it through <see cref="SpatialClient.Maps"/>.
/// </summary>
public sealed class SpatialMapClient
{
    private readonly SpatialClientTransport _transport;
    private readonly SpatialClientSession _session;

    internal SpatialMapClient(SpatialClientTransport transport, SpatialClientSession session)
    {
        _transport = transport;
        _session = session;
    }

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
            adminToken ?? _session.Token,
            cancellationToken);
    }

    /// <summary>Deletes a runtime map and reports whether it existed (requires the admin token).</summary>
    public async Task<bool> DeleteMapAsync(
        string name, string? adminToken = null, CancellationToken cancellationToken = default)
    {
        return await _transport.SendAsync<bool>(
            HttpMethod.Delete, $"/api/maps/{Uri.EscapeDataString(name)}", null, adminToken ?? _session.Token, cancellationToken);
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
        var queryString = IngestQuery(upload);

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StreamContent(content), "file", upload.FileName);
        return await _transport.SendAsync<IngestOutcome>(
            HttpMethod.Post, $"/api/ingest?{queryString}", multipart, adminToken ?? _session.Token, cancellationToken);
    }

    /// <summary>
    /// Opens a staged upload (ADR-0090). Passing the same
    /// <paramref name="uploadId"/> again returns the existing upload rather
    /// than truncating it, so a client whose create response was lost recovers
    /// instead of restarting.
    /// </summary>
    public Task<UploadState> StartUploadAsync(
        string? uploadId,
        long? totalBytes = null,
        string? sha256 = null,
        string? adminToken = null,
        CancellationToken cancellationToken = default) =>
        _transport.SendAsync<UploadState>(
            HttpMethod.Post,
            $"/api/uploads?{Query(("id", uploadId), ("total", Invariant(totalBytes)), ("sha256", sha256))}",
            null,
            adminToken ?? _session.Token,
            cancellationToken);

    /// <summary>Lists every staged upload.</summary>
    public Task<IReadOnlyList<UploadState>> ListUploadsAsync(
        string? adminToken = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync<IReadOnlyList<UploadState>>(
            HttpMethod.Get, "/api/uploads", null, adminToken ?? _session.Token, cancellationToken);

    /// <summary>How much of a staged upload has landed — the offset to resume from.</summary>
    public Task<UploadState> GetUploadAsync(
        string uploadId, string? adminToken = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync<UploadState>(
            HttpMethod.Get,
            $"/api/uploads/{Uri.EscapeDataString(uploadId)}",
            null,
            adminToken ?? _session.Token,
            cancellationToken);

    /// <summary>
    /// Appends a chunk whose first byte belongs at <paramref name="offset"/>.
    /// The last chunk declares the total size and the digest, which is what
    /// makes the staged upload complete.
    /// </summary>
    public Task<UploadState> AppendUploadAsync(
        string uploadId,
        Stream content,
        long offset,
        long? totalBytes = null,
        string? sha256 = null,
        string? adminToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var body = new StreamContent(content);
        body.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return _transport.SendAsync<UploadState>(
            HttpMethod.Put,
            $"/api/uploads/{Uri.EscapeDataString(uploadId)}?{Query(("offset", Invariant(offset)), ("total", Invariant(totalBytes)), ("sha256", sha256))}",
            body,
            adminToken ?? _session.Token,
            cancellationToken);
    }

    /// <summary>Discards a staged upload and its bytes.</summary>
    public Task<bool> DeleteUploadAsync(
        string uploadId, string? adminToken = null, CancellationToken cancellationToken = default) =>
        _transport.SendAsync<bool>(
            HttpMethod.Delete,
            $"/api/uploads/{Uri.EscapeDataString(uploadId)}",
            null,
            adminToken ?? _session.Token,
            cancellationToken);

    /// <summary>
    /// Ingests a staged upload instead of a body (ADR-0090). The host loads it
    /// only when every declared byte is staged, and discards the staging once
    /// the load has committed.
    /// </summary>
    public Task<IngestOutcome> IngestUploadAsync(
        string uploadId,
        IngestUpload upload,
        string? adminToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uploadId);
        ArgumentNullException.ThrowIfNull(upload);
        return _transport.SendAsync<IngestOutcome>(
            HttpMethod.Post,
            $"/api/ingest?{IngestQuery(upload, stagedUpload: uploadId)}",
            null,
            adminToken ?? _session.Token,
            cancellationToken);
    }

    private static string Query(params (string Key, string? Value)[] parameters) =>
        string.Join('&', parameters
            .Where(parameter => !string.IsNullOrEmpty(parameter.Value))
            .Select(parameter => $"{parameter.Key}={Uri.EscapeDataString(parameter.Value!)}"));

    private static string? Invariant(long? value) =>
        value?.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string IngestQuery(IngestUpload upload, string? stagedUpload = null)
    {
        var query = new Dictionary<string, string?>
        {
            ["store"] = upload.Store,
            ["dataset"] = upload.Dataset,
            ["srid"] = upload.Srid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["format"] = upload.Format,
            ["identity"] = upload.Identity,
            ["identityField"] = upload.IdentityField,
            ["publish"] = upload.Publish,
            ["sourceSrid"] = Invariant(upload.SourceSrid),
            ["upload"] = stagedUpload,
        };
        return string.Join('&', query
            .Where(pair => !string.IsNullOrEmpty(pair.Value))
            .Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value!)}"));
    }

    /// <summary>
    /// Runs a seed document against a Development host: download, ingest and
    /// publish in one call (ADR-0078). Admin route, so it defaults to the
    /// bearer the client last authenticated with.
    /// </summary>
    public async Task<SeedResponse> SeedAsync(
        SeedRequest request, string? adminToken = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await _transport.SendAsync<SeedResponse>(
            HttpMethod.Post,
            "/api/seed",
            SpatialClientCodec.Json(request),
            adminToken ?? _session.Token,
            cancellationToken);
    }
}
