using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;

namespace Spatial.Host.Api;

/// <summary>
/// Canonical-binary wire helpers (ADR-0033): geometries cross typed routes
/// as Base64 <c>SGEOM</c> bytes and feature batches as Base64 <c>SFBAT</c>
/// bytes (ADR-0020). Malformed payloads throw <see cref="Spatial.Contracts.SpatialException"/>
/// with code <c>invalid.arguments</c> naming the field.
/// </summary>
internal static class CodecWire
{
    public static IGeometry DecodeGeometry(string base64, string field)
    {
        var bytes = DecodeBytes(base64, field);
        try
        {
            return GeometryCodec.Decode(bytes);
        }
        catch (Exception exception)
        {
            throw Contracts.SpatialException.BadArguments($"'{field}' is not valid SGEOM geometry: {exception.Message}");
        }
    }

    public static string EncodeGeometry(IGeometry geometry) =>
        Convert.ToBase64String(GeometryCodec.Encode(geometry));

    public static FeatureBatch DecodeBatch(string base64, string field)
    {
        var bytes = DecodeBytes(base64, field);
        try
        {
            return FeatureBatchCodec.Decode(bytes);
        }
        catch (Exception exception)
        {
            throw Contracts.SpatialException.BadArguments($"'{field}' is not a valid SFBAT batch: {exception.Message}");
        }
    }

    public static string EncodeBatch(FeatureBatch batch) =>
        Convert.ToBase64String(FeatureBatchCodec.Encode(batch));

    private static byte[] DecodeBytes(string base64, string field)
    {
        try
        {
            return Convert.FromBase64String(base64);
        }
        catch (FormatException)
        {
            throw Contracts.SpatialException.BadArguments($"'{field}' must be Base64 canonical bytes.");
        }
    }
}
