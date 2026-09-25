namespace Spatial.Contracts;

/// <summary>
/// The single error type of the in-process spatial services (ADR-0033):
/// actionable failures carry a stable dotted <see cref="Code"/> so HTTP
/// (400 vs 503 vs 500) and clients can branch without parsing messages.
/// </summary>
public sealed class SpatialException : Exception
{
    public const string InvalidArguments = "invalid.arguments";
    public const string StoreUnavailable = "store.unavailable";
    public const string NotFound = "not.found";
    public const string AuthFailed = "auth.failed";
    public const string AuthUnauthorized = "auth.unauthorized";
    public const string AuthForbidden = "auth.forbidden";

    public SpatialException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public SpatialException(string code, string message, Exception? inner)
        : base(message, inner)
    {
        Code = code;
    }

    public string Code { get; }

    public static SpatialException BadArguments(string message) => new(InvalidArguments, message);

    public static SpatialException Unavailable(string message, Exception? inner = null) =>
        new(StoreUnavailable, message, inner);

    public static SpatialException Missing(string message) => new(NotFound, message);

    public static SpatialException AuthenticationFailed(string message = "The username or password is not valid.") =>
        new(AuthFailed, message);

    public static SpatialException AuthenticationRequired(string message = "Authentication is required.") =>
        new(AuthUnauthorized, message);

    public static SpatialException Forbidden(string message) => new(AuthForbidden, message);
}
