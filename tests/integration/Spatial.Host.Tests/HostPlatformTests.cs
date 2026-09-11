using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    /// <summary>
    /// Drift gate for the generated SDK: the host's live OpenAPI contract
    /// surface must match the committed snapshot the TypeScript SDK is built
    /// from. Without this, a host contract change can land while the snapshot
    /// (and therefore the SDK's wire types) silently goes stale. The
    /// environment-dependent <c>servers</c>/<c>info</c> blocks are ignored; the
    /// paths and component schemas are what consumers depend on.
    /// </summary>
    [Fact]
    public async Task Openapi_surface_matches_the_committed_sdk_snapshot()
    {
        var response = await Client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var live = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        using var snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(SdkSnapshotPath()));

        var livePaths = JsonNode.Parse(Section(live, "paths").GetRawText());
        var snapshotPaths = JsonNode.Parse(Section(snapshot, "paths").GetRawText());
        Assert.True(JsonNode.DeepEquals(livePaths, snapshotPaths), SnapshotMessage("paths"));

        var liveSchemas = JsonNode.Parse(Section(live, "components").GetProperty("schemas").GetRawText());
        var snapshotSchemas = JsonNode.Parse(Section(snapshot, "components").GetProperty("schemas").GetRawText());
        Assert.True(JsonNode.DeepEquals(liveSchemas, snapshotSchemas), SnapshotMessage("component schemas"));
    }

    private static JsonElement Section(JsonDocument document, string name) =>
        document.RootElement.GetProperty(name);

    private static string SnapshotMessage(string section) =>
        $"The host OpenAPI {section} differ from clients/typescript/scripts/openapi.snapshot.json; "
        + "refresh the snapshot and regenerate the SDK types (eng/e2e-web.sh, then npm run generate) after a contract change.";

    private static string SdkSnapshotPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "clients", "typescript", "scripts", "openapi.snapshot.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("could not find the committed OpenAPI snapshot above the test output directory");
    }
}
