using System.Xml.Linq;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.PluginSdk;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// The T-045 remaining surface (research/compat/wms.md): GetStyles and
/// DescribeLayer stay honest rejects, SLD/SLD_BODY on GetMap is rejected
/// instead of silently ignored, GetCapabilities negotiates the 1.1.1
/// dialect, and the QGIS DPI triple drives rendering scale.
/// </summary>
public sealed class WmsRemainingSurfaceTests
{
    private static readonly XNamespace Wms130 = "http://www.opengis.net/wms";

    [Theory]
    [InlineData("GetStyles")]
    [InlineData("DescribeLayer")]
    public async Task Unsupported_sld_operations_stay_honest_rejects(string operation)
    {
        // Diagnostics verdict (T-045 item 1): no recorded client trace has
        // ever sent these operations, so the default-arm reject stays.
        var map = OgcFixtures.Map();
        var (services, _) = OgcFixtures.Build(map);

        var exception = await Assert.ThrowsAsync<OgcServiceException>(
            () => DispatchAsync(map, services, $"?service=WMS&request={operation}"));

        Assert.Equal("OperationNotSupported", exception.Code);
    }

    [Theory]
    [InlineData("sld_body=<StyledLayerDescriptor/>")]
    [InlineData("sld=http://example.com/style.sld")]
    public async Task Get_map_rejects_an_sld_override(string sld)
    {
        // An SLD override silently dropped would render the wrong style with
        // a 200; the honest answer is an explicit reject.
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 5, 55));

        var exception = await Assert.ThrowsAsync<OgcServiceException>(
            () => DispatchAsync(
                map,
                services,
                "?service=WMS&request=GetMap&version=1.3.0&layers=Cities&crs=CRS:84"
                    + "&bbox=0,50,10,60&width=100&height=100&format=image/png&"
                    + sld));

        Assert.Equal("OperationNotSupported", exception.Code);
    }

    [Fact]
    public async Task Get_capabilities_serves_the_111_dialect_on_request()
    {
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676));

        var (_, body) = await DispatchAsync(map, services, "?service=WMS&request=GetCapabilities&version=1.1.1");
        var document = XDocument.Parse(body);

        Assert.Equal("WMS_Capabilities", document.Root!.Name.LocalName);
        Assert.Equal(XNamespace.None, document.Root!.Name.Namespace);
        Assert.Equal("1.1.1", document.Root!.Attribute("version")!.Value);
        Assert.NotNull(document.DocumentType);
        Assert.Contains("1.1.1", document.DocumentType!.SystemId);
        Assert.Empty(document.Descendants(XNamespace.None + "CRS"));
        Assert.Equal(
            ["EPSG:3857", "EPSG:4326"],
            document.Descendants(XNamespace.None + "SRS").Select(element => element.Value).Distinct().OrderBy(value => value).ToArray());
        var box = document.Descendants(XNamespace.None + "LatLonBoundingBox").Single();
        Assert.Equal("4.9041", box.Attribute("minx")!.Value);
        Assert.Equal("52.3676", box.Attribute("miny")!.Value);
        Assert.Empty(document.Descendants(XNamespace.None + "EX_GeographicBoundingBox"));
        var style = document.Descendants(XNamespace.None + "Style").Single();
        Assert.Equal("default", style.Element(XNamespace.None + "Name")!.Value);
        Assert.NotNull(style.Element(XNamespace.None + "LegendURL"));
    }

    [Fact]
    public async Task Get_capabilities_rejects_an_unknown_version()
    {
        var map = OgcFixtures.Map();
        var (services, _) = OgcFixtures.Build(map);

        var exception = await Assert.ThrowsAsync<OgcServiceException>(
            () => DispatchAsync(map, services, "?service=WMS&request=GetCapabilities&version=9.9.9"));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }

    [Fact]
    public async Task Get_capabilities_without_a_version_stays_on_the_130_dialect()
    {
        // QGIS add-layer GetCapabilities carries no VERSION
        // (tests/fixtures/qgis/qgis-4.2.2-wms.json): it must keep 1.3.0.
        var map = OgcFixtures.Map();
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676));

        var (_, body) = await DispatchAsync(map, services, "?service=WMS&request=GetCapabilities");
        var document = XDocument.Parse(body);

        Assert.Equal("1.3.0", document.Root!.Attribute("version")!.Value);
        Assert.NotEmpty(document.Descendants(Wms130 + "CRS"));
    }

    [Fact]
    public async Task Get_map_forwards_the_qgis_dpi_triple_to_the_renderer()
    {
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer);

        await DispatchAsync(
            map,
            services,
            "?service=WMS&request=GetMap&version=1.3.0&layers=Cities&crs=CRS:84"
                + "&bbox=0,50,10,60&width=100&height=100&format=image/png"
                + "&DPI=192&MAP_RESOLUTION=192&FORMAT_OPTIONS=dpi:192");

        Assert.NotNull(renderer.LastRequest);
        Assert.Equal(192, renderer.LastRequest.Dpi);
        Assert.Equal(100, renderer.LastRequest.Viewport.Width);
        Assert.Equal(100, renderer.LastRequest.Viewport.Height);
    }

    [Fact]
    public async Task Get_map_defaults_to_96_dpi()
    {
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer);

        await DispatchAsync(
            map,
            services,
            "?service=WMS&request=GetMap&version=1.3.0&layers=Cities&crs=CRS:84"
                + "&bbox=0,50,10,60&width=100&height=100&format=image/png");

        Assert.NotNull(renderer.LastRequest);
        Assert.Equal(96, renderer.LastRequest.Dpi);
    }

    [Theory]
    [InlineData("MAP_RESOLUTION=180", 180)]
    [InlineData("FORMAT_OPTIONS=dpi:180", 180)]
    public async Task Get_map_reads_each_dpi_source_alone(string knob, double expected)
    {
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer);

        await DispatchAsync(
            map,
            services,
            "?service=WMS&request=GetMap&version=1.3.0&layers=Cities&crs=CRS:84"
                + $"&bbox=0,50,10,60&width=100&height=100&format=image/png&{knob}");

        Assert.NotNull(renderer.LastRequest);
        Assert.Equal(expected, renderer.LastRequest.Dpi);
    }

    [Fact]
    public async Task Get_map_prefers_dpi_over_the_other_sources()
    {
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer);

        await DispatchAsync(
            map,
            services,
            "?service=WMS&request=GetMap&version=1.3.0&layers=Cities&crs=CRS:84"
                + "&bbox=0,50,10,60&width=100&height=100&format=image/png"
                + "&DPI=192&MAP_RESOLUTION=90&FORMAT_OPTIONS=dpi:90");

        Assert.NotNull(renderer.LastRequest);
        Assert.Equal(192, renderer.LastRequest.Dpi);
    }

    [Theory]
    [InlineData("DPI=abc")]
    [InlineData("DPI=0")]
    [InlineData("DPI=-72")]
    [InlineData("MAP_RESOLUTION=abc")]
    public async Task Get_map_rejects_a_malformed_dpi(string knob)
    {
        var map = OgcFixtures.Map();
        var (services, _) = OgcFixtures.Build(map);

        var exception = await Assert.ThrowsAsync<OgcServiceException>(
            () => DispatchAsync(
                map,
                services,
                "?service=WMS&request=GetMap&version=1.3.0&layers=Cities&crs=CRS:84"
                    + $"&bbox=0,50,10,60&width=100&height=100&format=image/png&{knob}"));

        Assert.Equal("InvalidParameterValue", exception.Code);
    }

    [Fact]
    public async Task Get_legend_graphic_forwards_dpi_to_the_renderer()
    {
        var renderer = new CapturingRenderer();
        var map = OgcFixtures.Map();
        var services = MapServices(map, renderer);

        await DispatchAsync(
            map,
            services,
            "?service=WMS&version=1.3.0&sld_version=1.1.0&request=GetLegendGraphic"
                + "&format=image/png&layer=Cities&style=&transparent=true&DPI=192");

        Assert.NotNull(renderer.LastRequest);
        Assert.Equal(192, renderer.LastRequest.Dpi);
        Assert.Equal(WmsService.LegendWidth, renderer.LastRequest.Viewport.Width);
        Assert.Equal(WmsService.LegendHeight, renderer.LastRequest.Viewport.Height);
    }

    private static OgcRequestServices MapServices(Spatial.PluginSdk.Providers.Map map, CapturingRenderer renderer)
    {
        var store = new OgcFixtures.FakeStore(OgcFixtures.Dataset);
        return new OgcRequestServices(
            new OgcFixtures.FakeStoreRegistry(store),
            new OgcFixtures.FakeRegistry(map),
            renderer,
            new OgcFixtures.IdentityTransforms());
    }

    private sealed class CapturingRenderer : IMapRenderer
    {
        public MapRenderRequest? LastRequest { get; private set; }

        public Task<RasterImage> RenderAsync(MapRenderRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(
                new RasterImage([1, 2, 3], "image/png", request.Viewport.Width, request.Viewport.Height, RasterFormat.Png));
        }
    }

    private static async Task<(string? ContentType, string Body)> DispatchAsync(
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
}
