using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;

namespace Spatial.Host.Tests;

/// <summary>
/// The runtime store-connection surface, test-first: an admin sets, lists,
/// removes and reloads the per-store connection strings without a restart;
/// saves are blind (a bad string fails on the store's next use, not on the
/// save); statuses never carry secrets; and everything here requires the
/// admin bearer.
/// </summary>
public sealed class StoreConnectionEndpointTests : IDisposable
{
    private const string Token = "test-admin-token";
    private const string BlindSecret = "Host=127.0.0.1;Port=1;Database=gis;Username=u;Password=p;Timeout=2";

    private readonly string _directory = Directory.CreateTempSubdirectory("spatial-connections-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ConnectionFactory Factory() => new(Path.Combine(_directory, "connections.json"));

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    private static HttpRequestMessage Authorized(HttpMethod method, string path, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        return request;
    }

    private static async Task<JsonElement> BodyAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Get_connections_starts_empty()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(HttpMethod.Get, "/api/connections"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await BodyAsync(response)).EnumerateArray());
    }

    [Fact]
    public async Task Put_connection_saves_blind_and_never_returns_the_secret()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var saved = await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/connections/postgis", Json($"{{\"connectionString\":\"{BlindSecret}\"}}")));

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var status = await BodyAsync(saved);
        Assert.Equal("postgis", status.GetProperty("store").GetString());
        Assert.True(status.GetProperty("configured").GetBoolean());
        Assert.NotNull(status.GetProperty("updatedAt").GetString());
        Assert.DoesNotContain("Password=p", await saved.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var listed = await client.SendAsync(Authorized(HttpMethod.Get, "/api/connections"));
        var raw = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Password=p", raw, StringComparison.Ordinal);
        Assert.DoesNotContain(BlindSecret, raw, StringComparison.Ordinal);
        var entries = JsonDocument.Parse(raw).RootElement;
        Assert.Equal("postgis", Assert.Single(entries.EnumerateArray()).GetProperty("store").GetString());
    }

    [Theory]
    [InlineData("memory")]
    [InlineData("demo")]
    [InlineData("nope")]
    [InlineData("has space")]
    public async Task Put_rejects_stores_without_a_connection_string(string store)
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Put, $"/api/connections/{store}", Json("{\"connectionString\":\"Host=x\"}")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Put_rejects_an_empty_connection_string()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/connections/postgis", Json("{\"connectionString\":\"  \"}")));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid.arguments", (await BodyAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_blind_save_surfaces_as_unavailable_on_next_use()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var saved = await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/connections/postgis", Json($"{{\"connectionString\":\"{BlindSecret}\"}}")));
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var catalogue = await client.GetAsync("/api/catalogue?store=postgis");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, catalogue.StatusCode);
        Assert.Equal("store.unavailable", (await BodyAsync(catalogue)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_removes_the_override()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/connections/postgis", Json($"{{\"connectionString\":\"{BlindSecret}\"}}")));

        var deleted = await client.SendAsync(Authorized(HttpMethod.Delete, "/api/connections/postgis"));

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await BodyAsync(deleted)).GetBoolean());
        Assert.Empty((await BodyAsync(await client.SendAsync(Authorized(HttpMethod.Get, "/api/connections")))).EnumerateArray());

        var again = await client.SendAsync(Authorized(HttpMethod.Delete, "/api/connections/postgis"));
        Assert.False((await BodyAsync(again)).GetBoolean());
    }

    [Fact]
    public async Task Reload_picks_up_on_disk_deletion()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        await client.SendAsync(Authorized(
            HttpMethod.Put, "/api/connections/postgis", Json($"{{\"connectionString\":\"{BlindSecret}\"}}")));
        File.Delete(Path.Combine(_directory, "connections.json"));

        var reloaded = await client.SendAsync(Authorized(HttpMethod.Post, "/api/connections/reload", Json("{}")));

        Assert.Equal(HttpStatusCode.OK, reloaded.StatusCode);
        Assert.Empty((await BodyAsync(reloaded)).GetProperty("connections").EnumerateArray());
    }

    [Fact]
    public async Task Overrides_survive_a_host_restart()
    {
        using (var factory = Factory())
        {
            var client = factory.CreateClient();
            var saved = await client.SendAsync(Authorized(
                HttpMethod.Put, "/api/connections/postgis", Json($"{{\"connectionString\":\"{BlindSecret}\"}}")));
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        }

        using (var factory = Factory())
        {
            var client = factory.CreateClient();
            var listed = await client.SendAsync(Authorized(HttpMethod.Get, "/api/connections"));
            Assert.Equal("postgis", Assert.Single((await BodyAsync(listed)).EnumerateArray()).GetProperty("store").GetString());
        }
    }

    [Fact]
    public async Task Without_a_token_the_connection_routes_are_401_and_wrong_is_403()
    {
        using var factory = Factory();
        var client = factory.CreateClient();

        var missing = await client.PutAsync("/api/connections/postgis", Json("{\"connectionString\":\"Host=x\"}"));
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);

        var wrongRequest = new HttpRequestMessage(HttpMethod.Put, "/api/connections/postgis")
        {
            Content = Json("{\"connectionString\":\"Host=x\"}"),
        };
        wrongRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "nope");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(wrongRequest)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/connections")).StatusCode);
    }

    private sealed class ConnectionFactory(string connectionsPath) : SpatialHostFactory
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Spatial:Admin:Token", Token);
            builder.UseSetting("Spatial:StoreConnections:Path", connectionsPath);
        }
    }
}
