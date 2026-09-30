using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;

namespace Spatial.Host.Tests;

/// <summary>
/// Relate and unrelate on a host with local auth enabled (ADR-0071 +
/// ADR-0077): they are edits, so they travel the same gate as
/// <c>addFeatures</c> — an admin session bearer authorizes the write, a
/// missing bearer is token-required and a signed-in user without the role is
/// an invalid token. The read they sit behind stays public, like
/// <c>query</c>.
/// </summary>
public sealed class GeoServicesRelationshipAuthTests : IDisposable
{
    private static readonly PasswordHasher<User> Hasher = new();
    private const string AdminPassword = "correct horse battery staple";
    private const string MemberPassword = "open sesame";
    private const string Service = "/arcgis/rest/services/rel/FeatureServer";

    private const string Parents = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.4,52.5]},"properties":{"id":1,"name":"Berlin"}}
        ]}
        """;

    private const string Children = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.41,52.51]},"properties":{"id":10,"parent_id":1,"name":"Mitte"}},
          {"type":"Feature","geometry":{"type":"Point","coordinates":[13.42,52.52]},"properties":{"id":11,"parent_id":null,"name":"Kreuzberg"}}
        ]}
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-relationship-auth-").FullName;
    private readonly SpatialHostFactory _factory;

    public GeoServicesRelationshipAuthTests() => _factory = new RelationshipAuthFactory(Path.Combine(_directory, "maps.json"));

    public void Dispose()
    {
        _factory.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task An_admin_session_bearer_authorizes_a_relate()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);

        var related = await PostAsync(client, admin, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children&relateIds=10");
        Assert.Equal(HttpStatusCode.OK, related.StatusCode);
        var result = JsonDocument.Parse(await related.Content.ReadAsStringAsync())
            .RootElement.GetProperty("results").EnumerateArray().Single();
        Assert.True(result.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task An_admin_session_bearer_authorizes_an_unrelate()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);
        await PostAsync(client, admin, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children&relateIds=10");

        var unrelate = await PostAsync(client, admin, $"{Service}/0/unrelate?f=json&objectIds=1&relationshipId=children&relateIds=10");

        Assert.Equal(HttpStatusCode.OK, unrelate.StatusCode);
        var result = JsonDocument.Parse(await unrelate.Content.ReadAsStringAsync())
            .RootElement.GetProperty("results").EnumerateArray().Single();
        Assert.True(result.GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_missing_bearer_is_a_token_required_edit_error()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);

        using var anonymous = await client.PostAsync(
            $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children&relateIds=10", new StringContent(string.Empty));

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        var error = JsonDocument.Parse(await anonymous.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(498, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task A_signed_in_user_without_the_admin_role_is_an_invalid_token_error()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);
        var member = await LoginAsync(client, "bob", MemberPassword);

        var refused = await PostAsync(client, member, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children&relateIds=10");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var error = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error");
        Assert.Equal(497, error.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task The_traversal_stays_public()
    {
        using var client = _factory.CreateClient();
        var admin = await LoginAsync(client, "alice", AdminPassword);
        await SeedAsync(client, admin);
        await PostAsync(client, admin, $"{Service}/0/relate?f=json&objectIds=1&relationshipId=children&relateIds=10");

        var read = await client.GetAsync($"{Service}/0/queryRelatedRecords?f=json&objectIds=1&relationshipId=children&outFields=name");

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    private static async Task<string> LoginAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new { username, password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("token").GetString();
        Assert.NotNull(token);
        return token;
    }

    /// <summary>Ingests the two datasets and declares the one-to-many relationship between them.</summary>
    private static async Task SeedAsync(HttpClient client, string token)
    {
        foreach (var (dataset, body) in new[] { ("rel.parents", Parents), ("rel.children", Children) })
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/ingest?store=memory&dataset={dataset}&srid=4326&format=geojson&identity=source&identityField=id")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var ingested = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, ingested.StatusCode);
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
                    relationships = new[] { new { name = "children", relatedLayerId = 1, primaryKeyColumn = "id", relatedKeyColumn = "parent_id" } },
                },
                new { dataset = "rel.children", layerId = 1, name = "children" },
            },
            services = new[] { "feature" },
        };
        using var declaration = new HttpRequestMessage(HttpMethod.Put, "/api/maps/rel")
        {
            Content = new StringContent(JsonSerializer.Serialize(map), Encoding.UTF8, "application/json"),
        };
        declaration.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var declared = await client.SendAsync(declaration);
        Assert.Equal(HttpStatusCode.OK, declared.StatusCode);
    }

    private static async Task<HttpResponseMessage> PostAsync(HttpClient client, string token, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(string.Empty) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request);
    }

    private sealed record User(string Name);

    private sealed class RelationshipAuthFactory(string mapsPath) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Maps:Path", mapsPath);
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Spatial:Auth:Users:0:Username"] = "alice",
                    ["Spatial:Auth:Users:0:PasswordHash"] = Hasher.HashPassword(new User("alice"), AdminPassword),
                    ["Spatial:Auth:Users:0:Roles:0"] = "admin",
                    ["Spatial:Auth:Users:1:Username"] = "bob",
                    ["Spatial:Auth:Users:1:PasswordHash"] = Hasher.HashPassword(new User("bob"), MemberPassword),
                }));
        }
    }
}
