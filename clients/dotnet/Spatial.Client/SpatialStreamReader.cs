using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.PluginSdk.Codec;
using Spatial.PluginSdk.Http;

namespace Spatial.Client;

/// <summary>
/// The streaming read surface of the .NET client SDK: bounded stream
/// resources as decoded items (scalars, strings, <c>byte[]</c>, geometry,
/// handles — the SDK value codec) and typed canonical feature batches. Kept
/// apart from <see cref="SpatialClient"/> so each type stays cohesive.
/// </summary>
public sealed class SpatialStreamReader
{
    private readonly HttpClient _http;

    internal SpatialStreamReader(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Reads a bounded stream resource as decoded items. Consuming the stream
    /// to its end closes the resource; a stream that ended with a structured
    /// failure throws <see cref="CapabilityStreamException"/>.
    /// </summary>
    public async IAsyncEnumerable<object?> ReadStreamAsync(
        string resourceToken, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"/api/resources/{Uri.EscapeDataString(resourceToken)}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        SpatialClientHelpers.EnsureSuccess(response);
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(body, Encoding.UTF8);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var node = JsonNode.Parse(line);
            if (node is JsonObject { } obj && obj.TryGetPropertyValue("$error", out var errorNode))
            {
                var error = errorNode.Deserialize<CapabilityErrorDto>(HostApiJson.Options)
                    ?? new CapabilityErrorDto("ProviderFailure", "provider.failure", "the stream failed without a structured error");
                throw new CapabilityStreamException(error);
            }

            yield return ValueCodec.Decode(node);
        }
    }

    /// <summary>
    /// Reads a feature stream (scan/query) as typed, decoded feature batches —
    /// the canonical binary interchange (ADR-0020) each <c>byte[]</c> item
    /// carries.
    /// </summary>
    public async IAsyncEnumerable<FeatureBatch> ReadFeatureBatchesAsync(
        string resourceToken, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in ReadStreamAsync(resourceToken, cancellationToken).WithCancellation(cancellationToken))
        {
            if (item is not byte[] bytes)
            {
                throw new CapabilityStreamException(
                    "a feature stream item is not canonical binary (byte[]); the interchange contract is violated");
            }

            yield return FeatureBatchCodec.Decode(bytes);
        }
    }
}

/// <summary>Shared little helpers of the client SDK (internal).</summary>
internal static class SpatialClientHelpers
{
    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new SpatialApiException((int)response.StatusCode, "the request failed");
        }
    }
}
