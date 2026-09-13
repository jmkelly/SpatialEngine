using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Mounts the OGC WMS/WFS route group (ADR-0053 §3): <c>/{name}/wms</c> and
/// <c>/{name}/wfs</c> under the configured root, each accepting GET and a
/// form POST. Every handler resolves the map, dispatches the operation and
/// maps any failure to the OGC <c>ServiceExceptionReport</c> envelope.
/// </summary>
public static class OgcEndpoints
{
    /// <summary>Maps the OGC projection at <see cref="OgcOptions.Root"/>.</summary>
    public static void Map(IEndpointRouteBuilder app, OgcOptions options, IMapRegistry registry)
    {
        var group = app.MapGroup(options.Root);
        group.MapMethods("/{name}/wms", ["GET", "POST"], (
            string name,
            HttpContext context,
            IServiceProvider services,
            IMapRenderer renderer,
            ICoordinateTransforms transforms,
            CancellationToken cancellationToken) =>
            Dispatch(context, parameters => WmsService.HandleAsync(
                name, parameters, new OgcRequestServices(services, registry, renderer, transforms), options, context, cancellationToken), cancellationToken));

        group.MapMethods("/{name}/wfs", ["GET", "POST"], (
            string name,
            HttpContext context,
            IServiceProvider services,
            IMapRenderer renderer,
            ICoordinateTransforms transforms,
            CancellationToken cancellationToken) =>
            Dispatch(context, parameters => WfsService.HandleAsync(
                name, parameters, new OgcRequestServices(services, registry, renderer, transforms), options, context, cancellationToken), cancellationToken));
    }

    private static async Task<IResult> Dispatch(
        HttpContext context, Func<OgcParameters, Task<IResult>> handle, CancellationToken cancellationToken)
    {
        try
        {
            var parameters = await OgcParameters.ReadAsync(context, cancellationToken);
            return await handle(parameters);
        }
        catch (Exception exception)
        {
            return OgcErrorMapper.Map(exception);
        }
    }
}
