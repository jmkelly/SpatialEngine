using Spatial.Cli;
using Spatial.Contracts.Providers;

namespace Spatial.Cli.Tests;

/// <summary>
/// The pinned JSON wire key of every <see cref="MapServiceKind"/>: the project
/// file records one key per service and <c>map view</c> joins them, so the
/// exported project and the host's <c>maps.json</c> must agree with the keys
/// the enum itself pins.
/// </summary>
public sealed class ServiceWireKeyTests
{
    public static TheoryData<MapServiceKind, string> Keys
    {
        get
        {
            var data = new TheoryData<MapServiceKind, string>();
            data.Add(MapServiceKind.FeatureServer, "feature");
            data.Add(MapServiceKind.MapServer, "map");
            data.Add(MapServiceKind.Tiles, "tiles");
            data.Add(MapServiceKind.Wms, "wms");
            data.Add(MapServiceKind.Wfs, "wfs");
            data.Add(MapServiceKind.ImageServer, "image");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Project_export_records_the_pinned_key(MapServiceKind kind, string key)
    {
        var gateway = new FakeSpatialGateway
        {
            Maps = [new Map("World", "memory", [new MapLayer("public.world", 0)], [kind])],
        };
        var path = Path.Combine(
            Path.GetTempPath(), "spatial-cli-wire-key", Guid.NewGuid().ToString("N"), "spatial.json");

        var run = await CliHarness.RunAsync(gateway, "project", "export", "--project", path);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var map = Assert.Single(SpatialProjectFile.Load(path).Maps);
        Assert.Equal(key, map.Kind);
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public async Task Map_view_joins_every_service_key(MapServiceKind kind, string key)
    {
        var gateway = new FakeSpatialGateway
        {
            MapsByName = new Dictionary<string, Map>(StringComparer.Ordinal)
            {
                ["World"] = new Map("World", "memory", [new MapLayer("public.world", 0)], [kind]),
            },
        };

        var run = await CliHarness.RunAsync(gateway, "map", "show", "World", "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        using var document = System.Text.Json.JsonDocument.Parse(run.Output);
        Assert.Equal(key, document.RootElement.GetProperty("data").GetProperty("kind").GetString());
    }
}
