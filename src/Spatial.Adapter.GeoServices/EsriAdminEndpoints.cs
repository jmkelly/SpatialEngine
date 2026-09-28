using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;
using Spatial.Ingest.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri admin projection (ADR-0041 §5): a thin, token-gated mapping of the
/// ArcGIS REST admin surface onto the neutral map registry and ingest
/// face. It is a projection, not the model — service creation goes
/// through <see cref="IMapRegistry.PutAsync"/> and uploads through
/// <see cref="IDatasetIngest"/>. Data-store registration, definitions,
/// portal items and everything the v1.0 specification does not describe are
/// rejected. Uploads are staged in memory with a TTL and the configured byte
/// cap before being published.
/// </summary>
public static class EsriAdminEndpoints
{
    /// <summary>Maps the admin projection at <see cref="EsriAdminOptions.Root"/>.</summary>
    public static void Map(
        IEndpointRouteBuilder app,
        EsriAdminOptions options,
        IMapRegistry registry,
        IAuthService auth)
    {
        var staging = new EsriUploadStaging(options.UploadTtl);
        var group = app.MapGroup(options.Root);

        group.MapMethods("/services", ["GET", "POST"], (HttpContext context, CancellationToken token) =>
            Handle(options, context, auth, () => EsriAdminServices.ListAsync(registry, token)));
        group.MapMethods("/services/{service}", ["GET", "POST"], (string service, HttpContext context, CancellationToken token) =>
            Handle(options, context, auth, () => EsriAdminServices.GetAsync(registry, service, token)));
        group.MapPost("/services/{service}/createService", (string service, HttpContext context, CancellationToken token) =>
            Handle(options, context, auth, () => EsriAdminServices.CreateAsync(registry, service, context, token)));
        group.MapPost("/services/{service}/deleteService", (string service, HttpContext context, CancellationToken token) =>
            Handle(options, context, auth, () => EsriAdminServices.DeleteAsync(registry, service, token)));
        group.MapPost("/uploads", (HttpContext context, IStoreRegistry stores, ICoordinateTransforms transforms, CancellationToken token) =>
            Handle(options, context, auth, () => EsriAdminUploads.UploadAsync(options, staging, context, stores, transforms, token)));
        group.MapPost("/uploads/{id}/publish", (string id, HttpContext context, CancellationToken token) =>
            Handle(options, context, auth, () => EsriAdminPublish.PublishAsync(registry, staging, id, context, token)));
    }

    private static async Task<IResult> Handle(
        EsriAdminOptions options,
        HttpContext context,
        IAuthService auth,
        Func<Task<IResult>> action)
    {
        try
        {
            await EnsureAuthorized(options, context, auth);
            return await action();
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    /// <summary>Validates the local bearer or the migration static token.</summary>
    private static async Task EnsureAuthorized(
        EsriAdminOptions options,
        HttpContext context,
        IAuthService auth)
    {
        if (!options.Enabled)
        {
            throw GeoServicesErrors.ServiceUnavailable(
                "The Esri admin projection is not configured; set Spatial:Auth:Users or Spatial:Admin:Token.");
        }

        try
        {
            await AuthGuard.RequireRoleAsync(
                auth,
                PresentedToken(context),
                options.Token,
                AuthGuard.AdminRole,
                context.RequestAborted);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.AuthUnauthorized)
        {
            throw GeoServicesErrors.TokenRequired("An admin token is required.");
        }
        catch (SpatialException exception) when (
            exception.Code is SpatialException.AuthFailed or SpatialException.AuthForbidden)
        {
            throw GeoServicesErrors.InvalidToken(exception.Message);
        }
    }

    private static string? PresentedToken(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var token = context.Request.Query["token"].ToString();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }


}

/// <summary>In-memory staging of uploaded, not-yet-published datasets with a TTL (ADR-0041 §6).</summary>
internal sealed class EsriUploadStaging(TimeSpan ttl)
{
    private readonly ConcurrentDictionary<string, StagedUpload> _items = new(StringComparer.Ordinal);

    /// <summary>Stages an ingest outcome and returns its item id.</summary>
    public string Stage(IngestOutcome outcome, string store)
    {
        Prune();
        var id = Guid.NewGuid().ToString("N");
        _items[id] = new StagedUpload(outcome, store, DateTimeOffset.UtcNow);
        return id;
    }

    /// <summary>Removes and returns a staged upload, or null when missing/expired.</summary>
    public StagedUpload? Take(string id)
    {
        Prune();
        return _items.TryRemove(id, out var staged) ? staged : null;
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - ttl;
        foreach (var (key, value) in _items)
        {
            if (value.CreatedAt < cutoff)
            {
                _items.TryRemove(key, out _);
            }
        }
    }
}

/// <summary>One staged upload: the ingest outcome and the target store.</summary>
internal sealed record StagedUpload(IngestOutcome Outcome, string Store, DateTimeOffset CreatedAt);
