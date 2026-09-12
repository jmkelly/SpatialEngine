using System.Net;
using System.Net.Http.Json;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;
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

    public static string Encode(IGeometry geometry) =>
        Convert.ToBase64String(GeometryCodec.Encode(geometry));

    public static IGeometry Decode(string base64) =>
        GeometryCodec.Decode(Convert.FromBase64String(base64));

    public static FeatureBatch DecodeBatch(string base64) =>
        FeatureBatchCodec.Decode(Convert.FromBase64String(base64));

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
