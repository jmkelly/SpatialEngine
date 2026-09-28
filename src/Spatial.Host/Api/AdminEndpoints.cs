using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Ingest.Codec;
using Spatial.Maps;

namespace Spatial.Host.Api;

/// <summary>
/// The neutral admin surface (ADR-0053 §4, evolving ADR-0041 §5): map CRUD and
/// the single ingest request. Mutation routes require the configured admin
/// token (env <c>SPATIAL_ADMIN_TOKEN</c>) compared in constant time; when no
/// token is configured they are not mounted at all, while the read routes stay
/// available. Ingest decodes a raw or multipart upload with
/// <see cref="DatasetDecoder"/> and loads it atomically through the target
/// store's <see cref="IDatasetIngest"/>. A <c>publish</c> query parameter
/// registers the uploaded dataset as a one-layer Feature map in the same call
/// and reports the partial state safely retryably. An <c>upload</c> query
/// parameter names a staged upload to load instead (ADR-0089), which is how a
/// document too large for one request is ingested without ever being re-sent.
///
/// <para>The pre-ADR-0053 <c>/api/publications</c> aliases were removed in
/// 0.2.0; <c>/api/maps</c> is canonical (unknown routes answer 404).</para>
/// </summary>
internal static class AdminEndpoints
{
    /// <summary>The store ingest defaults to: the always-available writable in-memory provider.</summary>
    public const string DefaultIngestStore = "memory";

    public static void Map(
        IEndpointRouteBuilder app,
        AdminOptions admin,
        IngestOptions ingest,
        AuthOptions authOptions,
        IAuthService auth)
    {
        app.MapGet("/api/maps", ListMaps).Produces<IReadOnlyList<Map>>();
        app.MapGet("/api/maps/{name}", GetMap).Produces<Map>();

        if (!admin.Enabled && !authOptions.Enabled)
        {
            return;
        }

        app.MapPut("/api/maps/{name}", (string name, Map map, HttpContext context, IStoreRegistry stores, IMapRegistry registry, CancellationToken token) =>
            PutMap(new AdminRoute(context, admin, auth, name, stores, registry, token), map));
        app.MapDelete("/api/maps/{name}", (string name, HttpContext context, IMapRegistry registry, CancellationToken token) =>
            DeleteMap(context, admin, auth, name, registry, token));
        app.MapPost("/api/ingest", (HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, IMapRegistry registry, IUploadStaging uploads, CancellationToken token) =>
            Ingest(
                new AdminRoute(context, admin, auth, string.Empty, stores, registry, token), ingest, transforms, uploads));
        UploadEndpoints.Map(app, admin, authOptions, auth);
    }

