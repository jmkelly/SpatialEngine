using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>
/// The read-only <c>map</c> verbs (ADR-0052/0053): listing every map and
/// exporting one map's GeoServices projection, including the empty catalogue,
/// the unknown map, the unknown format and the map with no projection.
/// </summary>
public sealed class MapQueryCommandTests
{
    private static FakeSpatialGateway WithMaps(params Map[] maps) => new()
    {
        Maps = maps,
        MapsByName = maps.ToDictionary(map => map.Name, StringComparer.Ordinal),
    };

    private static Map FeatureMap(string name = "World") => new(
        name,
        "memory",
        [new MapLayer("public.world", 0, "Countries")],
        [MapService.Feature]);

    [Fact]
    public async Task List_reports_no_maps_when_the_catalogue_is_empty()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "map", "list");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("No maps.", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task List_names_every_map()
    {
        var run = await CliHarness.RunAsync(
            WithMaps(FeatureMap("World"), FeatureMap("Cities")),
            "map", "list");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("World", run.Output, StringComparison.Ordinal);
        Assert.Contains("Cities", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_renders_a_map_with_its_endpoint()
    {
        var run = await CliHarness.RunAsync(WithMaps(FeatureMap()), "map", "show", "World");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("World", run.Output, StringComparison.Ordinal);
        Assert.Contains("FeatureServer", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Show_reports_not_found_for_an_unknown_map()
    {
        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "map", "show", "Missing");

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Contains("not.found", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_detail_renders_the_map()
    {
        var run = await CliHarness.RunAsync(WithMaps(FeatureMap()), "map", "export", "World");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("World", run.Output, StringComparison.Ordinal);
        Assert.Contains("FeatureServer", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_url_prints_only_the_endpoint()
    {
        var run = await CliHarness.RunAsync(
            WithMaps(FeatureMap()),
            "map", "export", "World",
            "--format", "url");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Contains("World/FeatureServer", run.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_rejects_an_unknown_format()
    {
        var run = await CliHarness.RunAsync(
            WithMaps(FeatureMap()),
            "map", "export", "World",
            "--format", "pdf");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("format", run.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Export_reports_not_found_for_an_unknown_map()
    {
        var run = await CliHarness.RunAsync(
            new FakeSpatialGateway(),
            "map", "export", "Missing",
            "--format", "url");

        Assert.Equal(ExitCodes.NotFound, run.ExitCode);
        Assert.Contains("not.found", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_url_without_a_projection_is_a_usage_error()
    {
        var draft = new Map("Draft", "memory", [], []);
        var run = await CliHarness.RunAsync(
            WithMaps(draft),
            "map", "export", "Draft",
            "--format", "url");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("no GeoServices projection", run.Error, StringComparison.Ordinal);
    }
}
