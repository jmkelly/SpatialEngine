using Microsoft.AspNetCore.Http;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// A typed OGC failure carrying the <c>ServiceException</c> code and HTTP
/// status the report is served with. The OGC protocol vocabulary (exception
/// codes) stays inside this adapter and never crosses a contract
/// (ADR-0053 §3).
/// </summary>
internal sealed class OgcServiceException : Exception
{
    public OgcServiceException(string code, string message, int status)
        : base(message)
    {
        Code = code;
        Status = status;
    }

    /// <summary>The OGC <c>ServiceException</c> code (for example <c>LayerNotDefined</c>).</summary>
    public string Code { get; }

    /// <summary>The HTTP status the report is served with.</summary>
    public int Status { get; }

    /// <summary>A required request parameter is absent.</summary>
    public static OgcServiceException Missing(string name) =>
        new("MissingParameterValue", $"The '{name}' parameter is required.", StatusCodes.Status400BadRequest);

    /// <summary>A parameter value is malformed or unsupported.</summary>
    public static OgcServiceException Invalid(string message) =>
        new("InvalidParameterValue", message, StatusCodes.Status400BadRequest);

    /// <summary>The requested layer or service is not defined.</summary>
    public static OgcServiceException NotDefined(string message) =>
        new("LayerNotDefined", message, StatusCodes.Status404NotFound);

    /// <summary>The requested operation is known but unsupported.</summary>
    public static OgcServiceException NotSupported(string message) =>
        new("OperationNotSupported", message, StatusCodes.Status400BadRequest);

    /// <summary>A requested output format the service does not support (WMS <c>InvalidFormat</c>).</summary>
    public static OgcServiceException InvalidFormat(string message) =>
        new("InvalidFormat", message, StatusCodes.Status400BadRequest);

    /// <summary>A GetFeatureInfo pixel coordinate is malformed (WMS <c>InvalidPoint</c>).</summary>
    public static OgcServiceException InvalidPoint(string message) =>
        new("InvalidPoint", message, StatusCodes.Status400BadRequest);

    /// <summary>A requested style is not defined for the layer (WMS <c>StyleNotDefined</c>).</summary>
    public static OgcServiceException StyleNotDefined(string message) =>
        new("StyleNotDefined", message, StatusCodes.Status400BadRequest);

    /// <summary>A GetFeatureInfo layer cannot be queried (WMS <c>LayerNotQueryable</c>).</summary>
    public static OgcServiceException LayerNotQueryable(string message) =>
        new("LayerNotQueryable", message, StatusCodes.Status400BadRequest);
}
