namespace Spatial.Interop.Esri;

/// <summary>
/// The Esri error envelope (spec §2.0.3):
/// <c>{"error": {"code": 400, "message": "...", "details": ["..."]}}</c>.
/// The facade maps engine <c>SpatialException</c> codes onto these numeric
/// codes; the provider maps these back. Details are optional and carry no
/// secrets (ADR-0035 §3).
/// </summary>
public sealed record EsriError(int Code, string Message, IReadOnlyList<string>? Details = null);

/// <summary>
/// The numeric error codes the GeoServices specification defines for the
/// served resources. HTTP status is chosen by the facade to stay consistent
/// with the engine mapping (ADR-0035), not by these codes.
/// </summary>
public static class EsriErrorCodes
{
    /// <summary>Invalid or missing input parameters.</summary>
    public const int InvalidParameters = 400;

    /// <summary>Resource not found.</summary>
    public const int NotFound = 404;

    /// <summary>The service is unavailable (backing store unreachable).</summary>
    public const int ServiceUnavailable = 503;

    /// <summary>Request cancelled by the client.</summary>
    public const int RequestCancelled = 499;

    /// <summary>An unclassified server-side failure.</summary>
    public const int ServerError = 500;
}

/// <summary>
/// A typed failure of the shared Esri wire codec (malformed geometry,
/// rejected <c>{"url": ...}</c> input, unknown WKID). The adapter and the
/// provider translate it into their own surfaces — it never escapes as a
/// foreign protocol concept.
/// </summary>
public sealed class EsriInteropException : Exception
{
    public EsriInteropException(int code, string message)
        : base(message)
    {
        Code = code;
    }

    public EsriInteropException(int code, string message, Exception? inner)
        : base(message, inner)
    {
        Code = code;
    }

    /// <summary>The Esri error code to place in the envelope.</summary>
    public int Code { get; }

    /// <summary>An invalid or missing input parameter.</summary>
    public static EsriInteropException Invalid(string message) => new(EsriErrorCodes.InvalidParameters, message);

    /// <summary>An invalid input carrying an inner failure.</summary>
    public static EsriInteropException Invalid(string message, Exception inner) => new(EsriErrorCodes.InvalidParameters, message, inner);

    /// <inheritdoc />
    public override string ToString() => $"EsriInteropException({Code}): {Message}";
}
