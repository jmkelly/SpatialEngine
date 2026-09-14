using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.Client;

namespace Spatial.Host.Tests;

/// <summary>
/// The neutral admin surface (ADR-0041 §5): token gating, publication CRUD,
/// raw/multipart ingest with the size and format caps, and the ingest →
/// publish → serve path over the always-available <c>memory</c> store.
/// </summary>
public sealed class AdminEndpointTests : IDisposable
{
    private const string Token = "test-admin-token";

    private static readonly string[] MapServices = ["map"];
    private static readonly string[] FeatureAndMapServices = ["feature", "map"];
    private static readonly string[] ImageServices = ["image"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-admin-").FullName;

    private WebApplicationFactory<Program> Factory(long maxBytes = 100_000_000, int maxFeatures = 1_000_000) =>
        new AdminFactory(Path.Combine(_directory, "publications.json"), maxBytes, maxFeatures);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    private const string GeoJson = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"name":"Berlin","population":3664000}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.35,48.85]},"properties":{"name":"Paris","population":2150000}}
        ]}
        """;

    [Fact]
    public async Task Without_a_configured_token_the_mutation_routes_are_not_mounted()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var put = await client.PutAsync("/api/maps/x", Json("{}"));
        var ingest = await client.PostAsync("/api/ingest?dataset=public.x&srid=4326&format=geojson", new ByteArrayContent([]));

        // The read route still exists, so PUT is method-not-allowed rather than
        // not-found; the ingest path (POST only) 404s because it is unmounted.
        Assert.Equal(HttpStatusCode.MethodNotAllowed, put.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ingest.StatusCode);
    }

    [Fact]
    public async Task The_removed_publications_aliases_are_not_found()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        // Pre-ADR-0053 aliases were removed in 0.2.0; /api/maps is canonical.
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/publications")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/publications/x")).StatusCode);
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsync("/api/publications/x/render", Json("{}"))).StatusCode);
    }

    [Fact]
    public async Task A_missing_token_is_401_and_a_wrong_token_is_403()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var missing = await client.PutAsync("/api/maps/x", Json("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrongRequest = new HttpRequestMessage(HttpMethod.Put, "/api/maps/x") { Content = Json("{}") };
        wrongRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        var wrong = await client.SendAsync(wrongRequest);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
    }

    [Fact]
    public async Task Publishing_a_map_with_a_missing_dataset_is_not_found()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "ghost",
            store = "memory",
            services = MapServices,
            layers = new[] { new { dataset = "public.ghost", layerId = 0, kind = "feature" } },
        });

        var response = await client.SendAsync(Authorized(HttpMethod.Put, "/api/maps/ghost", Json(body)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not.found", (await BodyAsync(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/maps/ghost")).StatusCode);
    }

    [Fact]
    public async Task Publishing_an_image_map_without_a_raster_provider_is_rejected()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var body = JsonSerializer.Serialize(new
        {
            name = "imagery",
            store = "memory",
            services = ImageServices,
            layers = new[] { new { dataset = "public.raster", layerId = 0, kind = "image" } },
        });

        var response = await client.SendAsync(Authorized(HttpMethod.Put, "/api/maps/imagery", Json(body)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Ingest_loads_a_geojson_upload_and_publishes_it()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.parks&srid=4326&format=geojson&publish=parks",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);
        var result = await BodyAsync(ingest);
        Assert.Equal("public.parks", result.GetProperty("dataset").GetString());
        Assert.Equal(2, result.GetProperty("features").GetInt64());
        Assert.Equal("id", result.GetProperty("identityField").GetString());
        Assert.Equal("parks", result.GetProperty("map").GetProperty("name").GetString());

        var publication = await client.GetAsync("/api/maps/parks");
        Assert.Equal(HttpStatusCode.OK, publication.StatusCode);
        var layers = (await BodyAsync(publication)).GetProperty("layers").EnumerateArray().ToArray();
        Assert.Single(layers);
        Assert.Equal("public.parks", layers[0].GetProperty("dataset").GetString());

        var describe = await client.GetAsync("/api/datasets/public.parks?store=memory");
        Assert.Equal(HttpStatusCode.OK, describe.StatusCode);
        Assert.Equal(["id"], (await BodyAsync(describe)).GetProperty("idColumns").EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task Ingest_accepts_a_multipart_upload()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(GeoJson, Encoding.UTF8, "application/geo+json"), "file", "parks.geojson");
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.multi&srid=4326&format=geojson", content));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, (await BodyAsync(response)).GetProperty("features").GetInt64());
    }

    [Fact]
    public async Task Csv_ingest_works_with_the_ndjson_format_name()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.csv&srid=4326&format=csv&identity=none",
            new StringContent("name,x,y\nBerlin,13.4,52.5\n", Encoding.UTF8, "text/csv")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await BodyAsync(response);
        Assert.Null(result.GetProperty("identityField").GetString());
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("")]
    [InlineData("{\"type\":\"FeatureCollection\",\"features\":[]}")]
    public async Task A_malformed_upload_is_a_bad_request(string body)
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.bad&srid=4326&format=geojson",
            new StringContent(body, Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_malformed_multipart_upload_is_a_bad_request_naming_the_file_part()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var bad = new ByteArrayContent(Encoding.UTF8.GetBytes("this is not a valid multipart body"));
        bad.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=----badboundary");
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.bad&srid=4326&format=geojson", bad));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await BodyAsync(response);
        Assert.Equal("invalid.arguments", body.GetProperty("code").GetString());
        Assert.Contains("file", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_esri_admin_malformed_multipart_upload_is_a_bad_request_naming_the_file_part()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var bad = new ByteArrayContent(Encoding.UTF8.GetBytes("this is not a valid multipart body"));
        bad.Headers.ContentType = MediaTypeHeaderValue.Parse("multipart/form-data; boundary=----badboundary");
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.bad&srid=4326&format=geojson", bad));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await BodyAsync(response)).GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("file", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_disallowed_format_is_rejected()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.x&srid=4326&format=shapefile",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_over_limit_upload_is_rejected()
    {
        using var factory = Factory(maxBytes: 16);
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=memory&dataset=public.x&srid=4326&format=geojson",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Maps_can_be_put_and_deleted()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        const string body = """{"name":"parks","store":"memory","services":["feature"],"layers":[{"dataset":"public.parks","layerId":0}]}""";

        await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.parks&srid=4326&format=geojson",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));

        var put = await client.SendAsync(Authorized(HttpMethod.Put, "/api/maps/parks", Json(body)));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.Equal("parks", (await BodyAsync(put)).GetProperty("name").GetString());

        var delete = await client.SendAsync(Authorized(HttpMethod.Delete, "/api/maps/parks"));
        Assert.True((await BodyAsync(delete)).GetBoolean());
    }

    [Fact]
    public async Task Ingest_into_an_unknown_store_is_rejected()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/api/ingest?store=nope&dataset=public.x&srid=4326&format=geojson",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("does not support ingest", (await BodyAsync(response)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_runtime_map_is_served_through_geoservices()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.parks&srid=4326&format=geojson&publish=parks",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        var catalog = await client.GetAsync("/arcgis/rest/services?f=json");
        var services = (await BodyAsync(catalog)).GetProperty("services").EnumerateArray()
            .Select(service => (service.GetProperty("name").GetString(), service.GetProperty("type").GetString()))
            .ToArray();
        Assert.Contains(("parks", "FeatureServer"), services);

        var root = await client.GetAsync("/arcgis/rest/services/parks/FeatureServer?f=json");
        Assert.Equal(1, (await BodyAsync(root)).GetProperty("layers").GetArrayLength());

        var query = await client.GetAsync("/arcgis/rest/services/parks/FeatureServer/0/query?where=1%3D1&outFields=*&f=json");
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
        var features = (await BodyAsync(query)).GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("name").GetString() ?? string.Empty)
            .ToArray();
        Assert.Equal(["Berlin", "Paris"], features);
    }

    [Fact]
    public async Task The_esri_admin_projection_is_unavailable_without_a_token()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/arcgis/admin/services");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(503, (await BodyAsync(response)).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task The_esri_admin_projection_lists_and_creates_services()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var created = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/services/parks.FeatureServer/createService",
            new FormUrlEncodedContent([new("store", "memory"), new("dataset", "public.parks")])));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        Assert.True((await BodyAsync(created)).GetProperty("success").GetBoolean());

        var listed = await client.SendAsync(Authorized(HttpMethod.Get, "/arcgis/admin/services"));
        var names = (await BodyAsync(listed)).GetProperty("services").EnumerateArray()
            .Select(service => service.GetProperty("name").GetString() ?? string.Empty).ToArray();
        Assert.Contains("parks", names);

        var got = await client.SendAsync(Authorized(HttpMethod.Get, "/arcgis/admin/services/parks.FeatureServer"));
        Assert.Equal("memory", (await BodyAsync(got)).GetProperty("store").GetString());
    }

    [Fact]
    public async Task The_esri_admin_upload_and_publish_creates_a_served_service()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(GeoJson, Encoding.UTF8, "application/geo+json"), "file", "parks.geojson");
        var upload = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.admin&srid=4326&format=geojson", content));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var itemId = (await BodyAsync(upload)).GetProperty("item").GetProperty("itemId").GetString();

        var published = await client.SendAsync(Authorized(
            HttpMethod.Post, $"/arcgis/admin/uploads/{itemId}/publish",
            new FormUrlEncodedContent([new("name", "adminparks")])));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        var served = await client.GetAsync("/arcgis/rest/services/adminparks/FeatureServer/0/query?where=1%3D1&f=json");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(2, (await BodyAsync(served)).GetProperty("features").GetArrayLength());
    }

    [Fact]
    public async Task An_uploaded_auto_identity_layer_accepts_an_add_without_an_objectid()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.parks&srid=4326&format=geojson&publish=parks",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, ingest.StatusCode);

        const string add = """
            {"features":[{"attributes":{"name":"Rome","population":2870000},"geometry":{"x":12.5,"y":41.9,"spatialReference":{"wkid":4326}}}]}
            """;
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/arcgis/rest/services/parks/FeatureServer/0/addFeatures",
            new StringContent(add, Encoding.UTF8, "application/json")));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await BodyAsync(response)).GetProperty("addResults")[0];
        Assert.True(result.GetProperty("success").GetBoolean(), result.ToString());
        Assert.Equal(3, result.GetProperty("objectId").GetInt64());
    }

    [Fact]
    public async Task The_dotnet_sdk_can_ingest_and_publish()
    {
        using var factory = Factory();
        var client = new SpatialClient(factory.CreateClient());

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(GeoJson));
        var result = await client.IngestAsync(
            stream,
            new IngestUpload("parks.geojson", "public.parks", 4326, Publish: "parks"),
            adminToken: Token);

        Assert.Equal(2, result.Features);
        Assert.Equal("parks", (await client.GetMapAsync("parks")).Name);
        Assert.Contains(await client.ListMapsAsync(), map => map.Name == "parks");
    }

    [Fact]
    public async Task The_esri_admin_upload_accepts_each_format_and_rejects_unknown()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        using var ndjson = new MultipartFormDataContent();
        ndjson.Add(new StringContent("{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[1,2]},\"properties\":{\"name\":\"A\"}}\n", Encoding.UTF8, "application/geo+json"), "file", "a.ndjson");
        var ndResponse = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.nd&srid=4326&format=ndjson", ndjson));
        Assert.Equal(HttpStatusCode.OK, ndResponse.StatusCode);

        using var csv = new MultipartFormDataContent();
        csv.Add(new StringContent("name,x,y\nA,1,2\n", Encoding.UTF8, "text/csv"), "file", "a.csv");
        var csvResponse = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.csv2&srid=4326&format=csv&identity=none", csv));
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);

        using var bad = new MultipartFormDataContent();
        bad.Add(new StringContent(GeoJson, Encoding.UTF8, "application/json"), "file", "a.json");
        var badResponse = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.bad&srid=4326&format=shapefile", bad));
        Assert.Equal(HttpStatusCode.BadRequest, badResponse.StatusCode);
    }

    [Fact]
    public async Task Ingest_reprojects_a_source_crs_to_the_target_srid()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        // Berlin in EPSG:3857 (Web Mercator) loaded into an EPSG:4326 column.
        const string webMercator = """
            {"type":"FeatureCollection","features":[
              {"type":"Feature","geometry":{"type":"Point","coordinates":[1494000,6894000]},"properties":{"name":"Berlin"}}
            ]}
            """;
        var ingest = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.merc&srid=4326&sourceSrid=3857&format=geojson&publish=merc",
            new StringContent(webMercator, Encoding.UTF8, "application/json")));
        Assert.True(ingest.StatusCode == HttpStatusCode.OK, await ingest.Content.ReadAsStringAsync());

        var query = await BodyAsync(await client.GetAsync(
            "/arcgis/rest/services/merc/FeatureServer/0/query?where=1%3D1&outFields=*&returnGeometry=true&outSR=4326&f=json"));
        var geometry = query.GetProperty("features")[0].GetProperty("geometry");
        Assert.InRange(geometry.GetProperty("x").GetDouble(), 13.3, 13.5);
        Assert.InRange(geometry.GetProperty("y").GetDouble(), 52.4, 52.6);
    }

    [Fact]
    public async Task The_esri_admin_upload_enforces_the_feature_cap()
    {
        using var factory = Factory(maxFeatures: 1);
        var client = factory.CreateClient();

        // The fixture carries two features, above the configured cap of one:
        // the small-bytes bypass the neutral surface closes (T-062) must
        // close on the Esri path too.
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(GeoJson, Encoding.UTF8, "application/geo+json"), "file", "parks.geojson");
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.capped&srid=4326&format=geojson", content));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await BodyAsync(response)).GetProperty("error");
        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("above the configured maximum", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_esri_admin_publish_merges_into_an_existing_map()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        // Seed a two-service map through the neutral surface: publish the
        // first dataset, then widen its services so the merge has something
        // to preserve.
        var seed = await client.SendAsync(Authorized(
            HttpMethod.Post,
            "/api/ingest?store=memory&dataset=public.first&srid=4326&format=geojson&publish=merged",
            new StringContent(GeoJson, Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, seed.StatusCode);
        var widen = JsonSerializer.Serialize(new
        {
            name = "merged",
            store = "memory",
            services = FeatureAndMapServices,
            layers = new[] { new { dataset = "public.first", layerId = 0, kind = "feature" } },
        });
        var put = await client.SendAsync(Authorized(HttpMethod.Put, "/api/maps/merged", Json(widen)));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        // Publish a second dataset over the same map through the Esri path.
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(GeoJson, Encoding.UTF8, "application/geo+json"), "file", "second.geojson");
        var upload = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.second&srid=4326&format=geojson", content));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var itemId = (await BodyAsync(upload)).GetProperty("item").GetProperty("itemId").GetString();

        var published = await client.SendAsync(Authorized(
            HttpMethod.Post, $"/arcgis/admin/uploads/{itemId}/publish",
            new FormUrlEncodedContent([new("name", "merged")])));
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);

        // Merge, not clobber: both layers present, both services kept, and
        // the appended layer takes the next stable id.
        var map = await BodyAsync(await client.GetAsync("/api/maps/merged"));
        var layers = map.GetProperty("layers").EnumerateArray().ToArray();
        Assert.Equal(2, layers.Length);
        Assert.Equal(["public.first", "public.second"],
            layers.Select(layer => layer.GetProperty("dataset").GetString() ?? string.Empty).ToArray());
        Assert.Equal([0, 1], layers.Select(layer => layer.GetProperty("layerId").GetInt32()).ToArray());
        var services = map.GetProperty("services").EnumerateArray()
            .Select(service => service.GetString() ?? string.Empty).ToArray();
        Assert.Contains("feature", services);
        Assert.Contains("map", services);
    }

    [Fact]
    public async Task The_esri_admin_publish_without_a_name_is_a_bad_request()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(GeoJson, Encoding.UTF8, "application/geo+json"), "file", "parks.geojson");
        var upload = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads?store=memory&dataset=public.noname&srid=4326&format=geojson", content));
        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        var itemId = (await BodyAsync(upload)).GetProperty("item").GetProperty("itemId").GetString();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"/arcgis/admin/uploads/{itemId}/publish",
            new FormUrlEncodedContent([])));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await BodyAsync(response)).GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task The_esri_admin_publish_of_an_unknown_upload_is_a_bad_request()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, "/arcgis/admin/uploads/doesnotexist/publish",
            new FormUrlEncodedContent([new("name", "ghost")])));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(400, (await BodyAsync(response)).GetProperty("error").GetProperty("code").GetInt32());
    }

    /// <summary>A host with an admin token and a per-test map file.</summary>
    private sealed class AdminFactory(string mapsPath, long maxBytes, int maxFeatures = 1_000_000) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
            builder.UseSetting("Spatial:Ingest:MaxBytes", maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Spatial:Ingest:MaxFeatures", maxFeatures.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
