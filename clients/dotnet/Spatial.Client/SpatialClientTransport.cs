using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Http;

namespace Spatial.Client;

/// <summary>
/// The transport seam of the .NET client SDK: one <see cref="HttpClient"/>,
/// the shared camelCase JSON wire (<see cref="HostApiJson"/>), the canonical
/// SGEOM/SFBAT Base64 codec, and mapping of non-success responses to
/// structured <see cref="SpatialClientException"/>s. Extracted from
/// <see cref="SpatialClient"/> so the client type carries only the typed
/// per-route API surface (ADR-0040).
/// </summary>
internal sealed class SpatialClientTransport
{
    private readonly HttpClient _http;

    public SpatialClientTransport(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    public async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    public async Task<T> PostAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(url, body, HostApiJson.Options, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    /// <summary>POSTs a JSON body and reads an image response, including the host's raster metadata headers.</summary>
    public async Task<RasterImage> PostForImageAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(url, body, HostApiJson.Options, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw await FailAsync(response, cancellationToken);
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        return new RasterImage(
            content,
            mediaType,
            HeaderInt(response, "X-Raster-Width"),
            HeaderInt(response, "X-Raster-Height"),
            FormatOf(mediaType));
    }

    private static int HeaderInt(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
        && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    private static RasterFormat FormatOf(string mediaType) => mediaType switch
    {
        "image/jpeg" => RasterFormat.Jpeg,
        "image/webp" => RasterFormat.Webp,
        "image/tiff" => RasterFormat.Tiff,
        _ => RasterFormat.Png,
    };

    /// <summary>Sends one request, optionally with a bearer admin token, and reads the typed body.</summary>
    public async Task<T> SendAsync<T>(HttpMethod method, string url, HttpContent? content, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            var value = await response.Content.ReadFromJsonAsync<T>(HostApiJson.Options, cancellationToken);
            return value ?? throw new SpatialClientException(500, "empty", "The host returned an empty body.");
        }

        throw await FailAsync(response, cancellationToken);
    }

    private static async Task<SpatialClientException> FailAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(HostApiJson.Options, cancellationToken);
            if (error is not null)
            {
                return new SpatialClientException((int)response.StatusCode, error.Code, error.Message);
            }
        }
        catch (Exception)
        {
            // Fall through to the status-only failure below.
        }

        return new SpatialClientException((int)response.StatusCode, "http.error", $"The host failed with {(HttpStatusCode)response.StatusCode}.");
    }
}
