using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Spatial.Interop.Esri;
using Spatial.PluginSdk;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The facade's JSON surface. Esri responses are written explicitly
/// (camelCase, no null noise) so the adapter owns its wire shape
/// independently of the engine host API (ADR-0035).
/// </summary>
internal static class EsriJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serialises a shaped record with the facade's options.</summary>
    public static IResult Value(object value, int statusCode = StatusCodes.Status200OK) =>
        Results.Json(value, Options, contentType: "application/json", statusCode: statusCode);

    /// <summary>
    /// Writes a response with a raw <see cref="Utf8JsonWriter"/> (for nested feature/geometry bodies).
    /// The writer bytes go straight to the body: an earlier shape decoded them
    /// to a UTF-16 string and re-encoded on write, doubling the per-response
    /// garbage on this hot path (T-092 burst-tail mitigation). Wire shape is
    /// unchanged (status, <c>application/json; charset=utf-8</c>, exact writer bytes).
    /// </summary>
    public static IResult Write(Action<Utf8JsonWriter> write, int statusCode = StatusCodes.Status200OK)
    {
        byte[] bytes;
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream))
            {
                write(writer);
            }

            bytes = stream.ToArray();
        }

        return new EsriJsonBytesResult(bytes, statusCode);
    }
}

/// <summary>
/// A pre-rendered Esri JSON body: status, JSON content type and an exact
/// content length, with the writer bytes copied once to the response stream.
/// </summary>
internal sealed class EsriJsonBytesResult(byte[] body, int statusCode) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json; charset=utf-8";
        httpContext.Response.ContentLength = body.Length;
        return httpContext.Response.Body.WriteAsync(body).AsTask();
    }
}

/// <summary>The <c>f</c> parameter negotiation: the JSON resources serve JSON only (with <c>pjson</c> accepted
/// as a JSON alias); the authored-metadata resources serve their XML document only (ADR-0068).</summary>
internal static class EsriFormat
{
    public const string Json = "json";

    /// <summary>The pretty-printed JSON alias (GDAL ESRIJSON driver, pygeoapi metadata fetch).</summary>
    public const string PrettyJson = "pjson";

    /// <summary>Validates the requested format, throwing a typed error for anything but json/pjson.</summary>
    public static void Ensure(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)
            || string.Equals(format, Json, StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, PrettyJson, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw EsriInteropException.Invalid($"Format '{format}' is not supported; supportedQueryFormats is 'JSON' — use f=json (f=pjson is accepted as an alias).");
    }

    /// <summary>
    /// Validates the requested format of an authored-metadata resource (ADR-0068):
    /// the document is served as <c>application/xml</c>, so only an absent
    /// format or <c>f=xml</c> passes; anything else (including <c>f=json</c>)
    /// is a typed <c>invalid.arguments</c> failure.
    /// </summary>
    public static void EnsureXml(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)
            || string.Equals(format, "xml", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw EsriInteropException.Invalid($"Format '{format}' is not supported; the metadata resource serves its authored XML document — use f=xml.");
    }
}

/// <summary>Maps engine and interop failures onto the Esri error envelope (spec §2.0.3).</summary>
internal static class EsriErrorMapper
{
    public static IResult Map(Exception exception)
    {
        if (exception is EsriInteropException interop)
        {
            return Envelope(interop.Code, interop.Message, HttpFor(interop.Code));
        }

        if (exception is SpatialException spatial)
        {
            return EngineFailure(spatial);
        }

        if (exception is OperationCanceledException)
        {
            return Envelope(EsriErrorCodes.RequestCancelled, "The request was cancelled.", StatusCodes.Status499ClientClosedRequest);
        }

        return Envelope(EsriErrorCodes.ServerError, "The operation failed.", StatusCodes.Status500InternalServerError);
    }

    private static IResult EngineFailure(SpatialException spatial)
    {
        var code = CodeFor(spatial.Code);
        return Envelope(code, spatial.Message, HttpFor(code));
    }

    /// <summary>Maps a per-feature edit failure to the Esri result error code (ADR-0037).</summary>
    public static int EditCodeFor(Exception exception) => EditCodeForException(exception);

    /// <summary>Maps a stored engine error code to the Esri result error code.</summary>
    public static int EditCodeFor(string? spatialCode) => EditCodeForCode(spatialCode);

    private static int EditCodeForException(Exception exception) => exception switch
    {
        EsriInteropException interop => interop.Code,
        SpatialException spatial => CodeFor(spatial.Code),
        ArgumentException => EsriErrorCodes.InvalidParameters,
        OperationCanceledException => EsriErrorCodes.RequestCancelled,
        _ => EsriErrorCodes.ServerError,
    };

    private static int EditCodeForCode(string? spatialCode) => spatialCode switch
    {
        null or "" => EsriErrorCodes.InvalidParameters,
        _ => CodeFor(spatialCode),
    };

    /// <summary>
    /// Writes the Esri error envelope. <c>details</c> is always present (an
    /// empty array when there is nothing to add), matching the Esri examples.
    /// </summary>
    private static IResult Envelope(int code, string message, int statusCode) =>
        EsriJson.Value(new EsriErrorResponse(new EsriError(code, message, [])), statusCode);

    private static int CodeFor(string spatialCode) => spatialCode switch
    {
        SpatialException.InvalidArguments => EsriErrorCodes.InvalidParameters,
        SpatialException.NotFound => EsriErrorCodes.NotFound,
        SpatialException.StoreUnavailable => EsriErrorCodes.ServiceUnavailable,
        _ => EsriErrorCodes.ServerError,
    };

    private static int HttpFor(int esriCode)
    {
        if (esriCode == EsriErrorCodes.InvalidParameters)
        {
            return StatusCodes.Status400BadRequest;
        }

        if (esriCode == EsriErrorCodes.NotFound)
        {
            return StatusCodes.Status404NotFound;
        }

        if (esriCode == EsriErrorCodes.ServiceUnavailable)
        {
            return StatusCodes.Status503ServiceUnavailable;
        }

        if (esriCode == EsriErrorCodes.TokenRequired)
        {
            return StatusCodes.Status401Unauthorized;
        }

        if (esriCode == EsriErrorCodes.InvalidToken)
        {
            return StatusCodes.Status403Forbidden;
        }

        return esriCode == EsriErrorCodes.RequestCancelled
            ? StatusCodes.Status499ClientClosedRequest
            : StatusCodes.Status500InternalServerError;
    }
}

/// <summary>The Esri error envelope root.</summary>
internal sealed record EsriErrorResponse(EsriError Error);
