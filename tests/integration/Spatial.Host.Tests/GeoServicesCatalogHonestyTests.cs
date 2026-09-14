using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-049 catalog honesty + directory scope: the services listing only
/// advertises served types (the G1 ground truth lists GPServer entries we
/// correctly omit), and <c>f=html</c> — the Services Directory default — is
/// a typed <c>invalid.arguments</c> failure naming the JSON surface
/// (ADR-0035) across the catalog and every served root.
/// </summary>
public sealed class GeoServicesCatalogHonestyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string[] ServedTypes = ["GeometryServer", "FeatureServer", "MapServer", "ImageServer"];

    private readonly HttpClient _client;

    public GeoServicesCatalogHonestyTests(WebApplicationFactory<Program> factory) => _client = factory.CreateClient();

    [Fact]
    public async Task The_catalog_advertises_only_served_types()
    {
        var services = await ServicesAsync("/arcgis/rest/services?f=json");

        Assert.NotEmpty(services);
        Assert.All(services, service => Assert.Contains(service.Type, ServedTypes));
    }

    [Fact]
    public async Task The_sampleserver6_replay_omits_unserved_types()
    {
        var groundTruth = GroundTruthCatalog();
        var groundTypes = groundTruth.EnumerateArray()
            .Select(service => service.GetProperty("type").GetString() ?? string.Empty)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Non-vacuous: the ground truth really does list GPServer entries.
        Assert.Contains("GPServer", groundTypes);
        var unserved = groundTypes.Except(ServedTypes, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(unserved);

        var services = await ServicesAsync("/arcgis/rest/services?f=json");

        Assert.DoesNotContain(services, service => unserved.Contains(service.Type, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("/arcgis/rest/services?f=html")]
    [InlineData("/arcgis/rest/services/Geometry/GeometryServer?f=html")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer?f=html")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer/0?f=html")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer/0/query?where=1%3D1&outFields=*&f=html")]
    [InlineData("/arcgis/rest/services/demo/FeatureServer/0/1?f=html")]
    public async Task Html_is_rejected_naming_the_json_surface(string path)
    {
        var response = await _client.GetAsync(path);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        var message = error.GetProperty("message").GetString() ?? string.Empty;
        Assert.Contains("supportedQueryFormats", message, StringComparison.Ordinal);
        Assert.Contains("f=json", message, StringComparison.Ordinal);
    }

    private async Task<(string Name, string Type)[]> ServicesAsync(string path)
    {
        var response = await _client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement
            .GetProperty("services").EnumerateArray()
            .Select(service => (
                service.GetProperty("name").GetString() ?? string.Empty,
                service.GetProperty("type").GetString() ?? string.Empty))
            .ToArray();
    }

    private static JsonElement GroundTruthCatalog()
    {
        var directory = AppContext.BaseDirectory;
        for (var depth = 0; depth < 12; depth++)
        {
            var candidate = Path.Combine(directory, "research", "compat", "ground-truth", "catalog.sampleserver6.json");
            if (File.Exists(candidate))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(candidate));
                return document.RootElement.GetProperty("body").GetProperty("services").Clone();
            }

            directory = Path.GetDirectoryName(directory)
                ?? throw new DirectoryNotFoundException("Cannot locate the repository root for the ground-truth replay.");
        }

        throw new FileNotFoundException("research/compat/ground-truth/catalog.sampleserver6.json was not found.");
    }
}
