using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// Feature attachments served end-to-end (T-061, ADR-0066): the layer
/// advertises <c>hasAttachments</c> with its <c>attachmentProperties</c>,
/// <c>queryAttachments</c> and the per-feature <c>attachments</c> resource
/// list the stored blobs, the content resource serves their bytes, and
/// <c>addAttachment</c>/<c>updateAttachment</c>/<c>deleteAttachments</c>
/// mutate them behind the single admin token. The always-available
/// <c>memory</c> store (with its <c>IFeatureAttachmentStore</c> capability)
/// stands in for PostGIS so no Docker is needed; each test gets a fresh
/// host, store and dataset.
/// </summary>
public sealed class GeoServicesAttachmentsTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Service = $"{Root}/attach/FeatureServer";

    private const string GeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"name":"Berlin","population":3664000}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.35,48.85]},"properties":{"name":"Paris","population":2150000}}
        ]}
        """;

    private static readonly byte[] Photo = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Revised = [9, 10, 11, 12];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-attachments-").FullName;
    private readonly WebApplicationFactory<Program> _factory;

    public GeoServicesAttachmentsTests() => _factory = new AttachmentFactory(Path.Combine(_directory, "publications.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private HttpClient Client() => _factory.CreateClient();

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

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

    private async Task SeedAsync()
    {
        var client = Client();
        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=attach.places&srid=4326&format=geojson&publish=attach",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
    }

    private static MultipartFormDataContent Upload(byte[] content, string fileName, string contentType, string? keywords = null)
    {
        var upload = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        upload.Add(file, "attachment", fileName);
        if (keywords is not null)
        {
            upload.Add(new StringContent(keywords), "keywords");
        }

        return upload;
    }

    private static async Task<JsonElement> AddAsync(HttpClient client, long objectId, byte[] content = null!, string fileName = "photo.jpg")
    {
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/{objectId}/addAttachment?f=json",
            Upload(content ?? Photo, fileName, "image/jpeg", "streetscape")));
        return await BodyAsync(response);
    }

    // ---- layer metadata ----

    [Fact]
    public async Task The_layer_advertises_attachments()
    {
        await SeedAsync();
        var layer = await BodyAsync(await Client().GetAsync($"{Service}/0?f=json"));

        Assert.True(layer.GetProperty("hasAttachments").GetBoolean());
        var properties = layer.GetProperty("attachmentProperties").EnumerateArray().ToArray();
        foreach (var name in new[] { "id", "name", "size", "contentType", "keywords" })
        {
            var property = Assert.Single(properties, entry => entry.GetProperty("name").GetString() == name);
            Assert.True(property.GetProperty("isEnabled").GetBoolean(), name);
        }
    }

    // ---- reads ----

    [Fact]
    public async Task Query_attachments_lists_every_feature_before_any_upload()
    {
        await SeedAsync();
        var body = await BodyAsync(await Client().GetAsync($"{Service}/0/queryAttachments?f=json"));

        var groups = body.GetProperty("attachmentGroups").EnumerateArray().ToArray();
        Assert.Equal([1, 2], groups.Select(group => group.GetProperty("parentObjectId").GetInt64()).ToArray());
        Assert.All(groups, group => Assert.Empty(group.GetProperty("attachmentInfos").EnumerateArray()));
    }

    [Fact]
    public async Task Query_attachments_filters_by_object_ids()
    {
        await SeedAsync();
        var client = Client();
        await AddAsync(client, 2);

        var body = await BodyAsync(await client.GetAsync($"{Service}/0/queryAttachments?f=json&objectIds=2"));

        var group = Assert.Single(body.GetProperty("attachmentGroups").EnumerateArray());
        Assert.Equal(2, group.GetProperty("parentObjectId").GetInt64());
        var info = Assert.Single(group.GetProperty("attachmentInfos").EnumerateArray());
        Assert.Equal("photo.jpg", info.GetProperty("name").GetString());
        Assert.Equal("image/jpeg", info.GetProperty("contentType").GetString());
        Assert.Equal(Photo.Length, info.GetProperty("size").GetInt64());
        Assert.Equal("streetscape", info.GetProperty("keywords").GetString());
    }

    [Fact]
    public async Task Query_attachments_with_an_unknown_object_id_is_not_found()
    {
        await SeedAsync();
        await ErrorAsync(
            await Client().GetAsync($"{Service}/0/queryAttachments?f=json&objectIds=99"),
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Query_attachments_with_malformed_object_ids_is_a_typed_error()
    {
        await SeedAsync();
        var error = await ErrorAsync(
            await Client().GetAsync($"{Service}/0/queryAttachments?f=json&objectIds=abc"),
            HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("objectIds", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_per_feature_attachments_resource_lists_uploads()
    {
        await SeedAsync();
        var client = Client();
        await AddAsync(client, 1);

        var body = await BodyAsync(await client.GetAsync($"{Service}/0/1/attachments?f=json"));

        var info = Assert.Single(body.GetProperty("attachmentInfos").EnumerateArray());
        Assert.Equal(1, info.GetProperty("id").GetInt64());
        Assert.Equal("photo.jpg", info.GetProperty("name").GetString());
        Assert.Equal(Photo.Length, info.GetProperty("size").GetInt64());
    }

    [Fact]
    public async Task The_per_feature_attachments_resource_for_an_unknown_feature_is_not_found()
    {
        await SeedAsync();
        await ErrorAsync(
            await Client().GetAsync($"{Service}/0/99/attachments?f=json"),
            HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_attachment_content_serves_the_stored_bytes()
    {
        await SeedAsync();
        var client = Client();
        await AddAsync(client, 1);

        var response = await client.GetAsync($"{Service}/0/1/attachments/1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Photo, await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task The_attachment_content_for_an_unknown_attachment_is_not_found()
    {
        await SeedAsync();
        var response = await Client().GetAsync($"{Service}/0/1/attachments/99");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- writes ----

    [Fact]
    public async Task Add_attachment_reports_the_assigned_id()
    {
        await SeedAsync();
        var result = await AddAsync(Client(), 1);

        var added = result.GetProperty("addAttachmentResult");
        Assert.True(added.GetProperty("success").GetBoolean());
        Assert.Equal(1, added.GetProperty("objectId").GetInt64());
    }

    [Fact]
    public async Task Add_attachment_to_an_unknown_feature_is_not_found()
    {
        await SeedAsync();
        var client = Client();
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/99/addAttachment?f=json",
            Upload(Photo, "photo.jpg", "image/jpeg")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Add_attachment_without_a_file_is_a_typed_error()
    {
        await SeedAsync();
        var client = Client();
        var error = await ErrorAsync(
            await client.SendAsync(Authorized(
                HttpMethod.Post, $"{Service}/0/1/addAttachment?f=json",
                new FormUrlEncodedContent([new KeyValuePair<string, string>("keywords", "none")]))),
            HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("attachment", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Update_attachment_replaces_the_blob()
    {
        await SeedAsync();
        var client = Client();
        await AddAsync(client, 1);

        using var upload = Upload(Revised, "revised.png", "image/png");
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/1/updateAttachment?f=json&attachmentId=1", upload));
        var updated = (await BodyAsync(response)).GetProperty("updateAttachmentResult");
        Assert.True(updated.GetProperty("success").GetBoolean());
        Assert.Equal(1, updated.GetProperty("objectId").GetInt64());

        var body = await BodyAsync(await client.GetAsync($"{Service}/0/1/attachments?f=json"));
        var info = Assert.Single(body.GetProperty("attachmentInfos").EnumerateArray());
        Assert.Equal("revised.png", info.GetProperty("name").GetString());
        Assert.Equal("image/png", info.GetProperty("contentType").GetString());
        Assert.Equal(Revised.Length, info.GetProperty("size").GetInt64());

        var content = await client.GetAsync($"{Service}/0/1/attachments/1");
        Assert.Equal(Revised, await content.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Update_attachment_with_an_unknown_id_is_not_found()
    {
        await SeedAsync();
        var client = Client();
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/1/updateAttachment?f=json&attachmentId=99",
            Upload(Revised, "revised.png", "image/png")));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Update_attachment_without_an_id_is_a_typed_error()
    {
        await SeedAsync();
        var client = Client();
        var error = await ErrorAsync(
            await client.SendAsync(Authorized(
                HttpMethod.Post, $"{Service}/0/1/updateAttachment?f=json",
                Upload(Revised, "revised.png", "image/png"))),
            HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("attachmentId", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_attachments_reports_each_id()
    {
        await SeedAsync();
        var client = Client();
        await AddAsync(client, 1);
        await AddAsync(client, 1, Revised, "second.jpg");

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/1/deleteAttachments?f=json&attachmentIds=1,2",
            new StringContent(string.Empty)));
        var results = (await BodyAsync(response)).GetProperty("deleteAttachmentResults").EnumerateArray().ToArray();

        Assert.Equal(2, results.Length);
        Assert.All(results, result => Assert.True(result.GetProperty("success").GetBoolean()));
        Assert.Equal([1, 2], results.Select(result => result.GetProperty("objectId").GetInt64()).ToArray());

        var body = await BodyAsync(await client.GetAsync($"{Service}/0/1/attachments?f=json"));
        Assert.Empty(body.GetProperty("attachmentInfos").EnumerateArray());
    }

    [Fact]
    public async Task Delete_attachments_reports_unknown_ids_per_id()
    {
        await SeedAsync();
        var client = Client();
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/1/deleteAttachments?f=json&attachmentIds=99",
            new StringContent(string.Empty)));
        var result = Assert.Single((await BodyAsync(response)).GetProperty("deleteAttachmentResults").EnumerateArray());

        Assert.Equal(99, result.GetProperty("objectId").GetInt64());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(404, result.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Delete_attachments_without_ids_is_a_typed_error()
    {
        await SeedAsync();
        var client = Client();
        var error = await ErrorAsync(
            await client.SendAsync(Authorized(
                HttpMethod.Post, $"{Service}/0/1/deleteAttachments?f=json",
                new StringContent(string.Empty))),
            HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("attachmentIds", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---- auth ----

    [Theory]
    [InlineData("addAttachment")]
    [InlineData("deleteAttachments")]
    [InlineData("updateAttachment")]
    public async Task Attachment_writes_require_an_admin_token(string operation)
    {
        await SeedAsync();
        var client = Client();

        using var anonymous = Upload(Photo, "photo.jpg", "image/jpeg");
        var missing = await ErrorAsync(
            await client.PostAsync($"{Service}/0/1/{operation}?f=json", anonymous),
            HttpStatusCode.Unauthorized);
        Assert.Equal(498, missing.GetProperty("code").GetInt32());

        using var forged = Upload(Photo, "photo.jpg", "image/jpeg");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{Service}/0/1/{operation}?f=json") { Content = forged };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        var invalid = await ErrorAsync(await client.SendAsync(request), HttpStatusCode.Forbidden);
        Assert.Equal(497, invalid.GetProperty("code").GetInt32());
    }

    private sealed class AttachmentFactory(string publicationsPath) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", publicationsPath);
        }
    }
}
