using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Api;

/// <summary>Maps <see cref="ErrorResponse"/> from service failures (ADR-0033).</summary>
internal static class ErrorMapper
{
    public static IResult Map(Exception exception) =>
        exception switch
        {
            SpatialException spatial => spatial.Code switch
            {
                SpatialException.InvalidArguments => Results.BadRequest(new ErrorResponse(spatial.Code, spatial.Message)),
                SpatialException.NotFound => Results.NotFound(new ErrorResponse(spatial.Code, spatial.Message)),
                SpatialException.StoreUnavailable => Results.Json(
                    new ErrorResponse(spatial.Code, spatial.Message),
                    statusCode: StatusCodes.Status503ServiceUnavailable),
                _ => Results.Json(
                    new ErrorResponse(spatial.Code, spatial.Message),
                    statusCode: StatusCodes.Status500InternalServerError),
            },
            OperationCanceledException => Results.StatusCode(StatusCodes.Status499ClientClosedRequest),
            _ => Results.Json(
                new ErrorResponse("provider.failure", exception.Message),
                statusCode: StatusCodes.Status500InternalServerError),
        };
}
