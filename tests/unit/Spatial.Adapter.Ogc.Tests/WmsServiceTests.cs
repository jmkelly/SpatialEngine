using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// WMS text/parameter helpers (ADR-0053 §3): attribute formatting for
/// GetFeatureInfo text and BGCOLOR normalisation. Every attribute kind and
/// colour form is exercised so the dispatch table is fully covered.
/// </summary>
public sealed class WmsServiceTests
{
    [Theory]
    [InlineData("text/html", "text/html", "<table>")]
    [InlineData("text/xml", "text/xml", "<FeatureInfoResponse")]
    [InlineData("application/vnd.ogc.gml", "application/vnd.ogc.gml", "gml:Point")]
    public async Task Get_feature_info_serves_the_documented_formats(string infoFormat, string mediaType, string marker)
    {
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(
            OgcFixtures.City("Amsterdam", 900_000, 5, 55),
            Road("road", 4.9, 55, 5.1, 55),
            Block("block", 4.9, 54.9, 5.1, 55.1));

        var (contentType, body) = await IdentifyAsync(map, services, infoFormat, i: null, j: null);

        Assert.Equal(mediaType, contentType);
        Assert.Contains(marker, body);
        Assert.Contains("Amsterdam", body);
    }

    [Fact]
    public async Task Get_feature_info_gml_covers_every_geometry_family()
    {
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(
            OgcFixtures.City("Amsterdam", 900_000, 5, 55),
            Road("road", 4.9, 55, 5.1, 55),
            Block("block", 4.9, 54.9, 5.1, 55.1));

        var (_, body) = await IdentifyAsync(map, services, "application/vnd.ogc.gml", i: null, j: null);

        Assert.Contains("gml:Point", body);
        Assert.Contains("gml:LineString", body);
        Assert.Contains("gml:Polygon", body);
    }

    [Fact]
    public async Task Get_feature_info_rejects_a_non_queryable_layer()
    {
        var map = new Spatial.PluginSdk.Providers.Map(
            OgcFixtures.MapName,
            OgcFixtures.Store,
            [
                new Spatial.PluginSdk.Providers.MapLayer(OgcFixtures.Dataset, 0, "Cities"),
                new Spatial.PluginSdk.Providers.MapLayer(
                    OgcFixtures.Dataset, 1, "Photo", Kind: Spatial.PluginSdk.Providers.MapLayerKind.Image),
            ],
            [Spatial.PluginSdk.Providers.MapService.Wms]);
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 5, 55));

        var exception = await Assert.ThrowsAsync<OgcServiceException>(
            () => IdentifyOnAsync(map, services, "Photo"));

        Assert.Equal("LayerNotQueryable", exception.Code);
    }

    [Fact]
    public async Task Get_feature_info_rejects_an_unknown_format_with_invalid_format()
    {
        var map = OgcFixtures.Map();
        var (services, _) = OgcFixtures.Build(map);

        var exception = await Assert.ThrowsAsync<OgcServiceException>(
            () => IdentifyAsync(map, services, "UnknownFormat", i: null, j: null));

        Assert.Equal("InvalidFormat", exception.Code);
    }

    private static Feature Road(string name, double x1, double y1, double x2, double y2) => new(
        new FeatureId(name),
        OgcFixtures.Schema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromInt64(0),
            AttributeValue.FromGeometry(GeometryFactory.CreateLineString(
                [new Coordinate(x1, y1), new Coordinate(x2, y2)], CoordinateReference.Epsg(4326))),
        ]);

    private static Feature Block(string name, double minX, double minY, double maxX, double maxY) => new(
        new FeatureId(name),
        OgcFixtures.Schema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromInt64(0),
            AttributeValue.FromGeometry(GeometryFactory.CreatePolygon(
                [
                    new Coordinate(minX, minY),
                    new Coordinate(maxX, minY),
                    new Coordinate(maxX, maxY),
                    new Coordinate(minX, maxY),
                    new Coordinate(minX, minY),
                ],
                CoordinateReference.Epsg(4326))),
        ]);

    private static Task<(string? ContentType, string Body)> IdentifyOnAsync(
        Spatial.PluginSdk.Providers.Map map, OgcRequestServices services, string queryLayers)
    {
        var query = "?service=WMS&request=GetFeatureInfo"
            + $"&query_layers={Uri.EscapeDataString(queryLayers)}&crs=CRS:84"
            + "&bbox=0,50,10,60&width=100&height=100&info_format=text/plain";
        return IdentifyRawAsync(map, services, query);
    }

    private static async Task<(string? ContentType, string Body)> IdentifyAsync(
        Spatial.PluginSdk.Providers.Map map, OgcRequestServices services, string infoFormat, int? i, int? j)
    {
        var query = "?service=WMS&request=GetFeatureInfo&query_layers=Cities&crs=CRS:84"
            + "&bbox=0,50,10,60&width=100&height=100"
            + (i is null ? string.Empty : $"&i={i}&j={j}")
            + $"&info_format={Uri.EscapeDataString(infoFormat)}";
        return await IdentifyRawAsync(map, services, query);
    }

    private static async Task<(string? ContentType, string Body)> IdentifyRawAsync(
        Spatial.PluginSdk.Providers.Map map, OgcRequestServices services, string query)
    {
        var request = new DefaultHttpContext();
        request.Request.QueryString = new QueryString(query);
        var parameters = await OgcParameters.ReadAsync(request, CancellationToken.None);
        var response = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        response.Response.Body = new MemoryStream();
        var result = await WmsService.HandleAsync(map.Name, parameters, services, new OgcOptions(), request, CancellationToken.None);
        await result.ExecuteAsync(response);
        response.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(response.Response.Body).ReadToEndAsync();
        return (response.Response.ContentType?.Split(';')[0].Trim(), body);
    }

    [Fact]
    public void Format_value_covers_every_attribute_kind()
    {
        Assert.Equal("true", WmsService.FormatValue(AttributeValue.FromBoolean(true)));
        Assert.Equal("false", WmsService.FormatValue(AttributeValue.FromBoolean(false)));
        Assert.Equal("42", WmsService.FormatValue(AttributeValue.FromInt64(42)));
        Assert.Equal("1.5", WmsService.FormatValue(AttributeValue.FromDouble(1.5)));
        Assert.Equal("text", WmsService.FormatValue(AttributeValue.FromString("text")));
        Assert.Equal(
            "2026-09-17T00:00:00.0000000+00:00",
            WmsService.FormatValue(AttributeValue.FromDateTimeOffset(new DateTimeOffset(2026, 9, 17, 0, 0, 0, TimeSpan.Zero))));
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(guid.ToString(), WmsService.FormatValue(AttributeValue.FromGuid(guid)));
        Assert.Equal(string.Empty, WmsService.FormatValue(AttributeValue.Null));
    }

    [Fact]
    public void Parse_color_normalises_the_documented_forms()
    {
        Assert.Null(WmsService.ParseColor(null));
        Assert.Equal("#AABBCC", WmsService.ParseColor("#aabbcc"));
        Assert.Equal("#AABBCC", WmsService.ParseColor("0xaabbcc"));
        Assert.Equal("#AABBCC", WmsService.ParseColor("aabbcc"));
        Assert.Equal("#AABBCC", WmsService.ParseColor("  #AaBbCc  "));
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("0x1234")]
    [InlineData("#12345g")]
    public void Parse_color_rejects_invalid_values(string value)
    {
        var exception = Assert.Throws<OgcServiceException>(() => WmsService.ParseColor(value));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }
}
