using System.Net;
using System.Net.Http.Json;
using Spatial.PluginSdk.Http;

namespace Spatial.Client;

/// <summary>
/// An HTTP failure of the spatial host: a non-2xx status, or a request that
/// could not be completed against the public API. Completed invocations that
/// carry a structured capability error are NOT thrown — they arrive as an
/// <see cref="Spatial.PluginSdk.Http.InvocationResponse"/> with
/// <c>Ok == false</c> — this exception is reserved for transport and
/// resource-level failures.
/// </summary>
public sealed class SpatialApiException : Exception
{
    public SpatialApiException(int statusCode, string message, Exception? inner = null)
        : base($"(HTTP {(HttpStatusCode)statusCode}) {message}", inner)
    {
        StatusCode = statusCode;
    }

    /// <summary>The HTTP status code of the failed response.</summary>
    public int StatusCode { get; }
}

/// <summary>A bounded stream ended (or was interrupted) with a structured capability failure.</summary>
public sealed class CapabilityStreamException : Exception
{
    public CapabilityStreamException(CapabilityErrorDto error)
        : base($"the stream completed with {error.Code}: {error.Message}")
    {
        Error = error;
    }

    public CapabilityStreamException(string message)
        : base(message)
    {
        Error = new CapabilityErrorDto("ContractViolation", "contract.violation", message);
    }

    /// <summary>The structured failure the stream ended with.</summary>
    public CapabilityErrorDto Error { get; }
}
