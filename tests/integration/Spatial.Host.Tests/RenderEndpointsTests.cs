using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The raster render routes (ADR-0044): a styled export served as image
/// bytes, capability discovery, and the failure mapping for unsupported
/// formats, unknown datasets and resource caps.
/// </summary>
public sealed class RenderEndpointsTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public RenderEndpointsTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Capabilities_describe_the_configured_pipeline()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/render/capabilities");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Capabilities>();
        Assert.Contains("png", body!.Formats);
        Assert.Equal(16_777_216, body.MaxPixels);
        Assert.Contains("over", body.BlendModes);
    }

    [Fact]
    public async Task Render_returns_a_png_for_a_circle_layer()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/render", CircleRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 8);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal((byte)'P', bytes[1]);
    }

    [Fact]
    public async Task Render_returns_jpeg_when_asked()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/render", CircleRequest(format: "jpeg"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Render_rejects_an_unconfigured_format()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/render", CircleRequest(format: "tiff"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", await CodeAsync(response));
    }

    [Fact]
    public async Task Render_maps_an_unknown_dataset_to_404()
    {
        using var client = _factory.CreateClient();

        var request = CircleRequest(dataset: "demo.absent");
        var response = await client.PostAsJsonAsync("/api/render", request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not.found", await CodeAsync(response));
    }

    [Fact]
    public async Task Render_maps_an_unknown_store_to_400()
    {
        using var client = _factory.CreateClient();

        var request = CircleRequest();
        request.Layers[0].Store = "warehouse";
        var response = await client.PostAsJsonAsync("/api/render", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", await CodeAsync(response));
    }

    [Fact]
    public async Task Render_rejects_an_invalid_viewport()
    {
        using var client = _factory.CreateClient();

        var request = CircleRequest();
        request.Viewport.MaxX = request.Viewport.MinX - 1;
        var response = await client.PostAsJsonAsync("/api/render", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_enforces_the_pixel_cap()
    {
        using var client = _factory.CreateClient();

        var request = CircleRequest();
        request.Viewport.Width = 20_000;
        request.Viewport.Height = 20_000;
        var response = await client.PostAsJsonAsync("/api/render", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Render_reports_an_unknown_imagery_source()
    {
        using var client = _factory.CreateClient();

        var request = CircleRequest();
        request.Imagery = [new Imagery { Source = "basemap" }];
        var response = await client.PostAsJsonAsync("/api/render", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", await CodeAsync(response));
    }

    private static async Task<string?> CodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<ErrorBody>();
        return body?.Code;
    }

    private static RenderRequest CircleRequest(string format = "png", string dataset = "demo.cities") => new()
    {
        Viewport = new Viewport { MinX = -10, MinY = 35, MaxX = 30, MaxY = 60, Width = 400, Height = 250, Crs = "EPSG:4326" },
        Style = JsonDocument.Parse(
            $$"""
            { "version": 8, "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#101820" } },
                { "id": "cities", "type": "circle", "source-layer": "{{dataset}}",
                  "paint": { "circle-color": "#ffd166", "circle-radius": 6 } } ] }
            """).RootElement.Clone(),
        Layers = [new Layer { Dataset = dataset, Store = "demo" }],
        Format = format,
    };

    private sealed class RenderRequest
    {
        public Viewport Viewport { get; set; } = new();

        public JsonElement Style { get; set; }

        public List<Layer> Layers { get; set; } = [];

        public List<Imagery>? Imagery { get; set; }

        public string Format { get; set; } = "png";
    }

    private sealed class Viewport
    {
        public double MinX { get; set; }

        public double MinY { get; set; }

        public double MaxX { get; set; }

        public double MaxY { get; set; }

        public int Width { get; set; }

        public int Height { get; set; }

        public string Crs { get; set; } = "EPSG:4326";
    }

    private sealed class Layer
    {
        public string Dataset { get; set; } = string.Empty;

        public string? Store { get; set; }

        public string? Filter { get; set; }
    }

    private sealed class Imagery
    {
        public string Source { get; set; } = string.Empty;
    }

    private sealed record Capabilities(IReadOnlyList<string> Formats, IReadOnlyList<string> BlendModes, long MaxPixels);

    private sealed record ErrorBody(string Code, string Message);
}
