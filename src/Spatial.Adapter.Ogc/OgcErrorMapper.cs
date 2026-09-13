using System.Text;
using Microsoft.AspNetCore.Http;
using Spatial.PluginSdk;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Maps engine and adapter failures onto the OGC <c>ServiceExceptionReport</c>
/// envelope (ADR-0052 §3): engine <c>SpatialException</c> codes become the
/// matching OGC exception code and HTTP status, so an invalid request, an
/// unknown layer and an unavailable store are distinguishable on the wire.
/// The exception kinds and engine codes dispatch through small helpers
/// (ADR-0040).
/// </summary>
internal static class OgcErrorMapper
{
    private static readonly Dictionary<string, Func<string, IResult>> SpatialReports =
        new(StringComparer.Ordinal)
        {
            [SpatialException.InvalidArguments] = message =>
                Report("InvalidParameterValue", message, StatusCodes.Status400BadRequest),
            [SpatialException.NotFound] = message =>
                Report("LayerNotDefined", message, StatusCodes.Status404NotFound),
            [SpatialException.StoreUnavailable] = message =>
                Report("NoApplicableCode", message, StatusCodes.Status503ServiceUnavailable),
        };

    public static IResult Map(Exception exception)
    {
        if (exception is OgcServiceException ogc)
        {
            return Report(ogc.Code, ogc.Message, ogc.Status);
        }

        return exception is SpatialException spatial ? MapSpatial(spatial) : Fallback(exception);
    }

    /// <summary>Maps one engine <see cref="SpatialException"/> by its stable code.</summary>
    internal static IResult MapSpatial(SpatialException exception) =>
        SpatialReports.TryGetValue(exception.Code, out var report)
            ? report(exception.Message)
            : Report("NoApplicableCode", exception.Message, StatusCodes.Status500InternalServerError);

    private static IResult Fallback(Exception exception) =>
        exception is OperationCanceledException
            ? Results.StatusCode(StatusCodes.Status499ClientClosedRequest)
            : Report("NoApplicableCode", exception.Message, StatusCodes.Status500InternalServerError);

    private static IResult Report(string code, string message, int status) =>
        Results.Text(OgcXml.ServiceExceptionReport(code, message), "application/xml", Encoding.UTF8, status);
}
