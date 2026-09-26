using Microsoft.Extensions.Logging;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The MapServer <c>export</c> and ImageServer <c>exportImage</c> diagnostic
/// seam (ADR-0045): one structured event per request carrying the service and
/// the request values, so a blank parity panel is diagnosable from the log
/// alone. A completed export logs at <c>Information</c> with the rendered
/// size; a failure rises to <c>Warning</c> with the mapped Esri code, HTTP
/// status and reason. Follows the OGC <c>Dispatch</c> seam.
/// </summary>
internal static partial class GeoServicesExportLogging
{
    /// <summary>
    /// The merged parameters in request order for the log. Export parameters
    /// carry no secrets, so the adapter logs them verbatim to make a rejected
    /// export reproducible.
    /// </summary>
    internal static string FormatParameters(EsriRequestParameters? parameters) =>
        parameters is null || parameters.Values.Count == 0
            ? "(none)"
            : string.Join('&', parameters.Values.Select(pair => $"{pair.Key}={pair.Value}"));

    [LoggerMessage(
        EventId = 10,
        Level = LogLevel.Information,
        Message = "MapServer export {Method} {Path} service '{Service}' completed ({Width}x{Height}, {Bytes} bytes, {MediaType}); parameters: {Parameters}")]
    internal static partial void LogMapExportCompleted(
        ILogger logger, string method, string path, string service,
        int width, int height, long bytes, string mediaType, string parameters);

    [LoggerMessage(
        EventId = 11,
        Level = LogLevel.Warning,
        Message = "MapServer export {Method} {Path} service '{Service}' failed with {EsriCode} (HTTP {StatusCode}): {Reason}; parameters: {Parameters}")]
    internal static partial void LogMapExportFailed(
        ILogger logger, Exception exception, string method, string path, string service,
        int esriCode, int statusCode, string reason, string parameters);

    [LoggerMessage(
        EventId = 12,
        Level = LogLevel.Information,
        Message = "ImageServer exportImage {Method} {Path} service '{Service}' completed; parameters: {Parameters}")]
    internal static partial void LogImageExportCompleted(
        ILogger logger, string method, string path, string service, string parameters);

    [LoggerMessage(
        EventId = 13,
        Level = LogLevel.Warning,
        Message = "ImageServer exportImage {Method} {Path} service '{Service}' failed with {EsriCode} (HTTP {StatusCode}): {Reason}; parameters: {Parameters}")]
    internal static partial void LogImageExportFailed(
        ILogger logger, Exception exception, string method, string path, string service,
        int esriCode, int statusCode, string reason, string parameters);
}
