using System.Text.Json;
using Spatial.Core.Geometry;

namespace Spatial.Esri.Codec.Tests;

public sealed class EsriSpatialReferenceTests
{
    private static CoordinateReference? Decode(string json) =>
        EsriSpatialReference.Decode(JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData(4326, 4326)]
    [InlineData(102100, 3857)]
    [InlineData(102113, 3857)]
    [InlineData(27700, 27700)]
    [InlineData(2154, 2154)]
    public void Known_wkids_resolve_to_epsg(int wkid, int epsg)
    {
        Assert.Equal(CoordinateReference.Epsg(epsg), Decode($$"""{"wkid":{{wkid}}}"""));
    }

    [Fact]
    public void Latest_wkid_is_a_fallback()
    {
        Assert.Equal(CoordinateReference.Epsg(3857), Decode("""{"latestWkid":3857}"""));
    }

    [Fact]
    public void An_unknown_wkid_is_rejected()
    {
        var exception = Assert.Throws<EsriInteropException>(() => Decode("""{"wkid":9999}"""));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
        Assert.Contains("curated", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_wkt_only_reference_is_rejected()
    {
        var exception = Assert.Throws<EsriInteropException>(() => Decode("""{"wkt":"GEOGCS[\"WGS 84\"]"}"""));

        Assert.Equal(EsriErrorCodes.InvalidParameters, exception.Code);
    }

    [Fact]
    public void A_missing_reference_is_unspecified()
    {
        Assert.Null(Decode("null"));
    }

    [Fact]
    public void Encoding_prefers_the_curated_wkid()
    {
        Assert.Equal(3857, EsriSpatialReference.ToWkid(CoordinateReference.Epsg(3857)));
    }

    [Fact]
    public void An_epsg_outside_the_map_cannot_be_encoded()
    {
        Assert.Throws<EsriInteropException>(() => EsriSpatialReference.ToWkid(CoordinateReference.Epsg(99999)));
    }

    [Fact]
    public void Wkid_map_accepts_aliases_and_rejects_unknown_codes()
    {
        Assert.True(WkidMap.TryToEpsg(102113, out var epsg));
        Assert.Equal(3857, epsg);
        Assert.False(WkidMap.TryToEpsg(9999, out _));
    }
}
