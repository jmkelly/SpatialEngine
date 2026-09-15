using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Spatial.Contracts;
using Spatial.Contracts.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Mounts the OGC WMS/WFS route group (ADR-0053 §3): <c>/{name}/wms</c> and
/// <c>/{name}/wfs</c> under the configured root, each accepting GET and a
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
                var method = Method(context);
                var path = Path(context);
                var operation = Operation(parameters);
                var values = Parameters(parameters);
                LogCompleted(logger, method, path, operation, values);
            }

            return result;
        }
        catch (Exception exception)
        {
            var failure = OgcErrorMapper.Describe(exception);
            if (logger.IsEnabled(LogLevel.Warning))
            {
                var method = Method(context);
                var path = Path(context);
                var operation = Operation(parameters);
                var values = Parameters(parameters);
                LogFailed(logger, method, path, operation, failure.Code, failure.Status, failure.Message, values);
            }

            return OgcErrorMapper.Map(exception);
        }
    }

    private static string Method(HttpContext context) => context.Request.Method;

    private static string Path(HttpContext context) => context.Request.Path.Value ?? "/";

    private static string Operation(OgcParameters? parameters) => parameters?.Get("request") ?? "-";

    // The merged parameters in request order; OGC requests carry no secrets, so
    // the adapter logs them verbatim to make a rejected GetMap reproducible.
    private static string Parameters(OgcParameters? parameters) =>
        parameters is null || parameters.Values.Count == 0
            ? "(none)"
            : string.Join('&', parameters.Values.Select(pair => $"{pair.Key}={pair.Value}"));

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "OGC {Method} {Path} request '{Operation}' completed; parameters: {Parameters}")]
    private static partial void LogCompleted(
        ILogger logger, string method, string path, string operation, string parameters);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "OGC {Method} {Path} request '{Operation}' failed with {ExceptionCode} (HTTP {StatusCode}): {Reason}; parameters: {Parameters}")]
    private static partial void LogFailed(
        ILogger logger, string method, string path, string operation,
        string exceptionCode, int statusCode, string reason, string parameters);
}
