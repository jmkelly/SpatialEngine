using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

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
    internal static async Task<IResult> FeatureQueryAttachments(AttachmentRequest request)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            var scope = await AttachmentScope.OpenAsync(request, request.LayerId);
            var objectIds = FeatureAttachmentTargets.ParseIds(parameters.Get("objectIds"), "objectIds");
            return await FeatureAttachments.QueryAsync(
                scope.Description,
                request.Stores.Features(scope.Store),
                request.Stores.AttachmentStore(scope.Store),
                objectIds,
                request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureAttachmentInfos(AttachmentRequest request, long objectId)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            return await FeatureAttachments.InfosAsync(
                await AttachmentScope.TargetAsync(request, request.LayerId, objectId), request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureAttachmentContent(AttachmentRequest request, long objectId, long attachmentId)
    {
        try
        {
            return await FeatureAttachments.ContentAsync(
                await AttachmentScope.TargetAsync(request, request.LayerId, objectId), attachmentId, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureAddAttachment(AttachmentRequest request, long objectId, AttachmentWriteAuthorization authorization)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            await AttachmentWriteGuard.EnsureAuthorized(authorization, request.Context, parameters, request.CancellationToken);
            var upload = await AttachmentWriteGuard.ReadUploadAsync(request.Context, "addAttachment", parameters.Get("keywords"), request.CancellationToken);
            return await FeatureAttachmentWrites.AddAsync(
                await AttachmentScope.TargetAsync(request, request.LayerId, objectId), upload, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureDeleteAttachments(AttachmentRequest request, long objectId, AttachmentWriteAuthorization authorization)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            await AttachmentWriteGuard.EnsureAuthorized(authorization, request.Context, parameters, request.CancellationToken);
            var attachmentIds = FeatureAttachmentTargets.ParseIds(parameters.Get("attachmentIds"), "attachmentIds")
                ?? throw GeoServicesErrors.Invalid("The 'attachmentIds' parameter is required.");
            return await FeatureAttachmentWrites.DeleteAsync(
                await AttachmentScope.TargetAsync(request, request.LayerId, objectId), attachmentIds, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    internal static async Task<IResult> FeatureUpdateAttachment(AttachmentRequest request, long objectId, AttachmentWriteAuthorization authorization)
    {
        try
        {
            var parameters = await LayerQuery.ReadParameters(request);
            await AttachmentWriteGuard.EnsureAuthorized(authorization, request.Context, parameters, request.CancellationToken);
            var attachmentId = AttachmentWriteGuard.ParseAttachmentId(parameters.Get("attachmentId"));
            var upload = await AttachmentWriteGuard.ReadUploadAsync(request.Context, "updateAttachment", parameters.Get("keywords"), request.CancellationToken);
            return await FeatureAttachmentWrites.UpdateAsync(
                await AttachmentScope.TargetAsync(request, request.LayerId, objectId), attachmentId, upload, request.CancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }
}

/// <summary>
/// The published service and layer an attachment resource addresses, and the
/// feature an attachment resource addresses on it. Naming the resolution once
/// keeps the attachment handlers to mapping and stops each of them repeating
/// the resolve-service, resolve-layer, build-target preamble.
/// </summary>
internal static class AttachmentScope
{
    /// <summary>Resolves the published service and describes the addressed layer.</summary>
    public static Task<LayerQuery.LayerScope> OpenAsync(AttachmentRequest request, int layerId) =>
        LayerQuery.OpenAsync(request, layerId);

    /// <summary>
    /// The feature an attachment resource addresses, from the resolved
    /// service and layer.
    /// </summary>
    public static async Task<FeatureAttachmentTargets.AttachmentTarget> TargetAsync(
        AttachmentRequest request, int layerId, long objectId)
    {
        var scope = await OpenAsync(request, layerId);
        return new FeatureAttachmentTargets.AttachmentTarget(
            scope.Description,
            request.Stores.Features(scope.Store),
            request.Stores.AttachmentStore(scope.Store),
            objectId);
    }
}

/// <summary>
/// The single admin gate an attachment write travels through (ADR-0065 §3):
/// the host's configured admin token, the auth service and the legacy
/// migration token, or the typed failure each mismatch produces. Split out of
/// <see cref="FeatureAttachmentHandlers"/> so the route handlers stay routing
/// and the auth/multipart fan-out lives with the code that uses it.
/// </summary>
internal static class AttachmentWriteGuard
{
    /// <summary>
    /// How attachment writes are authorized: the local auth service when the
    /// host has auth enabled, otherwise the single configured admin token.
    /// </summary>
    public static async Task EnsureAuthorized(
        AttachmentWriteAuthorization authorization,
        HttpContext context,
        EsriRequestParameters parameters,
        CancellationToken cancellationToken)
    {
        var presented = PresentedToken(context, parameters);
        if (authorization.AuthEnabled)
        {
            await RequireAdminRoleAsync(authorization, presented, cancellationToken);
            return;
        }

        EnsureAdminToken(presented, authorization.ConfiguredToken);
    }

    private static async Task RequireAdminRoleAsync(
        AttachmentWriteAuthorization authorization, string? presented, CancellationToken cancellationToken)
    {
        try
        {
            await AuthGuard.RequireRoleAsync(
                authorization.Auth!, presented, authorization.LegacyToken, AuthGuard.AdminRole, cancellationToken);
        }
        catch (SpatialException exception) when (
            exception.Code is SpatialException.AuthUnauthorized or SpatialException.AuthFailed or SpatialException.AuthForbidden)
        {
            throw AuthFailure(exception);
        }
    }

    /// <summary>Relabels an auth failure as the Esri attachment-write error; other codes keep their own code.</summary>
    private static Exception AuthFailure(SpatialException exception) => exception.Code switch
    {
        SpatialException.AuthUnauthorized => GeoServicesErrors.TokenRequired("An admin token is required to modify attachments."),
        SpatialException.AuthFailed or SpatialException.AuthForbidden => GeoServicesErrors.InvalidToken(exception.Message),
        _ => exception,
    };

    private static void EnsureAdminToken(string? presented, string? configuredToken)
    {
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            throw GeoServicesErrors.ServiceUnavailable(
                "Attachment writes are not configured; set Spatial:Admin:Token or SPATIAL_ADMIN_TOKEN.");
        }

        var token = presented ?? throw GeoServicesErrors.TokenRequired("An admin token is required to modify attachments.");
        if (!FixedTimeEquals(token, configuredToken))
        {
            throw GeoServicesErrors.InvalidToken("The admin token is not valid.");
        }
    }

    private static string? PresentedToken(HttpContext context, EsriRequestParameters parameters)
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

    /// <summary>Reads the <c>attachmentId</c> parameter of the single-attachment operations.</summary>
    public static long ParseAttachmentId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !long.TryParse(value.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var attachmentId))
        {
            throw GeoServicesErrors.Invalid("The 'attachmentId' parameter is required and must be an integer.");
        }

        return attachmentId;
    }

    /// <summary>Reads the multipart form upload of an attachment write into a bounded in-memory blob.</summary>
    public static async Task<FeatureAttachmentWrite> ReadUploadAsync(
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
        return new FeatureAttachmentWrite(file.FileName, contentType, buffer.ToArray(), keywords);
    }
}

/// <summary>
/// How attachment writes are authorized: the local auth service when the
/// host has auth enabled, otherwise the single configured admin token.
/// </summary>
internal sealed record AttachmentWriteAuthorization(
    string? ConfiguredToken, IAuthService? Auth, bool AuthEnabled, string? LegacyToken);
