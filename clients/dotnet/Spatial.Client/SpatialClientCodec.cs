using System.Net.Http.Json;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;

namespace Spatial.Client;

/// <summary>
/// The client SDK's wire codec helpers: the shared camelCase JSON options and
/// the canonical SGEOM/SFBAT Base64 encoding. Kept separate from
/// <see cref="SpatialClientTransport"/> so the transport type carries only
/// the HTTP request/response seam.
/// </summary>
internal static class SpatialClientCodec
{
    /// <summary>Serialises a body with the shared camelCase wire options.</summary>
    public static HttpContent Json(object body) =>
        JsonContent.Create(body, options: HostApiJson.Options);

    public static string Encode(IGeometry geometry) =>
        Convert.ToBase64String(GeometryCodec.Encode(geometry));

    public static IGeometry Decode(string base64) =>
        GeometryCodec.Decode(Convert.FromBase64String(base64));

    public static FeatureBatch DecodeBatch(string base64) =>
        FeatureBatchCodec.Decode(Convert.FromBase64String(base64));
}
