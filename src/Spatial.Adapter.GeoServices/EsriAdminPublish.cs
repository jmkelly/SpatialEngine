using Microsoft.AspNetCore.Http;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Ingest.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri <c>/uploads/{id}/publish</c> projection (ADR-0041 §5): a staged
/// upload is merged into the named map like the neutral <c>?publish=</c> path —
/// the dataset is appended as a new layer (or kept once when already mapped)
/// and the Feature service is unioned in, so publishing over a
/// multi-layer/multi-service map never clobbers it.
/// </summary>
internal static class EsriAdminPublish
{
    /// <summary>Publishes one staged upload into the named map.</summary>
    public static async Task<IResult> PublishAsync(
        IMapRegistry registry, EsriUploadStaging staging, string id, HttpContext context, CancellationToken token)
    {
        var staged = staging.Take(id) ?? throw GeoServicesErrors.Invalid($"The staged upload '{id}' does not exist or has expired.");
        var form = await context.Request.ReadFormAsync(token);
        var rawName = EsriAdminRequest.Form(form, "name", context)
            ?? EsriAdminRequest.Query(context.Request.Query, "name")
            ?? throw GeoServicesErrors.Invalid("A 'name' is required to publish the upload.");
        var name = Uri.UnescapeDataString(rawName);

        Map? existing = null;
        try
        {
            existing = await registry.GetAsync(name, token);
        }
        catch (SpatialException exception) when (exception.Code == SpatialException.NotFound)
        {
        }

        var layers = existing?.Layers.ToList() ?? [];
        if (layers.TrueForAll(layer => !string.Equals(layer.Dataset, staged.Outcome.Dataset, StringComparison.Ordinal)))
        {
            var next = layers.Count == 0 ? 0 : layers.Max(layer => layer.LayerId) + 1;
            layers.Add(new MapLayer(staged.Outcome.Dataset, next));
        }

        var map = existing is null
            ? new Map(name, staged.Store, layers, [MapServiceKind.FeatureServer])
            : existing with { Store = staged.Store, Layers = layers, Services = [.. existing.Services.Union([MapServiceKind.FeatureServer])] };
        var stored = await registry.PutAsync(map, token);
        return EsriAdminResponses.Success(stored.Name, stored.Layers.Select(layer => layer.LayerId).ToArray());
    }



}
