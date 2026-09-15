using Spatial.Esri.Codec;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature attachment endpoint handlers (T-061, ADR-0066): reads follow
/// feature-query auth (public), writes require the single admin token
/// (ADR-0065 §3). Split out of <see cref="GeoServicesEndpoints"/> so the
/// route facade keeps only mapping and the attachment fan-out (capability,
/// auth, multipart upload) lives with the code that uses it (ADR-0040).
/// </summary>
internal static class FeatureAttachmentHandlers
{
    internal static async Task<IResult> FeatureQueryAttachments(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
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

    internal static async Task<IResult> FeatureAttachmentInfos(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            return await FeatureAttachments.InfosAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store), objectId, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureAttachmentContent(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId, long attachmentId,
        HttpContext context, IStoreRegistry stores, CancellationToken cancellationToken)
    {
        try
        {
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
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

    internal static async Task<IResult> FeatureAddAttachment(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, string? adminToken, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureAttachmentAuthorized(adminToken, context, parameters);
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
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

    internal static async Task<IResult> FeatureDeleteAttachments(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, string? adminToken, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureAttachmentAuthorized(adminToken, context, parameters);
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
            var layer = await ResolveLayerAsync(stores, resolved, layerId, cancellationToken);
            var attachmentIds = FeatureAttachments.ParseIds(parameters.Get("attachmentIds"), "attachmentIds")
                ?? throw GeoServicesErrors.Invalid("The 'attachmentIds' parameter is required.");
            return await FeatureAttachments.DeleteAsync(
                layer.Description, stores.Features(resolved.Store), stores.AttachmentStore(resolved.Store),
                objectId, attachmentIds, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureUpdateAttachment(
        GeoServicesCatalog catalog, IMapRegistry registry, string service, int layerId, long objectId,
        HttpContext context, IStoreRegistry stores, string? adminToken, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            EnsureAttachmentAuthorized(adminToken, context, parameters);
            var resolved = await GeoServicesResolution.ResolveServiceAsync(catalog, registry, service, "FeatureServer", MapServiceKind.FeatureServer, cancellationToken);
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

    internal static async Task<ResolvedLayer> ResolveLayerAsync(
        IStoreRegistry stores, ResolvedService resolved, int layerId, CancellationToken cancellationToken)
    {
        var layers = await GeoServicesResolution.ListLayersAsync(stores, resolved, cancellationToken);
        var layer = layers.FirstOrDefault(candidate => candidate.Id == layerId)
            ?? throw GeoServicesErrors.NotFound($"Layer {layerId} does not exist in the service.");
        var description = await stores.Catalogue(resolved.Store).DescribeAsync(layer.Dataset, cancellationToken);
        return new ResolvedLayer(layer, description);
    }

    private static long ParseAttachmentId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var attachmentId))
        {
            throw GeoServicesErrors.Invalid("The 'attachmentId' parameter is required and must be an integer.");
        }

        return attachmentId;
    }

    private static void EnsureAttachmentAuthorized(string? configuredToken, HttpContext context, EsriRequestParameters parameters)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            throw GeoServicesErrors.ServiceUnavailable(
                "Attachment writes are not configured; set Spatial:Admin:Token or SPATIAL_ADMIN_TOKEN.");
        }

        var presented = PresentedAttachmentToken(context, parameters) ?? throw GeoServicesErrors.TokenRequired("An admin token is required to modify attachments.");
        if (!FixedTimeEquals(presented, configuredToken))
        {
            throw GeoServicesErrors.InvalidToken("The admin token is not valid.");
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

    private static async Task<AttachmentUpload> ReadAttachmentUploadAsync(
        HttpContext context, string operation, string? keywords, CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            throw GeoServicesErrors.Invalid(
                $"The '{operation}' operation requires a multipart form upload with a file part named 'attachment'.");
        }

        // EsriRequestParameters already consumed the form fields; re-reading
        // the form reuses the parsed collection rather than the body stream.
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["attachment"] ?? (form.Files.Count == 1 ? form.Files[0] : null)
            ?? throw GeoServicesErrors.Invalid(
                $"The '{operation}' operation requires a file part named 'attachment'.");
        if (string.IsNullOrWhiteSpace(file.FileName))
        {
            throw GeoServicesErrors.Invalid($"The '{operation}' upload requires a file name.");
        }

        await using var source = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, cancellationToken);
        var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;
        return new AttachmentUpload(file.FileName, contentType, buffer.ToArray(), keywords);
    }

    /// <summary>One layer resolved for attachment serving: the published layer and its catalogue description.</summary>
    internal sealed record ResolvedLayer(PublishedLayer Layer, DatasetDescription Description);
}
