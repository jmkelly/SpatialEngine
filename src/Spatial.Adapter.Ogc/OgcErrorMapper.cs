using System.Text;
using Microsoft.AspNetCore.Http;
using Spatial.PluginSdk;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Maps engine and adapter failures onto the OGC <c>ServiceExceptionReport</c>
/// envelope (ADR-0052 §3): engine <c>SpatialException</c> codes become the
/// matching OGC exception code and HTTP status, so an invalid request, an
/// unknown layer and an unavailable store are distinguishable on the wire.
/// </summary>
internal static class OgcErrorMapper
{
    public static IResult Map(Exception exception) => exception switch
    {
        OgcServiceException ogc => Report(ogc.Code, ogc.Message, ogc.Status),
        SpatialException spatial => MapSpatial(spatial),
        OperationCanceledException => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),
        _ => Report("NoApplicableCode", exception.Message, StatusCodes.Status500InternalServerError),
    };

    private static IResult MapSpatial(SpatialException exception) => exception.Code switch
    {
        SpatialException.InvalidArguments => Report("InvalidParameterValue", exception.Message, StatusCodes.Status400BadRequest),
        SpatialException.NotFound => Report("LayerNotDefined", exception.Message, StatusCodes.Status404NotFound),
        SpatialException.StoreUnavailable => Report("NoApplicableCode", exception.Message, StatusCodes.Status503ServiceUnavailable),
        _ => Report("NoApplicableCode", exception.Message, StatusCodes.Status500InternalServerError),
    };

    private static IResult Report(string code, string message, int status) =>
        Results.Text(OgcXml.ServiceExceptionReport(code, message), "application/xml", Encoding.UTF8, status);
}
