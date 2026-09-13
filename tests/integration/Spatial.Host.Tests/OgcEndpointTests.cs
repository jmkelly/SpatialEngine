using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The OGC WMS/WFS boundary adapter (ADR-0053 §3): maps that expose the Wms
/// or Wfs service answer capabilities, render/query and report failures as
/// <c>ServiceExceptionReport</c>; maps that do not, or unknown names, are
/// 404.
/// </summary>
public sealed class OgcEndpointTests : IDisposable
{
    private const string Token = "test-admin-token";

    private const string Style =
        """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":8}}]""";

    private static readonly XNamespace Wms = "http://www.opengis.net/wms";
    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs/2.0";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-ogc-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WebApplicationFactory<Program> Factory() => new OgcFactory(Path.Combine(_directory, "maps.json"));

    private static async Task<HttpClient> MapAsync(WebApplicationFactory<Program> factory, string name, params string[] services) =>
        await PutMapAsync(factory, name, services, new[] { new { dataset = "demo.cities", layerId = 0, name = "cities", style = Style } });

    private static async Task<HttpClient> Map2Async(WebApplicationFactory<Program> factory, string name, params string[] services) =>
        await PutMapAsync(factory, name, services, new[]
        {
            new { dataset = "demo.cities", layerId = 0, name = "cities", style = Style },
            new { dataset = "demo.points", layerId = 1, name = "towns", style = Style },
        });

    private static async Task<HttpClient> PutMapAsync(WebApplicationFactory<Program> factory, string name, string[] services, object layers)
    {
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name,
            store = "demo",
            services,
            layers,
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/maps/{name}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private static async Task<XDocument> XmlAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return XDocument.Parse(text);
    }

    private static async Task<string> ReportCodeAsync(HttpResponseMessage response)
    {
        var document = await XmlAsync(response);
        Assert.Equal("ServiceExceptionReport", document.Root!.Name.LocalName);
        return document.Descendants(Wms + "ServiceException").Single().Attribute("code")!.Value;
    }

    [Fact]
    public async Task Wms_get_capabilities_returns_the_service_document()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync("/ogc/world/wms?service=WMS&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var document = await XmlAsync(response);
        Assert.Equal("WMS_Capabilities", document.Root!.Name.LocalName);
        Assert.Contains("cities", document.Descendants(Wms + "Name").Select(element => element.Value));
    }

