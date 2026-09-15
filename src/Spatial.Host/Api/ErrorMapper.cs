using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Ingest.Codec;

namespace Spatial.Host.Api;

/// <summary>Maps <see cref="ErrorResponse"/> from service failures (ADR-0033).</summary>
internal static class ErrorMapper
{
    /// <summary>A 401 response for a mutation that carried no admin token.</summary>
    public static IResult Unauthorized(string message) =>
        Results.Json(new ErrorResponse("unauthorized", message), statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>A 403 response for a mutation that carried a wrong admin token.</summary>
    public static IResult Forbidden(string message) =>
        Results.Json(new ErrorResponse("forbidden", message), statusCode: StatusCodes.Status403Forbidden);

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
            // A malformed upload is a client error, not a provider failure: the
            // ingest decoder documents that the host maps it to invalid.arguments.
            IngestFormatException ingest => Results.BadRequest(
                new ErrorResponse(SpatialException.InvalidArguments, ingest.Message)),
            JsonException json => Results.BadRequest(
                new ErrorResponse(SpatialException.InvalidArguments, json.Message)),
            _ => Results.Json(
                new ErrorResponse("provider.failure", exception.Message),
                statusCode: StatusCodes.Status500InternalServerError),
        };
}
