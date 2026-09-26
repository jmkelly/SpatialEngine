using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Mounts the OGC WMS/WFS and API Tiles route group (ADR-0053 §3, ADR-0070):
/// <c>/{name}/wms</c>, <c>/{name}/wfs</c> and <c>/{name}/tiles</c> under the
/// configured root; WMS/WFS accept GET and a
/// form POST. Every handler resolves the map, dispatches the operation and
/// maps any failure to the OGC <c>ServiceExceptionReport</c> envelope. One
/// structured event per request carries the operation and the request
/// parameters, and a failure is raised to <c>Warning</c> with the mapped OGC
/// code and reason, so a blank or rejected interop client (for example QGIS)
/// is diagnosable from the logs alone (ADR-0045).
/// </summary>
public static partial class OgcEndpoints
{
    /// <summary>Maps the OGC projection at <see cref="OgcOptions.Root"/>.</summary>
    public static void Map(IEndpointRouteBuilder app, OgcOptions options, IMapRegistry registry)
    {
        var group = app.MapGroup(options.Root);
        group.MapMethods("/{name}/wms", ["GET", "POST"], (
            string name,
            HttpContext context,
            IStoreRegistry stores,
            IMapRenderer renderer,
            ICoordinateTransforms transforms,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            Dispatch(context, loggerFactory, parameters => WmsService.HandleAsync(
                name, parameters, new OgcRequestServices(stores, registry, renderer, transforms), options, context, cancellationToken), cancellationToken));

        group.MapMethods("/{name}/wfs", ["GET", "POST"], (
            string name,
            HttpContext context,
            IStoreRegistry stores,
            IMapRenderer renderer,
            ICoordinateTransforms transforms,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
            Dispatch(context, loggerFactory, parameters => WfsService.HandleAsync(
                name, parameters, new OgcRequestServices(stores, registry, renderer, transforms), options, context, cancellationToken), cancellationToken));

        OgcApiTiles.Map(group, options);
    }

    /// <summary>Reads, dispatches and reports one OGC operation, logging the outcome.</summary>
    internal static async Task<IResult> Dispatch(
        HttpContext context, ILoggerFactory loggerFactory, Func<OgcParameters, Task<IResult>> handle, CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger(typeof(OgcEndpoints));
        OgcParameters? parameters = null;
        try
        {
            parameters = await OgcParameters.ReadAsync(context, cancellationToken);
            var result = await handle(parameters);
            if (logger.IsEnabled(LogLevel.Information))
            {
                var request = OgcRequestLog.Describe(context, parameters);
                LogCompleted(logger, request);
            }

            return result;
        }
        catch (Exception exception)
        {
            var failure = OgcErrorMapper.Describe(exception);
            if (logger.IsEnabled(LogLevel.Warning))
            {
                var request = OgcRequestLog.Describe(context, parameters);
                LogFailed(logger, request, failure.Code, failure.Status, failure.Message);
            }

            return OgcErrorMapper.Map(exception);
        }
    }

    /// <summary>One OGC request as the structured log records it: what was asked, and where.</summary>
    private static class OgcRequestLog
    {
        /// <summary>
        /// The one descriptor both request events carry: the method and path,
        /// the OGC operation, and the merged parameters in request order. OGC
        /// requests carry no secrets, so the adapter logs the parameters
        /// verbatim to make a rejected GetMap reproducible.
        /// </summary>
        public static string Describe(HttpContext context, OgcParameters? parameters) =>
            $"{context.Request.Method} {context.Request.Path.Value ?? "/"} request '{parameters?.Get("request") ?? "-"}'; "
            + $"parameters: {Merged(parameters)}";

        // The merged parameters in request order.
        private static string Merged(OgcParameters? parameters) =>
            parameters is null || parameters.Values.Count == 0
                ? "(none)"
                : string.Join('&', parameters.Values.Select(pair => $"{pair.Key}={pair.Value}"));
    }

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "OGC {Request} completed")]
    private static partial void LogCompleted(ILogger logger, string request);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "OGC {Request} failed with {ExceptionCode} (HTTP {StatusCode}): {Reason}")]
    private static partial void LogFailed(
        ILogger logger, string request, string exceptionCode, int statusCode, string reason);
}
