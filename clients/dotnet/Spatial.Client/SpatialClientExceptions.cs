using System.Net;

namespace Spatial.Client;

/// <summary>
/// A failed spatial host call: the HTTP status plus the structured error
/// code/message the host returned (<c>invalid.arguments</c>,
/// <c>not.found</c>, <c>store.unavailable</c>, …).
/// </summary>
public sealed class SpatialClientException : Exception
{
    public SpatialClientException(int statusCode, string code, string message, Exception? inner = null)
        : base($"(HTTP {(HttpStatusCode)statusCode}) {code}: {message}", inner)
    {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }

    public string Code { get; }
}
