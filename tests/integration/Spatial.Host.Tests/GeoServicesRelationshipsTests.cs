using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Spatial.Host.Tests;

/// <summary>
/// Relationships served end-to-end (ADR-0077): a map declares
/// <c>dataset A column x is related to dataset B column y</c>, the declaring
/// layer advertises it in the <c>getLayer</c> metadata a client reads,
/// <c>queryRelatedRecords</c> traverses it with the related layer's own
/// <c>where</c> and field projection applied, and <c>relate</c>/
/// <c>unrelate</c> move the same key behind the edit gate. The
/// always-available <c>memory</c> store stands in for PostGIS so no Docker is
/// needed; each test gets a fresh host, store and datasets.
/// </summary>
public sealed class GeoServicesRelationshipsTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string Root = "/arcgis/rest/services";
    private const string Service = $"{Root}/rel/FeatureServer";

    private const string Parents = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"id":1,"name":"Berlin"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.35,48.85]},"properties":{"id":2,"name":"Paris"}}
        ]}
        """;

    private const string Children = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.41,52.51]},"properties":{"id":10,"parent_id":1,"name":"Mitte"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.36,48.86]},"properties":{"id":11,"parent_id":1,"name":"Kreuzberg"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.37,48.87]},"properties":{"id":12,"parent_id":2,"name":"Belleville"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.38,48.88]},"properties":{"id":13,"parent_id":null,"name":"Orphan"}}
        ]}
        """;

    private const string Tags = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"id":100,"name":"capital"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[2.35,48.85]},"properties":{"id":101,"name":"coastal"}}
        ]}
        """;

    private const string ParentTags = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":null,"properties":{"id":1,"parent_id":2,"tag_id":101}}
        ]}
        """;

    private static readonly string[] MitteAndKreuzberg = ["Mitte", "Kreuzberg"];

    private static readonly string[] KreuzbergAndBelleville = ["Kreuzberg", "Belleville"];

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-relationships-").FullName;
    private readonly SpatialHostFactory _factory;

    public GeoServicesRelationshipsTests() => _factory = new RelationshipFactory(Path.Combine(_directory, "maps.json"));

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
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {status}, got {response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.GetProperty("error");
    }

    /// <summary>The neutral host API's error envelope, which is flat rather than nested under <c>error</c>.</summary>
    private static async Task<JsonElement> HostErrorAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"expected {status}, got {response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>
    /// Ingests the four datasets and declares the map: layer 0 the parents,
    /// layer 1 their children (one-to-many), layer 2 the tags and layer 3 the
    /// join table the many-to-many relationship travels through.
    /// </summary>
    private async Task DeclareAsync(bool declareMap = true)
    {
        var client = Client();
        foreach (var (dataset, body) in new[]
        {
            ("rel.parents", Parents), ("rel.children", Children),
            ("rel.tags", Tags), ("rel.parent_tags", ParentTags),
        })
        {
            var response = await client.SendAsync(Authorized(
                HttpMethod.Post,
                $"/api/ingest?store=memory&dataset={dataset}&srid=4326&format=geojson&identity=source&identityField=id",
                new StringContent(body, Encoding.UTF8, "application/json")));
            Assert.Equal(HttpStatusCode.OK, await ExplainAsync(response));
        }

        if (!declareMap)
        {
            return;
        }

        var map = new
        {
            name = "rel",
            store = "memory",
            layers = new object[]
            {
                new
                {
                    dataset = "rel.parents", layerId = 0, name = "parents",
                    relationships = new object[]
                    {
                        new { name = "children", relatedLayerId = 1, primaryKeyColumn = "id", relatedKeyColumn = "parent_id" },
                        new
                        {
                            name = "tags", relatedLayerId = 2, primaryKeyColumn = "id", relatedKeyColumn = "id",
                            cardinality = "manyToMany",
                            join = new { dataset = "rel.parent_tags", primaryKeyColumn = "parent_id", relatedKeyColumn = "tag_id" },
                        },
                    },
                },
                new { dataset = "rel.children", layerId = 1, name = "children" },
                new { dataset = "rel.tags", layerId = 2, name = "tags" },
            },
            services = new[] { "feature" },
        };
        var declared = await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/maps/rel",
            new StringContent(JsonSerializer.Serialize(map), Encoding.UTF8, "application/json")));
        Assert.Equal(HttpStatusCode.OK, await ExplainAsync(declared));
    }

    /// <summary>The response body of a failure, so a red test says what the host answered.</summary>
    private static async Task<HttpStatusCode> ExplainAsync(HttpResponseMessage response) =>
        response.IsSuccessStatusCode
            ? response.StatusCode
            : throw new InvalidOperationException($"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private async Task<JsonElement> RelatedAsync(string query)
    {
        var client = Client();
        return await BodyAsync(await client.GetAsync($"{Service}/0/queryRelatedRecords?f=json&{query}"));
    }

    // ---- declaration ----

    [Fact]
    public async Task A_relationship_over_a_column_the_dataset_does_not_have_is_rejected_at_declaration()
    {
        await DeclareAsync(declareMap: false);
        var client = Client();
        var map = new
        {
            name = "rel",
            store = "memory",
            layers = new object[]
            {
                new
                {
                    dataset = "rel.parents", layerId = 0,
                    relationships = new[] { new { name = "children", relatedLayerId = 1, primaryKeyColumn = "id", relatedKeyColumn = "nope" } },
                },
                new { dataset = "rel.children", layerId = 1 },
            },
            services = new[] { "feature" },
        };

        var response = await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/maps/rel", new StringContent(JsonSerializer.Serialize(map), Encoding.UTF8, "application/json")));

        var error = await HostErrorAsync(response, HttpStatusCode.BadRequest);
        Assert.Contains("nope", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---- layer metadata ----

    [Fact]
    public async Task The_declaring_layer_advertises_its_relationships()
    {
        await DeclareAsync();
        var layer = await BodyAsync(await Client().GetAsync($"{Service}/0?f=json"));

        var relationships = layer.GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(2, relationships.Length);
        var children = Assert.Single(relationships, entry => entry.GetProperty("name").GetString() == "children");
        Assert.Equal("children", children.GetProperty("id").GetString());
        Assert.Equal(1, children.GetProperty("relatedLayerId").GetInt32());
        Assert.Equal("esriRelationshipTypeOneToMany", children.GetProperty("type").GetString());
        var tags = Assert.Single(relationships, entry => entry.GetProperty("name").GetString() == "tags");
        Assert.Equal(2, tags.GetProperty("relatedLayerId").GetInt32());
        Assert.Equal("esriRelationshipTypeManyToMany", tags.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_layer_that_declares_no_relationship_omits_the_key()
    {
        await DeclareAsync();
        var layer = await BodyAsync(await Client().GetAsync($"{Service}/1?f=json"));

        Assert.False(layer.TryGetProperty("relationships", out _));
    }

    // ---- reads ----

    [Fact]
    public async Task QueryRelatedRecords_returns_the_related_records_of_each_origin_record()
    {
        await DeclareAsync();

        var body = await RelatedAsync("objectIds=1,2&relationshipId=children&outFields=name&returnGeometry=false");

        var groups = body.GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal(1, groups[0].GetProperty("relatedId").GetInt32());
        Assert.Equal(
            MitteAndKreuzberg,
            groups[0].GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("attributes").GetProperty("name").GetString()!).ToArray());
        var second = Assert.Single(groups[1].GetProperty("fields").EnumerateArray());
        Assert.Equal("Belleville", second.GetProperty("attributes").GetProperty("name").GetString());
    }

    [Fact]
    public async Task QueryRelatedRecords_applies_the_related_layers_where_clause()
    {
        await DeclareAsync();

        var body = await RelatedAsync("objectIds=1&relationshipId=children&where=name%3D'Kreuzberg'&outFields=name&returnGeometry=false");

        var group = Assert.Single(body.GetProperty("relationships").EnumerateArray());
        var row = Assert.Single(group.GetProperty("fields").EnumerateArray());
        Assert.Equal("Kreuzberg", row.GetProperty("attributes").GetProperty("name").GetString());
    }

    [Fact]
    public async Task QueryRelatedRecords_projects_only_the_requested_fields()
    {
        await DeclareAsync();

        var body = await RelatedAsync("objectIds=2&relationshipId=children&outFields=parent_id&returnGeometry=false");

        var row = Assert.Single(Assert.Single(body.GetProperty("relationships").EnumerateArray()).GetProperty("fields").EnumerateArray());
        var attributes = row.GetProperty("attributes");
        Assert.Equal(2, attributes.GetProperty("parent_id").GetInt32());
        Assert.False(attributes.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task QueryRelatedRecords_takes_the_only_relationship_when_no_id_is_given()
    {
        await DeclareAsync();
        var client = Client();
        await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children&relateIds=12",
            new StringContent(string.Empty)));

        // The layer declares two relationships, so an absent id is a typed
        // failure naming them rather than a guess.
        var error = await ErrorAsync(
            await client.GetAsync($"{Service}/0/queryRelatedRecords?f=json&objectIds=1"),
            HttpStatusCode.BadRequest);

        Assert.Contains("relationshipId", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryRelatedRecords_traverses_a_many_to_many_join()
    {
        await DeclareAsync();

        var body = await RelatedAsync("objectIds=2&relationshipId=tags&outFields=name&returnGeometry=false");

        var row = Assert.Single(Assert.Single(body.GetProperty("relationships").EnumerateArray()).GetProperty("fields").EnumerateArray());
        Assert.Equal("coastal", row.GetProperty("attributes").GetProperty("name").GetString());
    }

    [Fact]
    public async Task QueryRelatedRecords_omits_an_origin_record_with_no_related_records()
    {
        await DeclareAsync();

        // Feature 1 has no tags, so the traversal answers no group for it
        // rather than an empty one.
        var body = await RelatedAsync("objectIds=1&relationshipId=tags&outFields=name");

        Assert.Empty(body.GetProperty("relationships").EnumerateArray());
    }

    [Fact]
    public async Task QueryRelatedRecords_over_every_record_groups_by_origin()
    {
        await DeclareAsync();

        var body = await RelatedAsync("relationshipId=children&outFields=name&returnGeometry=false");

        var groups = body.GetProperty("relationships").EnumerateArray().ToArray();
        Assert.Equal([1, 2], groups.Select(group => group.GetProperty("relatedId").GetInt32()).ToArray());
        Assert.Equal(2, groups[0].GetProperty("fields").GetArrayLength());
    }

    // ---- failures ----

    [Fact]
    public async Task QueryRelatedRecords_over_an_unknown_origin_id_is_not_found()
    {
        await DeclareAsync();

        var error = await ErrorAsync(
            await Client().GetAsync($"{Service}/0/queryRelatedRecords?f=json&objectIds=99&relationshipId=children"),
            HttpStatusCode.NotFound);

        Assert.Equal(404, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task QueryRelatedRecords_over_an_unknown_relationship_is_a_typed_error()
    {
        await DeclareAsync();

        var error = await ErrorAsync(
            await Client().GetAsync($"{Service}/0/queryRelatedRecords?f=json&objectIds=1&relationshipId=nope"),
            HttpStatusCode.BadRequest);

        Assert.Equal(400, error.GetProperty("code").GetInt32());
        Assert.Contains("relationshipId", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryRelatedRecords_on_a_layer_that_declares_none_is_a_typed_error()
    {
        await DeclareAsync();

        var error = await ErrorAsync(
            await Client().GetAsync($"{Service}/1/queryRelatedRecords?f=json&objectIds=1&relationshipId=children"),
            HttpStatusCode.BadRequest);

        Assert.Contains("declares no relationship", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryRelatedRecords_rejects_a_result_shape_it_cannot_answer()
    {
        await DeclareAsync();

        var error = await ErrorAsync(
            await Client().GetAsync($"{Service}/0/queryRelatedRecords?f=json&objectIds=1&relationshipId=children&returnCountOnly=true"),
            HttpStatusCode.BadRequest);

        Assert.Contains("returnCountOnly", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryRelatedRecords_over_a_malformed_object_id_list_is_a_typed_error()
    {
        await DeclareAsync();

        var error = await ErrorAsync(
            await Client().GetAsync($"{Service}/0/queryRelatedRecords?f=json&objectIds=one&relationshipId=children"),
            HttpStatusCode.BadRequest);

        Assert.Contains("objectIds", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    // ---- writes ----

    [Fact]
    public async Task Relate_moves_the_key_and_the_read_follows_it()
    {
        await DeclareAsync();
        var client = Client();
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/relate?f=json&objectIds=2&relationshipId=children&relateIds=11",
            new StringContent(string.Empty)));
        var results = (await BodyAsync(response)).GetProperty("results").EnumerateArray().ToArray();
        var result = Assert.Single(results);
        Assert.True(result.GetProperty("success").GetBoolean());
        Assert.Equal(2, result.GetProperty("objectId").GetInt64());
        Assert.Equal(11, result.GetProperty("relateId").GetInt64());

        var after = await RelatedAsync("objectIds=2&relationshipId=children&outFields=name&returnGeometry=false");
        Assert.Equal(
            KreuzbergAndBelleville,
            Assert.Single(after.GetProperty("relationships").EnumerateArray()).GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("attributes").GetProperty("name").GetString()!).ToArray());
    }

    [Fact]
    public async Task Relate_reports_an_unknown_related_record_per_pair()
    {
        await DeclareAsync();
        var response = await Client().SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children&relateIds=99",
            new StringContent(string.Empty)));

        var result = Assert.Single((await BodyAsync(response)).GetProperty("results").EnumerateArray());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(404, result.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Relate_without_relate_ids_is_a_typed_error()
    {
        await DeclareAsync();
        var error = await ErrorAsync(
            await Client().SendAsync(Authorized(
                HttpMethod.Post, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children",
                new StringContent(string.Empty))),
            HttpStatusCode.BadRequest);

        Assert.Contains("relateIds", error.GetProperty("message").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unrelate_clears_the_key()
    {
        await DeclareAsync();
        var client = Client();
        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/unrelate?f=json&objectIds=1&relationshipId=children&relateIds=10",
            new StringContent(string.Empty)));

        Assert.True(Assert.Single((await BodyAsync(response)).GetProperty("results").EnumerateArray()).GetProperty("success").GetBoolean());

        var after = await RelatedAsync("objectIds=1&relationshipId=children&outFields=name&returnGeometry=false");
        var row = Assert.Single(Assert.Single(after.GetProperty("relationships").EnumerateArray()).GetProperty("fields").EnumerateArray());
        Assert.Equal("Kreuzberg", row.GetProperty("attributes").GetProperty("name").GetString());
    }

    [Fact]
    public async Task Unrelate_of_a_pair_that_is_not_related_is_a_typed_error()
    {
        await DeclareAsync();
        var response = await Client().SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/unrelate?f=json&objectIds=1&relationshipId=children&relateIds=12",
            new StringContent(string.Empty)));

        var result = Assert.Single((await BodyAsync(response)).GetProperty("results").EnumerateArray());
        Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal(404, result.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Relate_over_a_many_to_many_relationship_adds_a_join_row()
    {
        await DeclareAsync();
        var client = Client();
        var before = await RelatedAsync("objectIds=1&relationshipId=tags&outFields=name&returnGeometry=false");
        Assert.Empty(before.GetProperty("relationships").EnumerateArray());

        var response = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=tags&relateIds=100",
            new StringContent(string.Empty)));
        Assert.True(Assert.Single((await BodyAsync(response)).GetProperty("results").EnumerateArray()).GetProperty("success").GetBoolean());

        var after = await RelatedAsync("objectIds=1&relationshipId=tags&outFields=name&returnGeometry=false");
        var row = Assert.Single(Assert.Single(after.GetProperty("relationships").EnumerateArray()).GetProperty("fields").EnumerateArray());
        Assert.Equal("capital", row.GetProperty("attributes").GetProperty("name").GetString());

        var unrelate = await client.SendAsync(Authorized(
            HttpMethod.Post, $"{Service}/0/unrelate?f=json&objectIds=1&relationshipId=tags&relateIds=100",
            new StringContent(string.Empty)));
        Assert.True(Assert.Single((await BodyAsync(unrelate)).GetProperty("results").EnumerateArray()).GetProperty("success").GetBoolean());

        var empty = await RelatedAsync("objectIds=1&relationshipId=tags&outFields=name&returnGeometry=false");
        Assert.Empty(empty.GetProperty("relationships").EnumerateArray());
    }

    private sealed class RelationshipFactory(string mapsPath) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
        }
    }
}
