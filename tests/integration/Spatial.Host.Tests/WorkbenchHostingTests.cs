using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Spatial.Host.Tests;

/// <summary>
/// Static workbench serving: the host serves the built app from
/// <c>Spatial:WebRoot</c> while the API routes stay untouched.
/// </summary>
public sealed class WorkbenchHostingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("spatial-webhost-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Static_workbench_serving_serves_the_app_and_keeps_the_api()
    {
        var webRoot = Path.Combine(_root, "web");
        Directory.CreateDirectory(webRoot);
        File.WriteAllText(Path.Combine(webRoot, "index.html"), "<!doctype html><title>workbench</title><div id=app></div>");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Spatial:WebRoot", webRoot));
        var client = factory.CreateClient();

        var app = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);
        Assert.Contains("workbench", await app.Content.ReadAsStringAsync());

        var api = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
    }

    [Fact]
    public async Task Without_a_web_root_the_host_serves_its_identity()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var root = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("Spatial.Host", await root.Content.ReadAsStringAsync());
    }

    [Fact]
    public void A_missing_web_root_fails_fast_naming_the_directory()
    {
        var missing = Path.Combine(_root, "nope");
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting("Spatial:WebRoot", missing));

        var exception = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(missing, exception.Message);
    }
}
