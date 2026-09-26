using Microsoft.AspNetCore.Http;
using Spatial.Contracts.Providers;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The Esri admin <c>/services</c> projection (ADR-0041 §5): listing the
/// FeatureServer services a registry map exposes, describing one, and the
/// create/delete operations that go through <see cref="IMapRegistry"/>. A
/// service is named <c>&lt;name&gt;.&lt;type&gt;</c> and only FeatureServer is
/// exposed. Split out of <see cref="EsriAdminEndpoints"/>, which owns the
/// routes and the token gate.
/// </summary>
internal static class EsriAdminServices
{
    public static async Task<IResult> ListAsync(IMapRegistry registry, CancellationToken token)
    {
        return EsriJson.Value(EsriAdminResponses.List(await registry.ListAsync(token)));
    }

    public static async Task<IResult> GetAsync(IMapRegistry registry, string service, CancellationToken token)
    {
        var name = ServiceName(service);
        var map = await registry.GetAsync(name, token);
        if (!map.Exposes(MapServiceKind.FeatureServer))
        {
            throw GeoServicesErrors.Invalid($"Map '{name}' does not expose a FeatureServer.");
        }

        return EsriJson.Value(EsriAdminResponses.Service(name, map));
    }

    public static async Task<IResult> CreateAsync(
        IMapRegistry registry, string service, HttpContext context, CancellationToken token)
    {
        var name = ServiceName(service);
        var form = await context.Request.ReadFormAsync(token);
        var store = EsriAdminRequest.Form(form, "store", context) ?? throw GeoServicesErrors.Invalid("A 'store' is required.");
        var dataset = EsriAdminRequest.Form(form, "dataset", context) ?? throw GeoServicesErrors.Invalid("A 'dataset' is required.");
        var map = new Map(name, store, [new MapLayer(dataset, -1)], [MapServiceKind.FeatureServer]);
        var stored = await registry.PutAsync(map, token);
        return EsriAdminResponses.Success(name, stored.Layers.Select(layer => layer.LayerId).ToArray());
    }

    public static async Task<IResult> DeleteAsync(IMapRegistry registry, string service, CancellationToken token)
    {
        var name = ServiceName(service);
        return EsriAdminResponses.Result(await registry.DeleteAsync(name, token), name, []);
    }

    private static string ServiceName(string service)
    {
        var dot = service.IndexOf('.');
        if (dot <= 0 || dot == service.Length - 1)
        {
            throw GeoServicesErrors.Invalid($"Service '{service}' must be named as '<name>.<type>'.");
        }

        var name = service[..dot];
        var type = service[(dot + 1)..];
        if (!string.Equals(type, "FeatureServer", StringComparison.OrdinalIgnoreCase))
        {
            throw GeoServicesErrors.Invalid($"Service type '{type}' is not supported; only FeatureServer is exposed.");
        }

        return name;
    }
}
