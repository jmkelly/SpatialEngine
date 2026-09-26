using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// The workbench Parity page contract over a Census-shaped dataset (the
/// <c>tests/fixtures/census/census-states.geojson</c> stand-in for the Esri
/// <c>Census</c> MapServer <c>states</c> layer): the exact localhost URL
/// shapes the page builds — <c>MapServer/export?f=image</c>,
/// <c>FeatureServer/0/query</c> count-only and sample with the parity bbox
/// envelope (<c>-125,25,-66,50</c>, WKID 4326) — return real data instead of
/// errors, so the localhost panels can stand beside the public Esri ones.
/// </summary>
public sealed class ParityCensusTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";

    /// <summary>The Parity page defaults both panels share (see <c>parity.ts</c>).</summary>
    private const string Bbox = "-125,25,-66,50";
    private const string Sr = "4326";

    private const string FillStyle =
        """[{"type":"fill","layout":{"visibility":"visible"},"paint":{"fill-color":"#3d7ea6","fill-opacity":0.35,"fill-outline-color":"#3d7ea6"}}]""";

    private static readonly string[] FeatureAndMapServices = ["feature", "map"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-parity-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eng", "verify.sh")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root (eng/verify.sh) from the test output.");
        return directory.FullName;
    }

    private WebApplicationFactory<Program> Factory() =>
        new ParityFactory(Path.Combine(_directory, "maps.json"));

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>Ingests the census fixture and publishes it as one map exposing both servers.</summary>
    private static async Task<HttpClient> CensusServiceAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var geojson = await File.ReadAllTextAsync(
            Path.Combine(RepoRoot(), "tests", "fixtures", "census", "census-states.geojson"));

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=census.states&srid=4326&format=geojson&identity=none",
            new StringContent(geojson, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        Assert.Equal(2, (await BodyAsync(ingest)).GetProperty("features").GetInt64());

        var map = JsonSerializer.Serialize(new
        {
            name = "census",
            store = "memory",
            services = FeatureAndMapServices,
            layers = new[] { new { dataset = "census.states", layerId = 0, name = "States", style = FillStyle } },
        });
        var publish = await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/maps/census", new StringContent(map, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);
        return client;
    }

    [Fact]
    public async Task The_parity_map_export_renders_census_bytes()
    {
        using var factory = Factory();
        var client = await CensusServiceAsync(factory);

        // The exact MapServer/export shape parity.ts builds (f=image bytes).
        var export = await client.GetAsync(
            $"{Root}/census/MapServer/export?bbox={Bbox}&bboxSR={Sr}&imageSR={Sr}&size=800,600&format=png&f=image");

        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal("image/png", export.Content.Headers.ContentType?.MediaType);
        Assert.True((await export.Content.ReadAsByteArrayAsync()).Length > 64);
    }

    [Fact]
    public async Task The_parity_feature_count_covers_both_states()
    {
        using var factory = Factory();
        var client = await CensusServiceAsync(factory);

        // The exact count-only shape parity.ts builds for the bbox envelope.
        var query = $"where={Uri.EscapeDataString("1=1")}&returnCountOnly=true"
            + $"&geometry={Uri.EscapeDataString(Bbox)}&geometryType=esriGeometryEnvelope"
            + $"&spatialRel=esriSpatialRelIntersects&inSR={Sr}&f=json";
        var count = await BodyAsync(await client.GetAsync($"{Root}/census/FeatureServer/0/query?{query}"));

        Assert.Equal(2, count.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task The_parity_feature_sample_returns_the_state_attributes()
    {
        using var factory = Factory();
        var client = await CensusServiceAsync(factory);

        // The exact sample shape parity.ts builds: no geometry, capped page.
        var query = $"where={Uri.EscapeDataString("1=1")}&outFields=*&returnGeometry=false&resultRecordCount=20"
            + $"&geometry={Uri.EscapeDataString(Bbox)}&geometryType=esriGeometryEnvelope"
            + $"&spatialRel=esriSpatialRelIntersects&inSR={Sr}&f=json";
        var sample = await BodyAsync(await client.GetAsync($"{Root}/census/FeatureServer/0/query?{query}"));

        var names = sample.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("NAME").GetString() ?? string.Empty)
            .Order()
            .ToArray();
        Assert.Equal(["California", "Texas"], names);
    }

    private sealed class ParityFactory(string mapsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