    private static async Task<IResult> ListMaps(IMapRegistry registry, CancellationToken token)
    {
        try
        {
            return Results.Ok(await registry.ListAsync(token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> GetMap(string name, IMapRegistry registry, CancellationToken token)
    {
        try
        {
            return Results.Ok(await registry.GetAsync(Uri.UnescapeDataString(name), token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// The seams every admin-gated route shares: the caller and its
    /// cancellation, the authorization policy, the addressed map name and the
    /// registries the route works through. Grouping them keeps each route's
    /// signature to the payload it carries.
    /// </summary>
    private sealed record AdminRoute(
        HttpContext Context,
        AdminOptions Admin,
        IAuthService Auth,
        string Name,
        IStoreRegistry Stores,
        IMapRegistry Registry,
        CancellationToken Token);

    private static async Task<IResult> PutMap(AdminRoute route, Map map)
    {
        var (context, admin, auth, name, stores, registry, token) = route;
        try
        {
            await Authorize(context, admin, auth, token);
            var named = map with { Name = Uri.UnescapeDataString(name) };
            await EnsureLayersAreServableAsync(stores, named, token);
            var stored = await registry.PutAsync(named, token);
            return Results.Ok(stored);
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Rejects a map whose layers cannot be served, so a dangling dataset or a
    /// missing raster provider never becomes a published service that fails on
    /// every request: a feature layer must exist in its store's catalogue and an
    /// image layer needs the store to expose an <see cref="IRasterCatalogue"/>.
    /// Declared relationships (ADR-0077) are checked against the same live
    /// schemas here, at declaration time, so a relationship over a column that
    /// does not exist never reaches a served service.
    /// </summary>
    internal static async Task EnsureLayersAreServableAsync(IStoreRegistry stores, Map map, CancellationToken token)
    {
        foreach (var layer in map.Layers)
        {
            var store = layer.Store ?? map.Store;
            if (layer.Kind == MapLayerKind.Image)
            {
                var raster = stores.RasterCatalogue(store)
                    ?? throw SpatialException.BadArguments(
                        $"Map '{map.Name}' exposes an image layer but store '{store}' has no raster provider.");
                await raster.DescribeAsync(layer.Dataset, token);
                continue;
            }

            var catalogue = stores.Catalogue(store);
            await catalogue.DescribeAsync(layer.Dataset, token);
        }

        await MapRelationshipSchemas.ValidateAsync(
            map,
            (store, dataset, cancellationToken) => stores.Catalogue(store).DescribeAsync(dataset, cancellationToken),
            token);
    }

    private static async Task<IResult> DeleteMap(
        HttpContext context, AdminOptions admin, IAuthService auth, string name, IMapRegistry registry, CancellationToken token)
    {
        try
        {
            await Authorize(context, admin, auth, token);
            return Results.Ok(await registry.DeleteAsync(Uri.UnescapeDataString(name), token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    private static async Task<IResult> Ingest(
        AdminRoute route, IngestOptions ingest, ICoordinateTransforms transforms, IUploadStaging uploads)
    {
        var (context, admin, auth, _, stores, registry, token) = route;
        try
        {
            await Authorize(context, admin, auth, token);
            var query = context.Request.Query;
            var dataset = Required(query, "dataset");
            var srid = IngestPipeline.ParseSrid(Required(query, "srid"));
            var store = query["store"].ToString();
            if (string.IsNullOrWhiteSpace(store))
            {
                store = DefaultIngestStore;
            }

            var format = IngestPipeline.ParseFormat(ingest, query["format"].ToString());
            var identityField = EmptyToNull(query["identityField"].ToString());
            var identity = IngestPipeline.ParseIdentity(query["identity"].ToString());
            var sourceSrid = IngestPipeline.ParseOptionalSrid(query["sourceSrid"].ToString());
            var target = stores.Ingest(store)
                ?? throw SpatialException.BadArguments($"Store '{store}' does not support ingest.");

            var outcome = await LoadAsync(
                context, ingest, format, dataset, srid, sourceSrid, target, identity, identityField, transforms, EmptyToNull(query["upload"].ToString()), uploads, token);

            return Results.Ok(await WithMapAsync(registry, query["publish"].ToString(), store, outcome, token));
        }
        catch (Exception exception)
        {
            return ErrorMapper.Map(exception);
        }
    }

    /// <summary>Reads the upload body (raw or multipart) under the byte cap, then decodes and loads it.</summary>
    private static async Task<IngestOutcome> LoadAsync(
        HttpContext context,
        IngestOptions ingest,
        IngestFormat format,
        string dataset,
        int srid,
        int? sourceSrid,
        IDatasetIngest target,
        IngestIdentity identity,
        string? identityField,
        ICoordinateTransforms transforms,
        string? uploadId,
        IUploadStaging uploads,
        CancellationToken token)
    {
        if (uploadId is not null)
        {
            return await LoadStagedAsync(context, ingest, format, dataset, srid, sourceSrid, target, identity, identityField, transforms, uploads, uploadId, token);
        }

        await using var body = await ReadUploadAsync(context.Request, ingest.MaxBytes);
        return await IngestPipeline.LoadAsync(
            body, format, dataset, srid, sourceSrid, target, target as IDatasetIngestStream,
            ingest, identity, identityField, transforms, token);
    }

    /// <summary>
    /// Ingests a staged upload (ADR-0089). Only a <em>complete</em> staged
    /// upload is loaded: a partial one is refused by name and by offset, so a
    /// client that believes it has finished cannot load half a document. The
    /// staged bytes are decoded and loaded exactly as a body would be, in one
    /// transaction, and the staging is discarded only once the load has
    /// committed — a failed load leaves the bytes staged for a retry.
    /// </summary>
    private static async Task<IngestOutcome> LoadStagedAsync(
        HttpContext context,
        IngestOptions ingest,
        IngestFormat format,
        string dataset,
        int srid,
        int? sourceSrid,
        IDatasetIngest target,
        IngestIdentity identity,
        string? identityField,
        ICoordinateTransforms transforms,
        IUploadStaging uploads,
        string uploadId,
        CancellationToken token)
    {
        if (context.Request.ContentLength is > 0)
        {
            throw SpatialException.BadArguments(
                "An ingest naming an 'upload' reads the staged bytes; send the document through PUT /api/uploads instead of a request body.");
        }

        var state = await uploads.DescribeAsync(uploadId, token);
        if (!state.Complete)
        {
            throw SpatialException.BadArguments(
                $"Staged upload '{uploadId}' is not complete: {state.Received} of {DescribeTotal(state)} byte(s) are staged. A partial upload is never ingested.");
        }

        await using var body = await uploads.OpenAsync(uploadId, token);
        var outcome = await IngestPipeline.LoadAsync(
            body, format, dataset, srid, sourceSrid, target, target as IDatasetIngestStream,
            ingest, identity, identityField, transforms, token);
        await uploads.DiscardAsync(uploadId, token);
        return outcome;
    }

    private static string DescribeTotal(UploadState state) =>
        state.TotalBytes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an undeclared number of";

    private static async Task<IngestOutcome> WithMapAsync(
        IMapRegistry registry, string publish, string store, IngestOutcome outcome, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(publish))
        {
            return outcome;
        }

        var name = Uri.UnescapeDataString(publish);
        Map? existing = null;
        try
        {
            existing = await registry.GetAsync(name, token);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
        }

        var layers = existing?.Layers.ToList() ?? [];
        if (layers.TrueForAll(layer => !string.Equals(layer.Dataset, outcome.Dataset, StringComparison.Ordinal)))
        {
            var next = layers.Count == 0 ? 0 : layers.Max(layer => layer.LayerId) + 1;
            layers.Add(new MapLayer(outcome.Dataset, next));
        }

        var map = await registry.PutAsync(
            existing is null
                ? new Map(name, store, layers, [MapServiceKind.FeatureServer])
                : existing with { Store = store, Layers = layers, Services = [.. existing.Services.Union([MapServiceKind.FeatureServer])] },
            token);
        return outcome with { Map = map };
    }

    /// <summary>Reads the request body into memory, enforcing the byte cap for raw and multipart bodies.</summary>
    private static async Task<MemoryStream> ReadUploadAsync(HttpRequest request, long maxBytes) =>
        request.HasFormContentType
            ? await ReadFormUploadAsync(request, maxBytes)
            : await ReadRawUploadAsync(request, maxBytes);

    /// <summary>The malformed-body failures a multipart upload maps to a client error.</summary>
    private static readonly Type[] MalformedUploadFailures =
        [typeof(InvalidDataException), typeof(IOException), typeof(BadHttpRequestException)];

    private static bool IsMalformedUpload(Exception exception) =>
        MalformedUploadFailures.Any(failure => failure.IsInstanceOfType(exception));

    private static async Task<MemoryStream> ReadFormUploadAsync(HttpRequest request, long maxBytes)
    {
        var file = FirstFile(await ReadFormAsync(request));
        return await CopyFormFileAsync(file, maxBytes);
    }

    private static IFormFile FirstFile(IFormCollection form) =>
        form.Files.Count > 0
            ? form.Files[0]
            : throw SpatialException.BadArguments("The multipart upload carries no file part.");

    private static async Task<MemoryStream> CopyFormFileAsync(IFormFile file, long maxBytes)
    {
        if (file.Length > maxBytes)
        {
            throw SpatialException.BadArguments($"The upload is {file.Length} bytes, above the configured maximum of {maxBytes}.");
        }

        var multipart = new MemoryStream();
        await using var source = file.OpenReadStream();
        await source.CopyToAsync(multipart);
        multipart.Position = 0;
        return multipart;
    }

    private static async Task<IFormCollection> ReadFormAsync(HttpRequest request)
    {
        try
        {
            return await request.ReadFormAsync();
        }
        catch (Exception exception) when (IsMalformedUpload(exception))
        {
            throw SpatialException.BadArguments(
                $"The multipart upload is malformed and the 'file' part could not be read: {exception.Message}");
        }
    }

    private static async Task<MemoryStream> ReadRawUploadAsync(HttpRequest request, long maxBytes)
    {
        var buffer = new MemoryStream();
        await CopyBodyAsync(request.Body, buffer, maxBytes);
        buffer.Position = 0;
        return buffer;
    }

    private static async Task CopyBodyAsync(Stream body, MemoryStream buffer, long maxBytes)
    {
        var chunk = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await body.ReadAsync(chunk)) > 0)
        {
            total += read;
            buffer.Write(chunk, 0, read);
            RequireWithinCap(total, maxBytes);
        }
    }

    private static void RequireWithinCap(long total, long maxBytes)
    {
        if (total > maxBytes)
        {
            throw SpatialException.BadArguments($"The upload exceeds the configured maximum of {maxBytes} bytes.");
        }
    }

    private static string Required(IQueryCollection query, string key)
    {
        var value = query[key].ToString();
        return string.IsNullOrWhiteSpace(value)
            ? throw SpatialException.BadArguments($"The '{key}' query parameter is required.")
            : value;
    }

    private static string? EmptyToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static async Task Authorize(
        HttpContext context,
        AdminOptions admin,
        IAuthService auth,
        CancellationToken token) =>
        await AdminAuthorization.RequireAsync(context, admin, auth, token).ConfigureAwait(false);
}
