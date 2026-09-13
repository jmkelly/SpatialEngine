using Microsoft.AspNetCore.Http;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// The merged, case-insensitive request parameters of one OGC operation
/// (ADR-0053 §3): query-string values plus, for a form POST, the
/// <c>application/x-www-form-urlencoded</c> body. OGC parameter names are
/// case-insensitive, so lookups normalise the key; the last source wins when
/// a name appears in both.
/// </summary>
internal sealed class OgcParameters
{
    private readonly Dictionary<string, string> _values;

    private OgcParameters(Dictionary<string, string> values) => _values = values;

    /// <summary>
    /// The merged request parameters, for diagnostics only. OGC request
    /// parameters carry no secrets (unlike the admin API's query token), so the
    /// adapter may log them to isolate a failing interop client (ADR-0045).
    /// </summary>
    public IReadOnlyDictionary<string, string> Values => _values;

    /// <summary>Reads the query and, when present, the form body (GET or POST).</summary>
    public static async Task<OgcParameters> ReadAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in context.Request.Query)
        {
            values[parameter.Key] = parameter.Value.ToString();
        }

        if (context.Request.HasFormContentType)
        {
            var form = await context.Request.ReadFormAsync(cancellationToken);
            foreach (var parameter in form)
            {
                values[parameter.Key] = parameter.Value.ToString();
            }
        }

        return new OgcParameters(values);
    }

    /// <summary>
    /// The trimmed value, or <c>null</c> when absent or blank. A parameter the
    /// client repeated with the same value (some clients reuse an advertised
    /// DCP URI that already carries it) is collapsed to that value, so
    /// <c>SERVICE=WMS,WMS</c> is read as <c>WMS</c>; a genuine list is left
    /// intact for <see cref="List"/>.
    /// </summary>
    public string? Get(string name)
    {
        if (!_values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return CollapseRepeated(value);
    }

    private static string CollapseRepeated(string value)
    {
        if (!value.Contains(','))
        {
            return value;
        }

        var parts = value.Split(',', StringSplitOptions.TrimEntries);
        return parts.All(part => string.Equals(part, parts[0], StringComparison.OrdinalIgnoreCase))
            ? parts[0]
            : value;
    }

    /// <summary>The trimmed value, or an OGC <c>MissingParameterValue</c> failure.</summary>
    public string Required(string name) => Get(name) ?? throw OgcServiceException.Missing(name);

    /// <summary>Validates and returns the <c>service</c> parameter for the expected protocol.</summary>
    public string RequiredService(string expected)
    {
        var service = Required("service");
        return string.Equals(service, expected, StringComparison.OrdinalIgnoreCase)
            ? service
            : throw OgcServiceException.Invalid($"The 'service' parameter must be '{expected}', got '{service}'.");
    }

    /// <summary>The required <c>request</c> operation name.</summary>
    public string RequiredRequest() => Required("request");

    /// <summary>A comma-separated list parameter (empty when absent).</summary>
    public IReadOnlyList<string> List(string name)
    {
        var value = Get(name);
        return string.IsNullOrEmpty(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
