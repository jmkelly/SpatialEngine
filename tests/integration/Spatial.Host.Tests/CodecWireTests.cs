using Spatial.Core.Geometry;
using Spatial.Core.Geometry.Codec;
using Spatial.Host.Api;
using Spatial.PluginSdk;

namespace Spatial.Host.Tests;

/// <summary>
/// The host's geometry wire codec (ADR-0033): Base64 SGEOM decoding rejects
/// malformed input with <c>invalid.arguments</c> naming the field, and round
/// trips preserve the geometry.
/// </summary>
public sealed class CodecWireTests
{
    [Fact]
    public void Decode_rejects_malformed_base64()
    {
        var exception = Assert.Throws<SpatialException>(() => CodecWire.DecodeGeometry("!!!", "geometry"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'geometry'", exception.Message);
    }

    [Fact]
    public void Decode_rejects_non_sgeom_bytes()
    {
        var exception = Assert.Throws<SpatialException>(() => CodecWire.DecodeGeometry(Convert.ToBase64String([0x00]), "geometry"));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("'geometry'", exception.Message);
    }

    [Fact]
    public void Wire_round_trip_preserves_the_geometry()
    {
        var point = GeometryFactory.CreatePoint(1, 2);

        Assert.Equal(point, CodecWire.DecodeGeometry(CodecWire.EncodeGeometry(point), "geometry"));
    }
}
