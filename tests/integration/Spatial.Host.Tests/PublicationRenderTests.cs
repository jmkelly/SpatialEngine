using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The publication render route (ADR-0047): a named publication renders its
/// datasets with the per-layer styles persisted on it, so a style authored
/// headlessly (PUT) takes effect without a client-side style document.
/// </summary>
public sealed class PublicationRenderTests : IDisposable
{
    private const string Token = "test-admin-token";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-publication-render-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private WebApplicationFactory<Program> Factory() => new RenderFactory(Path.Combine(_directory, "publications.json"));

    private static async Task PutPublicationAsync(HttpClient client, string name, string? style)
    {
        var body = JsonSerializer.Serialize(new
        {
            name,
            kind = "map",
            store = "demo",
            layers = new[] { new { dataset = "demo.cities", layerId = 0, style } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/publications/{name}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpResponseMessage> RenderAsync(HttpClient client, string name) =>
        client.PostAsJsonAsync($"/api/publications/{name}/render", new
        {
            viewport = new { minX = -10d, minY = 35d, maxX = 30d, maxY = 60d, width = 400, height = 250, crs = "EPSG:4326" },
        });

    [Fact]
    public async Task Renders_a_publication_with_its_persisted_style()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutPublicationAsync(
            client,
            "styled",
            """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":30,"circle-opacity":1.0}}]""");

        var response = await RenderAsync(client, "styled");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.True(int.Parse(response.Headers.GetValues("X-Raster-Width").Single(), CultureInfo.InvariantCulture) > 0);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.Length > 8);
    }

    [Fact]
    public async Task Renders_a_layer_without_a_persisted_style_using_defaults()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutPublicationAsync(client, "plain", style: null);

        var response = await RenderAsync(client, "plain");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task The_persisted_style_changes_the_rendered_image()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();
        await PutPublicationAsync(client, "plain", style: null);
        await PutPublicationAsync(
            client,
            "loud",
            """[{"type":"circle","layout":{"visibility":"visible"},"paint":{"circle-color":"#ff0000","circle-radius":40,"circle-opacity":1.0}},{"type":"background","paint":{"background-color":"#000000"}}]""");

        var plain = await (await RenderAsync(client, "plain")).Content.ReadAsByteArrayAsync();
        var loud = await (await RenderAsync(client, "loud")).Content.ReadAsByteArrayAsync();

        Assert.False(plain.SequenceEqual(loud), "the persisted style must reach the renderer");
    }

    [Fact]
    public async Task An_unknown_publication_is_not_found()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await RenderAsync(client, "absent");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("not.found", body.GetProperty("code").GetString());
    }

    /// <summary>A host with an admin token and a per-test publication file.</summary>
    private sealed class RenderFactory(string publicationsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Publications:Path", publicationsPath);
        }
    }
}
