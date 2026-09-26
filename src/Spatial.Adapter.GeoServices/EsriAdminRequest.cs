using Microsoft.AspNetCore.Http;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Reading an Esri admin request's values: a query parameter, falling back to
/// the multipart form, with blank values treated as absent. Shared by the
/// service, upload and publish projections so they all read a parameter the
/// same way.
/// </summary>
internal static class EsriAdminRequest
{
    /// <summary>The trimmed query value, or null when absent or blank.</summary>
    public static string? Query(IQueryCollection query, string key)
    {
        var value = query[key].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    /// <summary>The form value for the key, falling back to the query string.</summary>
    public static string? Form(IFormCollection form, string key, HttpContext context) =>
        form.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value.ToString()
            : Query(context.Request.Query, key);
}
