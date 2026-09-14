using System.Net;
using System.Text.Json;

namespace Spatial.Host.Tests;

/// <summary>
/// Live Esri fixture-refresh probes (T-065): nightly/explicit only.
///
/// The default suite replays checked-in fixtures with no network
/// (<see cref="EsriDocsReplayTests"/>, <c>RealWorldFixtureTests</c>); these
/// probes hit the public, unauthenticated services the fixtures were
/// recorded from and are skipped unless <c>SPATIAL_ESRI_LIVE=1</c>:
/// <c>eng/refresh-esri-fixtures.sh --check</c> runs the same sources as a
/// shell probe with a diff summary. Never gate a PR on these: live public
/// services drift, throttle and go down, and that must file a refresh task,
/// not fail a build.
/// </summary>
[Trait("Category", "Live")]
public sealed class EsriLiveRefreshTests
{
    private const string SamplesBase = "https://sampleserver6.arcgisonline.com";
    private const string BasemapBase = "https://services.arcgisonline.com";

    private static bool LiveEnabled =>
        string.Equals(Environment.GetEnvironmentVariable("SPATIAL_ESRI_LIVE"), "1", StringComparison.OrdinalIgnoreCase);

    private static void SkipUnlessLive() =>
        Skip.If(
            !LiveEnabled,
            "Live-network probe (nightly/explicit only): set SPATIAL_ESRI_LIVE=1 to run. "
            + "The default suite replays checked-in fixtures offline; drift goes through "
            + "eng/refresh-esri-fixtures.sh and eng/tasks add, never through a PR gate.");

    private static HttpClient LiveClient() => new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// The live GeometryServer still answers the project probe shape the
    /// refresh script diffs (a non-empty <c>geometries</c> array).
    /// </summary>
    [SkippableFact]
    public async Task Live_geometry_server_project_probe_keeps_its_shape()
    {
        SkipUnlessLive();
        using var client = LiveClient();
        const string url = SamplesBase
            + "/arcgis/rest/services/Utilities/Geometry/GeometryServer/project"
            + "?geometries=%7B%22geometryType%22%3A%22esriGeometryPoint%22%2C%22geometries%22%3A%5B%7B%22x%22%3A-117%2C%22y%22%3A34%7D%5D%7D"
            + "&inSR=4326&outSR=3857&f=json";

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var geometries = body.RootElement.GetProperty("geometries");
        Assert.NotEmpty(geometries.EnumerateArray());
    }

    /// <summary>
    /// The live Canvas basemap root still carries the service envelope keys
    /// the captured corpus was recorded from.
    /// </summary>
    [SkippableFact]
    public async Task Live_basemap_root_keeps_its_service_envelope()
    {
        SkipUnlessLive();
        using var client = LiveClient();
        const string url = BasemapBase + "/arcgis/rest/services/Canvas/World_Dark_Gray_Base/MapServer?f=json";

        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.TryGetProperty("layers", out _), "Live basemap root lost its 'layers' roster.");
        Assert.True(body.RootElement.TryGetProperty("currentVersion", out _), "Live basemap root lost 'currentVersion'.");
    }
}

/// <summary>
/// Guards the T-065 contract from the offline side: the PR gate
/// (<c>eng/verify.sh</c>) never invokes the live refresh script, and the
/// refresh script exists as a committed, executable entry point. Always runs.
/// </summary>
[Trait("Category", "Offline")]
public sealed class EsriRefreshGateTests
{
    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "eng", "verify.sh")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, "Could not locate the repository root (eng/verify.sh) from the test output.");
        return directory.FullName;
    }

    /// <summary>The default gate stays offline: no network refresh inside verify.</summary>
    [Fact]
    public void Refresh_script_is_not_part_of_the_default_gate()
    {
        var verify = File.ReadAllText(Path.Combine(RepoRoot(), "eng", "verify.sh"));
        Assert.DoesNotContain("refresh-esri-fixtures", verify);
    }

    /// <summary>The nightly/explicit entry point exists with its offline contract documented.</summary>
    [Fact]
    public void Refresh_script_exists_and_states_its_offline_contract()
    {
        var script = File.ReadAllText(Path.Combine(RepoRoot(), "eng", "refresh-esri-fixtures.sh"));
        Assert.Contains("NEVER runs in eng/verify.sh", script);
        Assert.Contains("--check", script);
        Assert.Contains("--write", script);
    }
}
