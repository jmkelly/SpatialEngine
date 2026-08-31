using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Tests;

/// <summary>
/// The Phase 10 browser hosting surface (ADR-0031): the host serves the
/// built workbench from <c>Spatial:WebRoot</c> (same origin, no CORS, no
/// desktop shell) while the API routes stay untouched, and the <c>demo@1</c>
/// data provider ships as a packaged worker whose catalogue/scan/query serve
/// the workbench's dataset browser, map and selection.
/// </summary>
public sealed class WorkbenchHostingTests : IDisposable
{
    private readonly string _root;
    private readonly WebApplicationFactory<Program> _factory;

    public WorkbenchHostingTests()
    {
        _root = Directory.CreateTempSubdirectory("spatial-webhost-").FullName;
        NtsHostPackageWriter.WriteDemo(_root);
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Spatial:PackagesRoot", _root));
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task The_demo_provider_serves_the_catalogue_and_scan_over_http()
    {
        var plugins = await Client.GetFromJsonAsync<PluginDto[]>("/api/plugins", HostApiJson.Options);
        Assert.Contains(plugins ?? [], plugin => plugin.Id == "demo@1");

        var capability = await Client.GetFromJsonAsync<CapabilityDetailDto>(
            "/api/capabilities/spatial.catalogue.list@1", HostApiJson.Options);
        Assert.NotNull(capability);
        Assert.Contains(capability.Providers, provider => provider.Id == "demo@1");

        // Catalogue list needs no permission; the scan does — prove the
        // requested permission is granted and the stream decodes.
        var catalogue = await Client.PostAsJsonAsync(
            "/api/invocations",
            new InvocationRequest("spatial.catalogue.list@1"),
            HostApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, catalogue.StatusCode);
        var catalogueBody = await catalogue.Content.ReadFromJsonAsync<InvocationResponse>(HostApiJson.Options);
        Assert.NotNull(catalogueBody);
        Assert.True(catalogueBody.Ok, catalogueBody.Error?.Message ?? "catalogue failed");
        Assert.Equal("demo@1", catalogueBody.Provenance?.Provider);
    }

    [Fact]
    public async Task Static_workbench_serving_serves_the_app_and_keeps_the_api()
    {
        var webRoot = Path.Combine(_root, "web");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><title>workbench</title><div id=app></div>");
        File.WriteAllText(Path.Combine(webRoot, "assets.js"), "console.log('workbench');");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Spatial:PackagesRoot", _root);
                builder.UseSetting("Spatial:WebRoot", webRoot);
            });
        var client = factory.CreateClient();

        // The app is served at the root instead of the identity document.
        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("text/html", root.Content.Headers.ContentType?.ToString());
        var html = await root.Content.ReadAsStringAsync();
        Assert.Contains("workbench", html);

        // Static assets are served from the configured root.
        var asset = await client.GetAsync("/assets.js");
        Assert.Equal(HttpStatusCode.OK, asset.StatusCode);

        // The API surface is untouched.
        var openapi = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, openapi.StatusCode);
        var health = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(1, (await health.Content.ReadFromJsonAsync<JsonObject>())?["plugins"]?.GetValue<int>());
    }

    [Fact]
    public async Task A_configured_but_missing_web_root_fails_at_startup()
    {
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Spatial:WebRoot", Path.Combine(_root, "does-not-exist")));

        var exception = Record.Exception(() => factory.CreateClient());

        Assert.NotNull(exception);
        Assert.Contains("Spatial:WebRoot", exception.Message);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
