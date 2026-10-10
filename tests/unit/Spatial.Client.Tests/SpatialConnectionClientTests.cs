using System.Net;
using System.Text.Json;

namespace Spatial.Client.Tests;

/// <summary>
/// The store-connection client: route shapes, the admin bearer and structured
/// error mapping for set/list/remove/reload — all against the stub host.
/// </summary>
public sealed class SpatialConnectionClientTests
{
    [Fact]
    public async Task Set_puts_the_connection_string_and_decodes_the_status()
    {
        string? sent = null;
        using var stub = new StubClient(new StubHttpHandler(request =>
        {
            sent = request.Content!.ReadAsStringAsync().Result;
            return StubHttpHandler.Json(
                """{"store":"postgis","configured":true,"updatedAt":"2026-10-10T12:00:00Z"}""");
        }));

        var status = await stub.Client.SetStoreConnectionAsync("postgis", "Host=db", "token");

        Assert.Equal("postgis", status.Store);
        Assert.True(status.Configured);
        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal(HttpMethod.Put, exchange.Request.Method);
        Assert.Equal("/api/connections/postgis", exchange.Request.RequestUri?.AbsolutePath);
        var body = JsonDocument.Parse(sent!);
        Assert.Equal("Host=db", body.RootElement.GetProperty("connectionString").GetString());
        Assert.Equal("Bearer", exchange.Request.Headers.Authorization?.Scheme);
    }

    [Fact]
    public async Task List_decodes_statuses()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """[{"store":"postgis","configured":true,"updatedAt":"2026-10-10T12:00:00Z"}]""")));

        var statuses = await stub.Client.ListStoreConnectionsAsync("token");

        Assert.Equal("postgis", Assert.Single(statuses).Store);
        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal(HttpMethod.Get, exchange.Request.Method);
        Assert.Equal("/api/connections", exchange.Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Remove_deletes_and_decodes_the_flag()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json("true")));

        Assert.True(await stub.Client.RemoveStoreConnectionAsync("postgis", "token"));

        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal(HttpMethod.Delete, exchange.Request.Method);
        Assert.Equal("/api/connections/postgis", exchange.Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Reload_posts_and_decodes_the_statuses()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"connections":[{"store":"postgis","configured":true,"updatedAt":null}]}""")));

        var response = await stub.Client.ReloadStoreConnectionsAsync("token");

        Assert.Equal("postgis", Assert.Single(response.Connections).Store);
        var exchange = Assert.Single(stub.Handler.Exchanges);
        Assert.Equal(HttpMethod.Post, exchange.Request.Method);
        Assert.Equal("/api/connections/reload", exchange.Request.RequestUri?.AbsolutePath);
    }

    [Fact]
    public async Task Unknown_store_maps_the_structured_error()
    {
        using var stub = new StubClient(new StubHttpHandler(_ => StubHttpHandler.Json(
            """{"code":"invalid.arguments","message":"no such store"}""", HttpStatusCode.BadRequest)));

        var exception = await Assert.ThrowsAsync<SpatialClientException>(() =>
            stub.Client.SetStoreConnectionAsync("memory", "Host=db", "token"));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid.arguments", exception.Code);
    }
}
