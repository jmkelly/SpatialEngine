using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Spatial.PluginSdk.Http;

namespace Spatial.Host.Tests;

/// <summary>
/// The remaining public surfaces: the plugin listing (empty without a
/// supervisor), the health endpoints, and the OpenAPI description of the
/// public HTTP contracts (plan §12).
/// </summary>
public sealed class HostPlatformTests : IClassFixture<HostApiTestFactory>
{
    private readonly HostApiTestFactory _factory;

    public HostPlatformTests(HostApiTestFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client => _factory.CreateClient();

    [Fact]
    public async Task Plugins_list_is_empty_without_a_supervisor()
    {
        var response = await Client.GetAsync("/api/plugins");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plugins = await response.Content.ReadFromJsonAsync<PluginDto[]>(HostApiJson.Options);
        Assert.NotNull(plugins);
        Assert.Empty(plugins);
    }

    [Fact]
    public async Task Unknown_plugin_is_404()
    {
        var response = await Client.GetAsync("/api/plugins/nts@1");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Ready_reports_ready_with_zero_plugins()
    {
        var response = await Client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("ready", body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("plugins").GetInt32());
    }

    [Fact]
    public async Task Openapi_describes_the_public_contracts()
    {
        var response = await Client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var paths = document.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/api/capabilities", out _));
        Assert.True(paths.TryGetProperty("/api/invocations", out _));
        Assert.True(paths.TryGetProperty("/api/jobs/{id}", out _));
        var schema = document.GetProperty("components").GetProperty("schemas");
        Assert.True(schema.TryGetProperty("InvocationResponse", out _));
    }
}