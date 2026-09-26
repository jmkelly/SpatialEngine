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
        Results.Json(new ErrorResponse(SpatialException.AuthUnauthorized, message), statusCode: StatusCodes.Status401Unauthorized);

    /// <summary>A 403 response for a caller without the required auth role.</summary>
    public static IResult Forbidden(string message) =>
        Results.Json(new ErrorResponse(SpatialException.AuthForbidden, message), statusCode: StatusCodes.Status403Forbidden);

    /// <summary>One recognised failure kind: the exception shape it matches and the result it maps to.</summary>
    private sealed record Failure(Func<Exception, bool> Matches, Func<Exception, IResult> Map);

    /// <summary>
    /// The failure kinds the host recognises before a provider failure. A
    /// malformed upload is a client error, not a provider failure: the ingest
    /// decoder documents that the host maps it to invalid.arguments.
    /// </summary>
    private static readonly Failure[] ClientFailures =
    [
        new(
            exception => exception is OperationCanceledException,
            _ => Results.StatusCode(StatusCodes.Status499ClientClosedRequest)),
        new(
            exception => exception is IngestFormatException or JsonException,
            exception => Invalid(exception.Message)),
    ];

    public static IResult Map(Exception exception) =>
        exception is SpatialException spatial ? MapSpatial(spatial) : MapClientFailure(exception);

    private static IResult MapClientFailure(Exception exception) =>
        ClientFailures.FirstOrDefault(failure => failure.Matches(exception)) is { } failure
            ? failure.Map(exception)
            : ProviderFailure(exception);

    /// <summary>The HTTP result for each spatial error code; an unlisted code is an internal failure.</summary>
    private static readonly Dictionary<string, Func<ErrorResponse, IResult>> SpatialResults = new(StringComparer.Ordinal)
    {
        [SpatialException.InvalidArguments] = Results.BadRequest,
        [SpatialException.NotFound] = Results.NotFound,
        [SpatialException.StoreUnavailable] = error => Results.Json(error, statusCode: StatusCodes.Status503ServiceUnavailable),
        [SpatialException.AuthFailed] = Unauthorized,
        [SpatialException.AuthUnauthorized] = Unauthorized,
        [SpatialException.AuthForbidden] = Forbidden,
    };

    private static IResult MapSpatial(SpatialException spatial) =>
        SpatialResults.TryGetValue(spatial.Code, out var result)
            ? result(new ErrorResponse(spatial.Code, spatial.Message))
            : Results.Json(
                new ErrorResponse(spatial.Code, spatial.Message),
                statusCode: StatusCodes.Status500InternalServerError);

    private static IResult Invalid(string message) =>
        Results.BadRequest(new ErrorResponse(SpatialException.InvalidArguments, message));

    private static IResult ProviderFailure(Exception exception) =>
        Results.Json(
            new ErrorResponse("provider.failure", exception.Message),
            statusCode: StatusCodes.Status500InternalServerError);

    private static IResult Unauthorized(ErrorResponse error) =>
        Results.Json(error, statusCode: StatusCodes.Status401Unauthorized);

    private static IResult Forbidden(ErrorResponse error) =>
        Results.Json(error, statusCode: StatusCodes.Status403Forbidden);
}
