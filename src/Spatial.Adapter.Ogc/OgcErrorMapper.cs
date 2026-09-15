using System.Text;
using Microsoft.AspNetCore.Http;
using Spatial.PluginSdk;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Maps engine and adapter failures onto the OGC <c>ServiceExceptionReport</c>
/// envelope (ADR-0053 §3): engine <c>SpatialException</c> codes become the
/// matching OGC exception code and HTTP status, so an invalid request, an
/// unknown layer and an unavailable store are distinguishable on the wire.
/// The exception kinds and engine codes dispatch through small helpers
/// (ADR-0040). <see cref="Describe"/> exposes the same decision as a value so
/// the failure log and the report never disagree (ADR-0045).
/// </summary>
internal static class OgcErrorMapper
{
    private static readonly Dictionary<string, Func<string, OgcFailure>> SpatialReports =
        new(StringComparer.Ordinal)
        {
            [SpatialException.InvalidArguments] = message =>
                new OgcFailure("InvalidParameterValue", message, StatusCodes.Status400BadRequest),
            [SpatialException.NotFound] = message =>
                new OgcFailure("LayerNotDefined", message, StatusCodes.Status404NotFound),
            [SpatialException.StoreUnavailable] = message =>
                new OgcFailure("NoApplicableCode", message, StatusCodes.Status503ServiceUnavailable),
        };

    public static IResult Map(Exception exception) =>
        exception is OperationCanceledException
            ? Results.StatusCode(StatusCodes.Status499ClientClosedRequest)
            : Report(Describe(exception));

    /// <summary>The OGC code, reason and HTTP status a failure maps to.</summary>
    internal static OgcFailure Describe(Exception exception) => exception is OgcServiceException ogc
        ? ForOgc(ogc)
        : DescribeNonOgc(exception);

    private static OgcFailure DescribeNonOgc(Exception exception) => exception is SpatialException spatial
        ? DescribeSpatial(spatial)
        : DescribeGeneric(exception);

    private static OgcFailure DescribeGeneric(Exception exception) => exception is OperationCanceledException
        ? new OgcFailure("ClientClosedRequest", exception.Message, StatusCodes.Status499ClientClosedRequest)
        : new OgcFailure("NoApplicableCode", exception.Message, StatusCodes.Status500InternalServerError);

    private static OgcFailure ForOgc(OgcServiceException ogc) => new(ogc.Code, ogc.Message, ogc.Status);

    /// <summary>Maps one engine <see cref="SpatialException"/> by its stable code.</summary>
    internal static IResult MapSpatial(SpatialException exception) => Report(DescribeSpatial(exception));

    private static OgcFailure DescribeSpatial(SpatialException exception) =>
        SpatialReports.TryGetValue(exception.Code, out var describe)
            ? describe(exception.Message)
            : new OgcFailure("NoApplicableCode", exception.Message, StatusCodes.Status500InternalServerError);

    private static IResult Report(OgcFailure failure) => Report(failure.Code, failure.Message, failure.Status);

    private static IResult Report(string code, string message, int status) =>
        Results.Text(OgcXml.ServiceExceptionReport(code, message), "application/xml", Encoding.UTF8, status);
}

/// <summary>The OGC <c>ServiceException</c> a failure maps to (ADR-0053 §3).</summary>
internal sealed record OgcFailure(string Code, string Message, int Status);
