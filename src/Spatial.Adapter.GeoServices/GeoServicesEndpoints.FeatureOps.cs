using Spatial.Interop.Esri;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature write-model operations (T-038, ADR-0058): the service-level
/// <c>FeatureServer/query</c> (S1), the per-layer <c>generateRenderer</c>
/// (reusing the T-039 <see cref="MapGenerateRenderer"/> classification
/// rather than duplicating it), <c>validateSQL</c> (S4), the honestly
/// rejected aggregation extensions (<c>queryBins</c>,
/// <c>queryTopFeatures</c>, <c>queryAnalytic</c>), and the attachment
/// surface served on the <c>IFeatureAttachmentStore</c> capability (T-061,
/// ADR-0066): reads follow feature-query auth (public), writes require the
/// single admin token (ADR-0065 §3).
/// </summary>
public static partial class GeoServicesEndpoints
{
    internal static void MapFeatureOps(RouteGroupBuilder group, GeoServicesCatalog catalog, IMapRegistry registry, string? adminToken = null)
    {
        group.MapMethods("/{service}/FeatureServer/query", ["GET", "POST"], (
            string service, HttpContext context, IStoreRegistry stores,
            IGeometryOperations operations, ICoordinateTransforms transforms, CancellationToken cancellationToken) =>
            FeatureServiceQuery(catalog, registry, service, context, stores, operations, transforms, cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/generateRenderer", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureGenerateRenderer(catalog, registry, service, layerId, context, stores, cancellationToken));

        group.MapMethods("/{service}/FeatureServer/{layerId:int}/validateSQL", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureValidateSql(catalog, registry, service, layerId, context, stores, cancellationToken));

        // Aggregation extensions without an engine model (ADR-0058 §4):
        // mounted so clients get a typed invalid-arguments failure naming
        // the served alternative instead of a bare 404.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryBins", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryBins",
                "binned aggregation has no engine model; use 'query' with 'outStatistics' and 'groupByFieldsForStatistics' instead.",
                cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryTopFeatures", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryTopFeatures",
                "top-N aggregation has no engine model; use 'query' with 'orderByFields' and 'resultRecordCount' instead.",
                cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryAnalytic", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            UnsupportedLayerOperation(catalog, registry, service, layerId, context, stores,
                "queryAnalytic",
                "analytic aggregation has no engine model; use 'query' with 'outStatistics' instead.",
                cancellationToken));

        // Attachments (T-061, ADR-0066): reads are served on the store's
        // attachment capability (public, like the features they annotate),
        // writes require the single admin token (ADR-0065 §3). Layers whose
        // store exposes no capability keep the honest surface: empty reads
        // and typed write rejects naming the missing capability.
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/queryAttachments", ["GET", "POST"], (
            string service, int layerId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureQueryAttachments(catalog, registry, service, layerId, context, stores, cancellationToken));
        group.MapMethods("/{service}/FeatureServer/{layerId:int}/{objectId:long}/attachments", ["GET", "POST"], (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentInfos(catalog, registry, service, layerId, objectId, context, stores, cancellationToken));
        group.MapGet("/{service}/FeatureServer/{layerId:int}/{objectId:long}/attachments/{attachmentId:long}", (
            string service, int layerId, long objectId, long attachmentId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAttachmentContent(catalog, registry, service, layerId, objectId, attachmentId, context, stores, cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/addAttachment", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureAddAttachment(catalog, registry, service, layerId, objectId, context, stores, adminToken, cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/deleteAttachments", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureDeleteAttachments(catalog, registry, service, layerId, objectId, context, stores, adminToken, cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/{objectId:long}/updateAttachment", (
            string service, int layerId, long objectId, HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureUpdateAttachment(catalog, registry, service, layerId, objectId, context, stores, adminToken, cancellationToken));
    }

    /// <summary>
    /// The service-level query (S1 query-feature-service/): the shared
    /// parameters apply to every queried layer, <c>layerDefs</c> narrows
    /// individual layers, and the response is one feature set, count, or id
    /// list per layer.
    /// </summary>
    private static async Task<IResult> FeatureServiceQuery(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        HttpContext context,
        IStoreRegistry stores,
        IGeometryOperations operations,
        ICoordinateTransforms transforms,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layers = await ListLayersAsync(stores, resolved, cancellationToken);
            var defs = Adapter.GeoServices.FeatureServiceQuery.ParseLayerDefs(parameters.Get("layerDefs"));
            var selected = SelectServiceLayers(layers, defs, service);
            var catalogue = stores.Catalogue(resolved.Store);
            var descriptions = new List<(PublishedLayer Layer, DatasetDescription Description)>(selected.Count);
            Spatial.Core.Geometry.CoordinateReference? fallback = null;
            foreach (var layer in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var description = await catalogue.DescribeAsync(layer.Dataset, cancellationToken);
                fallback ??= EsriLayerModel.LayerCoordinateReference(description.Srid);
                descriptions.Add((layer, description));
            }

            var shared = EsriFeatureQuery.Parse(parameters, fallback);
            Adapter.GeoServices.FeatureServiceQuery.RejectLayerOnlyShapes(shared);
            var targets = descriptions
                .Select(entry => new ServiceLayerQuery(
                    entry.Layer.Id,
                    entry.Description,
                    Adapter.GeoServices.FeatureServiceQuery.ForLayer(
                        shared, defs.GetValueOrDefault(entry.Layer.Id)),
                    EsriLayerModel.IsTable(entry.Description)))
                .ToArray();
            var store = stores.Features(resolved.Store);
            return await FeatureQueryEngine.ServiceQueryAsync(targets, store, shared, operations, transforms, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Selects the service-query layers: the <c>layerDefs</c> ids when
    /// present (unknown ids are <c>not.found</c>, never silently dropped),
    /// otherwise every layer and table in id order.
    /// </summary>
    private static IReadOnlyList<PublishedLayer> SelectServiceLayers(
        IReadOnlyList<PublishedLayer> layers, IReadOnlyDictionary<int, Adapter.GeoServices.FeatureServiceQuery.LayerDef> defs, string service)
    {
        if (defs.Count == 0)
        {
            return layers;
        }

        var known = layers.ToDictionary(layer => layer.Id);
        var selected = new List<PublishedLayer>(defs.Count);
        foreach (var id in defs.Keys.OrderBy(id => id))
        {
            if (!known.TryGetValue(id, out var layer))
            {
                throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {id} does not exist in service '{service}'.");
            }

            selected.Add(layer);
        }

        return selected;
    }

    /// <summary>
    /// The Feature Service per-layer <c>generateRenderer</c>: the same
    /// server-side classification the MapServer serves (T-039,
    /// ADR-0055), reused rather than duplicated. The feature write-model
    /// track (T-038) mounts this route; the implementation stays the single
    /// <see cref="MapGenerateRenderer"/> classifier.
    /// </summary>
    private static async Task<IResult> FeatureGenerateRenderer(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            var renderer = await MapGenerateRenderer.GenerateAsync(
                stores.Features(resolved.Store), description, parameters.Get("classificationDef"), parameters.Get("where"), cancellationToken);
            return EsriJson.Value(new EsriGenerateRendererResponse(renderer));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The layer-level <c>validateSQL</c> (S4): validates the <c>sql</c> WHERE clause, never runs it.</summary>
    private static async Task<IResult> FeatureValidateSql(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var description = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            return EsriJson.Value(Adapter.GeoServices.FeatureValidateSql.Validate(
                description, parameters.Get("sql"), parameters.Get("sqlType")));
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// An aggregation extension with no engine model (ADR-0058 §4): the
    /// layer must exist (unknown ids stay <c>not.found</c>), then the
    /// operation fails as typed <c>invalid.arguments</c> naming the served
    /// alternative.
    /// </summary>
    private static async Task<IResult> UnsupportedLayerOperation(
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        string service,
        int layerId,
        HttpContext context,
        IStoreRegistry stores,
        string operation,
        string guidance,
        CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            _ = await DescribeAsync(stores, resolved, layerId, cancellationToken);
            throw EsriInteropException.Invalid($"The '{operation}' operation is not supported on service '{service}': {guidance}");
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The layer-level <c>queryAttachments</c> (S4): one group per requested feature over the store's capability.</summary>
    private static async Task<IResult> FeatureQueryAttachments(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            var objectIds = FeatureAttachments.ParseIds(parameters.Get("objectIds"), "objectIds");
            return await FeatureAttachments.QueryAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store), objectIds, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The per-feature <c>attachments</c> resource: the stored attachment infos for one feature.</summary>
    private static async Task<IResult> FeatureAttachmentInfos(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            return await FeatureAttachments.InfosAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store), objectId, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The per-attachment content resource: the stored bytes with their content type.</summary>
    private static async Task<IResult> FeatureAttachmentContent(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId, long attachmentId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            return await FeatureAttachments.ContentAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store),
                objectId, attachmentId, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The per-feature <c>addAttachment</c>: an admin-gated multipart upload stored on the capability.</summary>
    private static async Task<IResult> FeatureAddAttachment(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, string? adminToken, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureAttachmentAuthorized(adminToken, context, parameters);
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            var upload = await ReadAttachmentUploadAsync(context, "addAttachment", parameters.Get("keywords"), cancellationToken);
            return await FeatureAttachments.AddAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store),
                objectId, upload, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The per-feature <c>deleteAttachments</c>: an admin-gated batch delete with per-id results.</summary>
    private static async Task<IResult> FeatureDeleteAttachments(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, string? adminToken, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureAttachmentAuthorized(adminToken, context, parameters);
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            var attachmentIds = FeatureAttachments.ParseIds(parameters.Get("attachmentIds"), "attachmentIds")
                ?? throw EsriInteropException.Invalid("The 'attachmentIds' parameter is required.");
            return await FeatureAttachments.DeleteAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store),
                objectId, attachmentIds, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>The per-feature <c>updateAttachment</c>: an admin-gated multipart replacement keeping the identity.</summary>
    private static async Task<IResult> FeatureUpdateAttachment(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, string? adminToken, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureAttachmentAuthorized(adminToken, context, parameters);
            var resolved = await ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapService.Feature, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            var attachmentId = ParseAttachmentId(parameters.Get("attachmentId"));
            var upload = await ReadAttachmentUploadAsync(context, "updateAttachment", parameters.Get("keywords"), cancellationToken);
            return await FeatureAttachments.UpdateAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store),
                objectId, attachmentId, upload, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>
    /// Resolves one layer's dataset for serving: the published layer and its
    /// catalogue description. An unknown layer id is <c>not.found</c>, never
    /// silently dropped.
    /// </summary>
    internal static async Task<ResolvedLayer> ResolveLayerAsync(
        IStoreRegistry stores, ResolvedService resolved, int layerId, CancellationToken cancellationToken)
    {
        var layers = await ListLayersAsync(stores, resolved, cancellationToken);
        var layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)
            ?? throw new EsriInteropException(EsriErrorCodes.NotFound, $"Layer {layerId} does not exist in the service.");
        var description = await stores.Catalogue(resolved.Store).DescribeAsync(layer.Dataset, cancellationToken);
        return new ResolvedLayer(layer, description);
    }

    private static long ParseAttachmentId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var attachmentId))
        {
            throw EsriInteropException.Invalid("The 'attachmentId' parameter is required and must be an integer.");
        }

        return attachmentId;
    }

    /// <summary>
    /// Gates an attachment write on the single admin token (ADR-0065 §3),
    /// exactly like the Esri admin projection gate: unconfigured means
    /// unavailable, a missing token is required, a wrong token is invalid.
    /// The token travels as a Bearer header or a <c>token</c> parameter.
    /// </summary>
    private static void EnsureAttachmentAuthorized(string? configuredToken, HttpContext context, EsriRequestParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            throw new EsriInteropException(
                EsriErrorCodes.ServiceUnavailable,
                "Attachment writes are not configured; set Spatial:Admin:Token or SPATIAL_ADMIN_TOKEN.");
        }

        var presented = PresentedAttachmentToken(context, parameters) ?? throw new EsriInteropException(
            EsriErrorCodes.TokenRequired, "An admin token is required to modify attachments.");
        if (!FixedTimeEquals(presented, configuredToken))
        {
            throw new EsriInteropException(EsriErrorCodes.InvalidToken, "The admin token is not valid.");
        }
    }

    private static string? PresentedAttachmentToken(HttpContext context, EsriRequestParameters parameters)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        return parameters.Get("token");
    }

    private static bool FixedTimeEquals(string left, string right) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(left), System.Text.Encoding.UTF8.GetBytes(right));

    /// <summary>
    /// Reads the multipart upload of an attachment write: the file part named
    /// <c>attachment</c> (the single file part when unnamed). Anything else
    /// is a typed <c>invalid.arguments</c> failure naming the expectation.
    /// </summary>
    private static async Task<AttachmentUpload> ReadAttachmentUploadAsync(
        HttpContext context, string operation, string? keywords, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            throw EsriInteropException.Invalid(
                $"The '{operation}' operation requires a multipart form upload with a file part named 'attachment'.");
        }

        // EsriRequestParameters already consumed the form fields; re-reading
        // the form reuses the parsed collection rather than the body stream.
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["attachment"] ?? (form.Files.Count == 1 ? form.Files[0] : null)
            ?? throw EsriInteropException.Invalid(
                $"The '{operation}' operation requires a file part named 'attachment'.");
        if (string.IsNullOrWhiteSpace(file.FileName))
        {
            throw EsriInteropException.Invalid($"The '{operation}' upload requires a file name.");
        }

        await using var source = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        return new AttachmentUpload(file.FileName, contentType, buffer.ToArray(), keywords);
    }
}

/// <summary>One layer resolved for attachment serving: the published layer and its catalogue description.</summary>
internal sealed record ResolvedLayer(PublishedLayer Layer, DatasetDescription Description);
