using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Feature Server editing routes (spec §9.1.6–§9.1.9, ADR-0037):
/// POST-only <c>addFeatures</c>, <c>updateFeatures</c>, <c>deleteFeatures</c>
/// and <c>applyEdits</c>. Every edit is gated on the single admin token
/// (ADR-0065 §3), and each request carries its operation so one handler serves
/// all four routes; the request itself is applied by
/// <see cref="FeatureEditRequests"/>.
/// </summary>
internal static class FeatureEditEndpoints
{
    public static void MapFeatureEdit(
        RouteGroupBuilder group,
        GeoServicesCatalog catalog,
        IMapRegistry registry,
        IAuthService? auth,
        bool authEnabled,
        string? legacyToken)
    {
        group.MapPost("/{service}/FeatureServer/{layerId:int}/addFeatures", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Add, auth, authEnabled, legacyToken), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/updateFeatures", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Update, auth, authEnabled, legacyToken), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/deleteFeatures", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Delete, auth, authEnabled, legacyToken), cancellationToken));
        group.MapPost("/{service}/FeatureServer/{layerId:int}/applyEdits", (
            HttpContext context, string service, int layerId, IStoreRegistry stores, CancellationToken cancellationToken) =>
            FeatureEdit(new FeatureEditContext(catalog, registry, context, service, layerId, stores, EsriEditOperation.Apply, auth, authEnabled, legacyToken), cancellationToken));
    }

    /// <summary>
    /// One edit request: negotiate <c>f=json</c>, gate on the admin token
    /// (ADR-0065 §3), then apply the operation over the layer.
    /// </summary>
    private static async Task<IResult> FeatureEdit(FeatureEditContext request, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await EsriRequestParameters.ReadAsync(request.Context, cancellationToken);
            EsriFormat.Ensure(parameters.Get("f"));
            if (request.AuthEnabled)
            {
                await AuthGuard.RequireRoleAsync(
                    request.Auth!,
                    Bearer(request.Context),
                    request.LegacyToken,
                    AuthGuard.AdminRole,
                    cancellationToken);
            }

            return await FeatureEditRequests.ApplyAsync(request, parameters, cancellationToken);
        }
        catch (Exception exception)
        {
            return EsriErrorMapper.Map(exception);
        }
    }

    private static string? Bearer(HttpContext context)
    {
        var header = context.Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}

/// <summary>The resolved services of one Feature Service editing request.</summary>
internal sealed record FeatureEditContext(
    GeoServicesCatalog Catalog,
    IMapRegistry Registry,
    HttpContext Context,
    string Service,
    int LayerId,
    IStoreRegistry Stores,
    EsriEditOperation Operation,
    IAuthService? Auth,
    bool AuthEnabled,
    string? LegacyToken);
