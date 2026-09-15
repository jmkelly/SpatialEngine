using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// T-038 items 2–5 over the demo store: the Feature Service per-layer
/// <c>generateRenderer</c> (reusing the T-039 classifier), <c>validateSQL</c>,
/// the honestly rejected aggregation extensions (<c>queryBins</c>,
/// <c>queryTopFeatures</c>, <c>queryAnalytic</c>), and the attachment surface
/// without a blob face (T-061 served behaviour when the store exposes no face;
/// the store-backed behaviour lives in GeoServicesAttachmentsTests).
/// Red-first: none of these routes exist today, so every test here 404s
/// while the surface is unmounted.
/// </summary>
public sealed class GeoServicesFeatureOpsTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Feature = $"{Root}/demo/FeatureServer";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-feature-ops-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesFeatureOpsTests() => _factory = new OpsFactory(Path.Combine(_directory, "publications.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private HttpClient Client() => _factory.CreateClient();

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
    }

    // ---- item 2: generateRenderer (served by the T-039 reuse) ----

    [Fact]
    public async Task Generate_renderer_classifies_equal_interval_breaks()
    {
        var classification = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"population","breakCount":2,"classificationMethod":"esriClassifyEqualInterval"}""");

        var renderer = await BodyAsync(await Client().GetAsync(
            $"{Feature}/0/generateRenderer?f=json&classificationDef={classification}"));

        var body = renderer.GetProperty("renderer");
        Assert.Equal("classBreaks", body.GetProperty("type").GetString());
        Assert.Equal("population", body.GetProperty("field").GetString());
        Assert.Equal(2, body.GetProperty("classBreakInfos").GetArrayLength());
    }

    [Fact]
    public async Task Generate_renderer_enumerates_unique_values()
    {
        var classification = Uri.EscapeDataString(
            """{"type":"uniqueValueDef","uniqueValueFields":["name"]}""");

        var renderer = await BodyAsync(await Client().GetAsync(
            $"{Feature}/0/generateRenderer?f=json&classificationDef={classification}"));

        Assert.Equal("uniqueValue", renderer.GetProperty("renderer").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Generate_renderer_without_a_classification_is_a_typed_error()
    {
        var error = await ErrorAsync(
            await Client().GetAsync($"{Feature}/0/generateRenderer?f=json"), HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("classificationDef", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_renderer_for_an_unknown_layer_is_not_found()
    {
        var classification = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"population","breakCount":2}""");

        await ErrorAsync(
            await Client().GetAsync($"{Feature}/99/generateRenderer?f=json&classificationDef={classification}"),
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Generate_renderer_matches_the_map_service_classifier()
    {
        // The T-039 reuse proof: the same classification over the same data
        // returns the same renderer on both surfaces.
        var client = await MapServiceAsync();
        var classification = Uri.EscapeDataString(
            """{"type":"classBreaksDef","classificationField":"population","breakCount":3,"classificationMethod":"esriClassifyEqualInterval"}""");

        var feature = await BodyAsync(await client.GetAsync(
            $"{Feature}/0/generateRenderer?f=json&classificationDef={classification}"));
        var map = await BodyAsync(await client.GetAsync(
            $"{Root}/world/MapServer/0/generateRenderer?f=json&classificationDef={classification}"));

        Assert.Equal(
            JsonSerializer.Serialize(map.GetProperty("renderer")),
            JsonSerializer.Serialize(feature.GetProperty("renderer")));
    }

    // ---- item 3: validateSQL ----

    [Fact]
    public async Task Validate_sql_accepts_a_supported_where_clause()
    {
        var body = await BodyAsync(await Client().GetAsync(
            $"{Feature}/0/validateSQL?f=json&sql=" + Uri.EscapeDataString("population > 1000000")));

        Assert.True(body.GetProperty("isValidSQL").GetBoolean());
    }

    [Fact]
    public async Task Validate_sql_reports_a_syntax_error()
    {
        var body = await BodyAsync(await Client().GetAsync(
            $"{Feature}/0/validateSQL?f=json&sql=" + Uri.EscapeDataString("population > FUNC(1)")));

        Assert.False(body.GetProperty("isValidSQL").GetBoolean());
        var errors = body.GetProperty("validationErrors").EnumerateArray().ToArray();
        Assert.Equal(3002, errors[0].GetProperty("errorCode").GetInt32());
    }

    [Fact]
    public async Task Validate_sql_reports_an_unknown_field()
    {
        var body = await BodyAsync(await Client().GetAsync(
            $"{Feature}/0/validateSQL?f=json&sql=" + Uri.EscapeDataString("some_date < CURRENT_TIMESTAMP")));

        Assert.False(body.GetProperty("isValidSQL").GetBoolean());
        var errors = body.GetProperty("validationErrors").EnumerateArray().ToArray();
        Assert.Equal(3008, errors[0].GetProperty("errorCode").GetInt32());
        Assert.Contains("some_date", errors[0].GetProperty("description").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_sql_without_sql_is_a_typed_error()
    {
        var error = await ErrorAsync(
            await Client().GetAsync($"{Feature}/0/validateSQL?f=json"), HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("sql", error.GetProperty("message").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- item 4: aggregation-extension honesty ----

    [Theory]
    [InlineData("queryBins", "outStatistics")]
    [InlineData("queryTopFeatures", "orderByFields")]
    [InlineData("queryAnalytic", "outStatistics")]
    public async Task Aggregation_extensions_name_the_served_alternative(string operation, string alternative)
    {
        var error = await ErrorAsync(
            await Client().GetAsync($"{Feature}/0/{operation}?f=json"), HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(operation, error.GetProperty("message").GetString(), StringComparison.Ordinal);
        Assert.Contains(alternative, error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_aggregation_extension_on_an_unknown_layer_is_not_found()
    {
        await ErrorAsync(
            await Client().GetAsync($"{Feature}/99/queryBins?f=json"), HttpStatusCode.NotFound);
    }

    // ---- item 5: attachments (T-061 served: the demo store exposes no
    // blob face, so reads stay empty and writes stay rejected; the
    // store-backed behaviour lives in GeoServicesAttachmentsTests) ----

    [Fact]
    public async Task Query_attachments_reports_empty_groups_without_a_store()
    {
        // Without a blob face nothing is stored for any feature, so
        // every group is truthfully empty.
        var body = await BodyAsync(await Client().GetAsync($"{Feature}/0/queryAttachments?f=json"));

        var groups = body.GetProperty("attachmentGroups").EnumerateArray().ToArray();
        Assert.NotEmpty(groups);
        Assert.All(groups, group => Assert.Empty(group.GetProperty("attachmentInfos").EnumerateArray()));
    }

    [Fact]
    public async Task The_per_feature_attachments_resource_reports_the_empty_set()
    {
        var body = await BodyAsync(await Client().GetAsync($"{Feature}/0/1/attachments?f=json"));

        Assert.Empty(body.GetProperty("attachmentInfos").EnumerateArray());
    }

    [Theory]
    [InlineData("addAttachment", "")]
    [InlineData("deleteAttachments", "&attachmentIds=1")]
    [InlineData("updateAttachment", "&attachmentId=1")]
    public async Task Attachment_writes_are_rejected_without_a_store(string operation, string ids)
    {
        var error = await ErrorAsync(
            await AuthorizedClient().PostAsync($"{Feature}/0/1/{operation}?f=json{ids}", new StringContent(string.Empty)),
            HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains(operation, error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("addAttachment")]
    [InlineData("deleteAttachments")]
    [InlineData("updateAttachment")]
    public async Task Attachment_writes_require_an_admin_token(string operation)
    {
        var missing = await ErrorAsync(
            await Client().PostAsync($"{Feature}/0/1/{operation}?f=json", new StringContent(string.Empty)),
            HttpStatusCode.Unauthorized);

        Assert.Equal(498, missing.GetProperty("code").GetInt32());

        var request = new HttpRequestMessage(HttpMethod.Post, $"{Feature}/0/1/{operation}?f=json")
        {
            Content = new StringContent(string.Empty),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        var invalid = await ErrorAsync(await Client().SendAsync(request), HttpStatusCode.Forbidden);

        Assert.Equal(497, invalid.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Attachments_on_an_unknown_layer_are_not_found()
    {
        await ErrorAsync(
            await Client().GetAsync($"{Feature}/99/queryAttachments?f=json"), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_layer_reports_no_attachments_without_a_store()
    {
        var layer = await BodyAsync(await Client().GetAsync($"{Feature}/0?f=json"));

        Assert.False(layer.GetProperty("hasAttachments").GetBoolean());
        Assert.False(layer.TryGetProperty("attachmentProperties", out _));
    }

    private HttpClient AuthorizedClient()
    {
        var client = Client();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return client;
    }

    private static readonly string[] MapServices = ["map"];

    private async Task<HttpClient> MapServiceAsync()
    {
        var client = _factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "world",
            store = "demo",
            services = MapServices,
            layers = new[] { new { dataset = "demo.cities", layerId = 0, name = "Cities" } },
        });
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/maps/world")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return client;
    }

    private sealed class OpsFactory(string publicationsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", publicationsPath);
        }
    }
}
