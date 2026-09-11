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

    /// <summary>Writes a response with a raw <see cref="Utf8JsonWriter"/> (for nested feature/geometry bodies).</summary>
    public static IResult Write(Action<Utf8JsonWriter> write, int statusCode = StatusCodes.Status200OK)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            write(writer);
        }

        return Results.Text(Encoding.UTF8.GetString(stream.ToArray()), "application/json", Encoding.UTF8, statusCode);
    }
}

/// <summary>The <c>f</c> parameter negotiation: this facade serves JSON only.</summary>
internal static class EsriFormat
{
    public const string Json = "json";

    /// <summary>Validates the requested format, throwing a typed error for anything but json.</summary>
    public static void Ensure(string? format)
    {
        if (string.IsNullOrWhiteSpace(format) || string.Equals(format, Json, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw EsriInteropException.Invalid($"Format '{format}' is not supported; the facade serves f=json only.");
    }
}

/// <summary>Maps engine and interop failures onto the Esri error envelope (spec §2.0.3).</summary>
internal static class EsriErrorMapper
{
    public static IResult Map(Exception exception) => exception switch
    {
        EsriInteropException interop =>
            Envelope(interop.Code, interop.Message, HttpFor(interop.Code)),
        SpatialException spatial =>
            EngineFailure(spatial),
        OperationCanceledException =>
            Envelope(EsriErrorCodes.RequestCancelled, "The request was cancelled.", StatusCodes.Status499ClientClosedRequest),
        _ => Envelope(EsriErrorCodes.ServerError, "The operation failed.", StatusCodes.Status500InternalServerError),
    };

    private static IResult EngineFailure(SpatialException spatial)
    {
        var code = CodeFor(spatial.Code);
        return Envelope(code, spatial.Message, HttpFor(code));
    }

    private static IResult Envelope(int code, string message, int statusCode) =>
        EsriJson.Value(new EsriErrorResponse(new EsriError(code, message)), statusCode);

    private static int CodeFor(string spatialCode) => spatialCode switch
    {
        SpatialException.InvalidArguments => EsriErrorCodes.InvalidParameters,
        SpatialException.NotFound => EsriErrorCodes.NotFound,
        SpatialException.StoreUnavailable => EsriErrorCodes.ServiceUnavailable,
        _ => EsriErrorCodes.ServerError,
    };

    private static int HttpFor(int esriCode) => esriCode switch
    {
        EsriErrorCodes.InvalidParameters => StatusCodes.Status400BadRequest,
        EsriErrorCodes.NotFound => StatusCodes.Status404NotFound,
        EsriErrorCodes.ServiceUnavailable => StatusCodes.Status503ServiceUnavailable,
        EsriErrorCodes.RequestCancelled => StatusCodes.Status499ClientClosedRequest,
        _ => StatusCodes.Status500InternalServerError,
    };
}

/// <summary>The Esri error envelope root.</summary>
internal sealed record EsriErrorResponse(EsriError Error);
