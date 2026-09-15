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
            [Spatial.PluginSdk.Providers.MapServiceKind.Wms]);
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

    [Fact]
    public async Task Get_feature_info_honours_feature_count()
    {
        // MapServer trace D (research/interop/wms-conformance.md G8):
        // FEATURE_COUNT caps the matches around the click.
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(
            OgcFixtures.City("Alpha", 1, 5, 55),
            OgcFixtures.City("Beta", 2, 5.02, 55.01),
            OgcFixtures.City("Gamma", 3, 4.98, 54.99));

        var (_, body) = await IdentifyRawAsync(
            map,
            services,
            "?service=WMS&request=GetFeatureInfo&query_layers=Cities&crs=CRS:84"
                + "&bbox=0,50,10,60&width=100&height=100&info_format=text/plain&feature_count=1");

        Assert.Equal(1, CountOccurrences(body, "Feature: "));
    }

    [Fact]
    public async Task Get_feature_info_rejects_a_bad_feature_count()
    {
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 5, 55));

        var exception = await Assert.ThrowsAsync<OgcServiceException>(
            () => IdentifyRawAsync(
                map,
                services,
                "?service=WMS&request=GetFeatureInfo&query_layers=Cities&crs=CRS:84"
                    + "&bbox=0,50,10,60&width=100&height=100&info_format=text/plain&feature_count=abc"));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }

    [Fact]
    public async Task Get_feature_info_observes_cancellation()
    {
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 5, 55));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => IdentifyRawAsync(
                map,
                services,
                "?service=WMS&request=GetFeatureInfo&query_layers=Cities&crs=CRS:84"
                    + "&bbox=0,50,10,60&width=100&height=100&info_format=text/plain",
                cancelled.Token));
    }

    [Fact]
    public async Task Get_map_passes_transparent_true_through_the_png_path()
    {
        // QGIS sends TRANSPARENT=TRUE upper-case (research/interop/wms-conformance.md
        // trace A): the adapter must forward the alpha request, not swallow it.
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer, out _);

        await GetMapRawAsync(map, services, MapQuery("format=image/png&transparent=TRUE"));

        Assert.NotNull(renderer.LastRequest);
        Assert.True(renderer.LastRequest.Transparent);
        Assert.Null(renderer.LastRequest.Background);
    }

    [Fact]
    public async Task Get_map_defaults_to_opaque()
    {
        // No TRANSPARENT and no BGCOLOR (CITE basic:no-bgcolor): the adapter
        // passes nulls and the renderer paints the opaque-white default.
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer, out _);

        await GetMapRawAsync(map, services, MapQuery("format=image/png"));

        Assert.NotNull(renderer.LastRequest);
        Assert.False(renderer.LastRequest.Transparent);
        Assert.Null(renderer.LastRequest.Background);
    }

    [Fact]
    public async Task Get_map_jpeg_with_transparent_stays_lenient()
    {
        // JPEG carries no alpha and QGIS never sends this combination, but a
        // client that does must get an image, never a 500.
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer, out _);

        var (contentType, _) = await GetMapRawAsync(map, services, MapQuery("format=image/jpeg&transparent=TRUE"));

        Assert.Equal("image/png", contentType);
        Assert.NotNull(renderer.LastRequest);
        Assert.Equal(Spatial.PluginSdk.RasterFormat.Jpeg, renderer.LastRequest.Format);
        Assert.True(renderer.LastRequest.Transparent);
    }

    [Fact]
    public async Task Get_map_accepts_an_explicit_bgcolor()
    {
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer, out _);

        await GetMapRawAsync(map, services, MapQuery("format=image/png&bgcolor=0x0000FF"));

        Assert.NotNull(renderer.LastRequest);
        Assert.Equal("#0000FF", renderer.LastRequest.Background);
    }

    private static string MapQuery(string extra) =>
        $"?service=WMS&request=GetMap&version=1.3.0&layers=Cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&{extra}";

    private static OgcRequestServices MapServices(
        Spatial.PluginSdk.Providers.Map map, CapturingRenderer renderer, out OgcFixtures.FakeStore store)
    {
        store = new OgcFixtures.FakeStore(OgcFixtures.Dataset);
        return new OgcRequestServices(
            new OgcFixtures.FakeStoreRegistry(store),
            new OgcFixtures.FakeRegistry(map),
            renderer,
            new OgcFixtures.IdentityTransforms());
    }

    private static async Task<(string? ContentType, string Body)> GetMapRawAsync(
        Spatial.PluginSdk.Providers.Map map, OgcRequestServices services, string query) =>
        await IdentifyRawAsync(map, services, query);

    private sealed class CapturingRenderer : Spatial.PluginSdk.IMapRenderer
    {
        public Spatial.PluginSdk.MapRenderRequest? LastRequest { get; private set; }

        public Task<Spatial.PluginSdk.RasterImage> RenderAsync(
            Spatial.PluginSdk.MapRenderRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(
                new Spatial.PluginSdk.RasterImage([1, 2, 3], "image/png", request.Viewport.Width, request.Viewport.Height, Spatial.PluginSdk.RasterFormat.Png));
        }
    }

    private static int CountOccurrences(string body, string marker)
    {
        var count = 0;
        var index = 0;
        while ((index = body.IndexOf(marker, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += marker.Length;
        }

        return count;
    }

    private static async Task<(string? ContentType, string Body)> IdentifyRawAsync(
        Spatial.PluginSdk.Providers.Map map, OgcRequestServices services, string query, CancellationToken cancellationToken = default)
    {
        var request = new DefaultHttpContext();
        request.Request.QueryString = new QueryString(query);
        var parameters = await OgcParameters.ReadAsync(request, CancellationToken.None);
        var response = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        response.Response.Body = new MemoryStream();
        var result = await WmsService.HandleAsync(map.Name, parameters, services, new OgcOptions(), request, cancellationToken);
        await result.ExecuteAsync(response);
        response.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(response.Response.Body).ReadToEndAsync(CancellationToken.None);
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

    [Fact]
    public async Task Parse_dpi_prefers_the_explicit_dpi_parameter()
    {
        var parameters = await ParametersAsync("?dpi=192&map_resolution=150&format_options=dpi:72");

        Assert.Equal(192, WmsService.ParseDpi(parameters));
    }

    [Fact]
    public async Task Parse_dpi_falls_back_to_map_resolution_then_format_options()
    {
        Assert.Equal(150, WmsService.ParseDpi(await ParametersAsync("?map_resolution=150&format_options=dpi:72")));
        Assert.Equal(72, WmsService.ParseDpi(await ParametersAsync("?format_options=antialiasing:true;dpi:72")));
        Assert.Equal(
            Spatial.PluginSdk.MapRenderRequest.ReferenceDpi,
            WmsService.ParseDpi(await ParametersAsync("?format_options=antialiasing:true")));
        Assert.Equal(
            Spatial.PluginSdk.MapRenderRequest.ReferenceDpi,
            WmsService.ParseDpi(await ParametersAsync(string.Empty)));
    }

    [Theory]
    [InlineData("?format_options=dpi:0")]
    [InlineData("?format_options=dpi:-5")]
    [InlineData("?format_options=dpi:abc")]
    [InlineData("?format_options=dpi")]
    [InlineData("?format_options=other:72")]
    public async Task Parse_dpi_skips_a_malformed_format_options_dpi(string query)
    {
        Assert.Equal(
            Spatial.PluginSdk.MapRenderRequest.ReferenceDpi,
            WmsService.ParseDpi(await ParametersAsync(query)));
    }

    [Theory]
    [InlineData("?dpi=abc")]
    [InlineData("?dpi=0")]
    [InlineData("?map_resolution=NaN")]
    public async Task Parse_dpi_rejects_a_malformed_explicit_value(string query)
    {
        var parameters = await ParametersAsync(query);

        var exception = Assert.Throws<OgcServiceException>(() => WmsService.ParseDpi(parameters));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }

    private static async Task<OgcParameters> ParametersAsync(string query)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = new QueryString(query);
        return await OgcParameters.ReadAsync(context, CancellationToken.None);
    }
}