    [Fact]
    public async Task Wms_get_map_renders_the_requested_bbox()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png&transparent=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, bytes[0]);
    }

    [Fact]
    public async Task Wms_get_feature_info_returns_text_and_json()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");
        const string Base =
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=49&j=76";

        var text = await client.GetAsync(Base + "&info_format=text/plain");
        var json = await client.GetAsync(Base + "&info_format=application/json");

        Assert.Equal(HttpStatusCode.OK, text.StatusCode);
        Assert.Equal("text/plain", text.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Amsterdam", await text.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, json.StatusCode);
        Assert.Equal("application/json", json.Content.Headers.ContentType?.MediaType);
        var feature = JsonDocument.Parse(await json.Content.ReadAsStringAsync()).RootElement
            .GetProperty("features")[0];
        Assert.Equal("Amsterdam", feature.GetProperty("properties").GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("text/html", "text/html")]
    [InlineData("text/xml", "text/xml")]
    [InlineData("application/vnd.ogc.gml", "application/vnd.ogc.gml")]
    public async Task Wms_get_feature_info_serves_each_info_format(string infoFormat, string mediaType)
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=49&j=76&info_format="
            + Uri.EscapeDataString(infoFormat));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Amsterdam", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wms_get_feature_info_rejects_an_unknown_info_format()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=49&j=76&info_format=UnknownFormat");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidFormat", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_get_feature_info_identifies_a_click_within_the_marker()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // The cities style paints an 8px circle; Amsterdam is at (4.9041, 52.3676)
        // and pixel column 49. A click six pixels east (i=55) is still inside the
        // rendered marker and must identify the city, not report an empty result.
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=55&j=76&info_format=text/plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Amsterdam", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wms_get_feature_info_misses_a_click_outside_the_marker()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // The 85cabcc band: the identify tolerance is the rendered marker
        // radius (8px) plus half a pixel, i.e. 0.85 viewport units here.
        // Amsterdam is at (4.9041, 52.3676); pixel column 40 centres on
        // x=4.05, just outside the box, so the click must miss while the
        // neighbouring column 41 (x=4.15) still hits.
        var miss = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=40&j=76&info_format=text/plain");
        var hit = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=41&j=76&info_format=text/plain");

        Assert.Equal(HttpStatusCode.OK, miss.StatusCode);
        Assert.DoesNotContain("Amsterdam", await miss.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, hit.StatusCode);
        Assert.Contains("Amsterdam", await hit.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wms_epsg4326_bbox_is_latitude_first()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // minLat,minLon,maxLat,maxLon is the same viewport as CRS:84's 0,50,10,60.
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=EPSG:4326&bbox=50,0,60,10&width=100&height=100&i=49&j=76&info_format=text/plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Amsterdam", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wms_get_map_rejects_an_unknown_format()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=UnknownFormat");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidFormat", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_get_feature_info_rejects_a_non_numeric_pixel()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=abc&j=10&info_format=text/plain");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidPoint", await ReportCodeAsync(response));
    }

    [Theory]
    [InlineData("10,10,5,5")]
    [InlineData("5,5,5,10")]
    public async Task Wms_get_map_rejects_a_degenerate_bbox(string bbox)
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            $"/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities&crs=CRS:84&bbox={bbox}&width=200&height=200&format=image/png");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ServiceExceptionReport", (await XmlAsync(response)).Root!.Name.LocalName);
    }

    [Fact]
    public async Task Wms_capabilities_advertise_a_default_style_per_layer()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync("/ogc/world/wms?service=WMS&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await XmlAsync(response);
        var layer = document.Descendants(Wms + "Layer")
            .First(element => element.Element(Wms + "Name")?.Value == "cities");
        Assert.Equal("default", layer.Element(Wms + "Style")?.Element(Wms + "Name")?.Value);
    }

    [Fact]
    public async Task Wms_get_map_accepts_the_advertised_default_style()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities&styles=default&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wms_get_map_accepts_an_empty_styles_value()
    {
        using var factory = Factory();
        var client = await Map2Async(factory, "world", "wms");

        // QGIS sends one empty STYLES for an all-default selection even over
        // several layers; the count is lenient but every layer still renders.
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities,towns&styles=&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wms_get_map_rejects_a_styles_count_mismatch()
    {
        using var factory = Factory();
        var client = await Map2Async(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities,towns&styles=s1&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("StyleNotDefined", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_get_map_inimage_returns_an_image_on_failure()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // CITE getmap:exceptions-inimage-mime and the MapServer EXCEPTIONS=INIMAGE trace.
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=ghost&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png&exceptions=INIMAGE");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, bytes[0]);
    }

    [Fact]
    public async Task Wms_get_map_inimage_accepts_the_mime_vocabulary()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=ghost&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png&exceptions="
            + Uri.EscapeDataString("application/vnd.ogc.se_inimage"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wms_get_map_blank_returns_a_transparent_image()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // CITE getmap:exceptions-blank-transparent.
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=ghost&crs=CRS:84&bbox=-10,35,30,60&width=64&height=64&format=image/png&transparent=TRUE&exceptions=BLANK");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        using var bitmap = SkiaSharp.SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.NotNull(bitmap);
        Assert.Equal(64, bitmap.Width);
        Assert.Equal(64, bitmap.Height);
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                Assert.Equal(0, bitmap.GetPixel(x, y).Alpha);
            }
        }
    }

    [Theory]
    [InlineData("XML")]
    [InlineData("application/vnd.ogc.se_xml")]
    public async Task Wms_get_map_xml_exceptions_stay_xml(string exceptions)
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=ghost&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png&exceptions="
            + Uri.EscapeDataString(exceptions));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LayerNotDefined", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_get_legend_graphic_renders_the_layer_style()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // The QGIS layer-tree request shape (research/interop/wms-conformance.md trace C).
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&version=1.3.0&sld_version=1.1.0&request=GetLegendGraphic&format=image/png&layer=cities&style=&transparent=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(0x89, bytes[0]);
    }

    [Fact]
    public async Task Wms_get_legend_graphic_requires_a_layer()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&version=1.3.0&request=GetLegendGraphic&format=image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MissingParameterValue", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_get_legend_graphic_rejects_an_unknown_layer()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&version=1.3.0&request=GetLegendGraphic&format=image/png&layer=ghost&style=");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LayerNotDefined", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_get_legend_graphic_rejects_an_unknown_style()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&version=1.3.0&request=GetLegendGraphic&format=image/png&layer=cities&style=bogus");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("StyleNotDefined", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_capabilities_advertise_a_legend_url_per_style()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync("/ogc/world/wms?service=WMS&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await XmlAsync(response);
        var layer = document.Descendants(Wms + "Layer")
            .First(element => element.Element(Wms + "Name")?.Value == "cities");
        var href = layer.Element(Wms + "Style")?.Element(Wms + "LegendURL")
            ?.Elements().FirstOrDefault(element => element.Name.LocalName == "OnlineResource")
            ?.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "href")?.Value;
        Assert.Contains("GetLegendGraphic", href);
        Assert.Contains("layer=cities", href);
    }

    [Fact]
    public async Task Wms_get_map_rejects_an_unknown_style()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities&styles=bogus&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("StyleNotDefined", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_get_feature_info_rejects_an_unknown_query_layer()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=ghost&crs=CRS:84&bbox=0,50,10,60&width=100&height=100&i=49&j=76&info_format=text/plain");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("LayerNotDefined", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wms_111_epsg4326_bbox_stays_x_first()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // The same geography: 1.1.1 lon/lat via SRS versus 1.3.0 lat/lon via CRS.
        var legacy = await client.GetAsync(
            "/ogc/world/wms?service=WMS&version=1.1.1&request=GetMap&srs=EPSG:4326&bbox=-10,35,30,60&format=image/png&width=200&height=200&styles=&layers=cities");
        var current = await client.GetAsync(
            "/ogc/world/wms?service=WMS&version=1.3.0&request=GetMap&crs=EPSG:4326&bbox=35,-10,60,30&format=image/png&width=200&height=200&styles=&layers=cities");

        Assert.Equal(HttpStatusCode.OK, legacy.StatusCode);
        Assert.Equal(HttpStatusCode.OK, current.StatusCode);
        Assert.Equal(await current.Content.ReadAsByteArrayAsync(), await legacy.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Wms_get_map_without_a_version_is_a_typed_report()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&layers=cities&crs=CRS:84&bbox=-10,35,30,60&width=200&height=200&format=image/png");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MissingParameterValue", await ReportCodeAsync(response));
    }

    [Theory]
    [InlineData("5,10,5,5")]
    [InlineData("5,5,10,5")]
    public async Task Wms_get_map_rejects_an_inverted_y_bbox(string bbox)
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // CITE getmap:bbox-miny-gt-maxy / bbox-miny-eq-maxy.
        var response = await client.GetAsync(
            $"/ogc/world/wms?service=WMS&request=GetMap&version=1.3.0&layers=cities&crs=CRS:84&bbox={bbox}&width=200&height=200&format=image/png");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ServiceExceptionReport", (await XmlAsync(response)).Root!.Name.LocalName);
    }

    [Fact]
    public async Task Wms_111_gdal_shaped_get_map_still_renders()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // The GDAL 1.1.1 KVP shape (research/interop/wms-conformance.md trace E):
        // SRS (not CRS), lon/lat bbox, trailing empty STYLES. T-031's
        // VERSION gating must keep this traffic rendering.
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetMap&version=1.1.1&layers=cities&styles=&srs=EPSG:4326&bbox=-10,35,30,60&format=image/png&width=200&height=200");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Wms_get_feature_info_without_a_version_keeps_the_130_reading()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        // GetMap without VERSION is MissingParameterValue, but GetFeatureInfo
        // stays lenient and reads the bbox the 1.3.0 way.
        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=EPSG:4326&bbox=50,0,60,10&width=100&height=100&i=49&j=76&info_format=text/plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Amsterdam", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wms_get_feature_info_rejects_a_degenerate_bbox()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync(
            "/ogc/world/wms?service=WMS&request=GetFeatureInfo&query_layers=cities&crs=CRS:84&bbox=10,10,5,5&width=100&height=100&i=49&j=76&info_format=text/plain");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ServiceExceptionReport", (await XmlAsync(response)).Root!.Name.LocalName);
    }

    [Fact]
    public async Task Wms_accepts_a_form_post()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.PostAsync(
            "/ogc/world/wms",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SERVICE"] = "WMS",
                ["REQUEST"] = "GetCapabilities",
            }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("WMS_Capabilities", (await XmlAsync(response)).Root!.Name.LocalName);
    }

    [Fact]
    public async Task An_unknown_map_or_disabled_service_is_a_not_found_report()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "draft");

        var absent = await client.GetAsync("/ogc/absent/wms?service=WMS&request=GetCapabilities");
        var disabled = await client.GetAsync("/ogc/draft/wms?service=WMS&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        Assert.Equal("LayerNotDefined", await ReportCodeAsync(absent));
        Assert.Equal(HttpStatusCode.NotFound, disabled.StatusCode);
        Assert.Equal("LayerNotDefined", await ReportCodeAsync(disabled));
    }

    [Fact]
    public async Task A_missing_parameter_is_a_typed_report()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wms");

        var response = await client.GetAsync("/ogc/world/wms?service=WMS&request=GetMap&layers=cities");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("MissingParameterValue", await ReportCodeAsync(response));
    }

    [Fact]
    public async Task Wfs_get_capabilities_lists_the_feature_type()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wfs");

        var response = await client.GetAsync("/ogc/world/wfs?service=WFS&request=GetCapabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await XmlAsync(response);
        Assert.Equal("WFS_Capabilities", document.Root!.Name.LocalName);
        Assert.Equal("cities", document.Descendants(Wfs + "FeatureType").Single().Element(Wfs + "Name")!.Value);
    }

    [Fact]
    public async Task Wfs_describe_feature_type_returns_an_xsd()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wfs");

        var response = await client.GetAsync("/ogc/world/wfs?service=WFS&request=DescribeFeatureType&typeNames=cities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("citiesType", text);
        Assert.Contains("xsd:string", text);
    }

    [Fact]
    public async Task Wfs_get_feature_returns_geojson()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wfs");

        var response = await client.GetAsync("/ogc/world/wfs?service=WFS&request=GetFeature&TYPENAMES=cities&count=5&bbox=0,50,10,60");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/geo+json", response.Content.Headers.ContentType?.MediaType);
        var features = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("features").EnumerateArray().ToArray();
        var feature = Assert.Single(features);
        Assert.Equal("Amsterdam", feature.GetProperty("properties").GetProperty("name").GetString());
        Assert.Equal("Point", feature.GetProperty("geometry").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Wfs_rejects_gml_output()
    {
        using var factory = Factory();
        var client = await MapAsync(factory, "world", "wfs");

        var response = await client.GetAsync(
            "/ogc/world/wfs?service=WFS&request=GetFeature&typeNames=cities&outputFormat=application/gml+xml");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("InvalidParameterValue", await ReportCodeAsync(response));
    }

    /// <summary>A host with an admin token and a per-test map file.</summary>
    private sealed class OgcFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
