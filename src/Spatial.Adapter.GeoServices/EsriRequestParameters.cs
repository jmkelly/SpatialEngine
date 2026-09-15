using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The merged query-string and form/JSON body parameters of one GeoServices
/// request. GeoServices passes every operation argument as a named string
/// (GET query or POST form), so the facade reads them uniformly.
/// </summary>
internal sealed class EsriRequestParameters
{
    private readonly Dictionary<string, string> _values;

    private EsriRequestParameters(Dictionary<string, string> values) => _values = values;

    /// <summary>Reads the query string and, for POST, the form or JSON body.</summary>
    public static async Task<EsriRequestParameters> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in context.Request.Query)
        {
            values[key] = value.ToString();
        }

        if (HttpMethods.IsPost(context.Request.Method))
        {
            await ReadBodyAsync(context, values, cancellationToken);
        }

        return new EsriRequestParameters(values);
    }

    /// <summary>The raw value, or null when the parameter is absent.</summary>
    public string? Get(string name) => _values.TryGetValue(name, out var value) ? value : null;

    /// <summary>Whether the parameter was supplied (even with an empty value).</summary>
    public bool Has(string name) => _values.ContainsKey(name);

    /// <summary>A boolean parameter (<c>true</c>/<c>false</c>), or the fallback when absent.</summary>
    public bool GetBool(string name, bool fallback)
    {
        var value = Get(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : ParseBool(value);
    }

    /// <summary>A required non-empty parameter, or a typed invalid-argument failure.</summary>
    public string Require(string name) =>
        string.IsNullOrWhiteSpace(Get(name))
            ? throw GeoServicesErrors.Invalid($"The '{name}' parameter is required.")
            : Get(name)!;

    private static bool ParseBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => throw GeoServicesErrors.Invalid($"Expected a boolean value, got '{value}'."),
    };

    private static async Task ReadBodyAsync(HttpContext context, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            foreach (var (key, value) in form)
            {
                values[key] = value.ToString();
            }

            return;
        }

        if (context.Request.ContentType?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
        {
            await ReadJsonBodyAsync(context, values, cancellationToken);
        }
    }

    private static async Task ReadJsonBodyAsync(HttpContext context, Dictionary<string, string> values, CancellationToken cancellationToken)
    {
        using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in document.RootElement.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
        }
    }
}
