using System.Text.Json;
using Spatial.Cli;
using Spatial.PluginSdk.Providers;

namespace Spatial.Cli.Tests;

/// <summary>The <c>project</c> commands against a fake gateway (ADR-0052).</summary>
public sealed class ProjectCommandTests
{
    [Fact]
    public async Task Init_writes_a_starter_file_and_refuses_to_overwrite_without_force()
    {
        var path = TempPath();

        var first = await CliHarness.RunAsync(new FakeSpatialGateway(), "project", "init", "--project", path);
        Assert.Equal(ExitCodes.Success, first.ExitCode);
        Assert.True(File.Exists(path));

        var second = await CliHarness.RunAsync(new FakeSpatialGateway(), "project", "init", "--project", path);
        Assert.Equal(ExitCodes.Usage, second.ExitCode);

        var forced = await CliHarness.RunAsync(new FakeSpatialGateway(), "project", "init", "--project", path, "--force");
        Assert.Equal(ExitCodes.Success, forced.ExitCode);
    }

    [Fact]
    public async Task Plan_reports_missing_and_existing_datasets_and_maps_without_mutating()
    {
        var path = WriteProject(
            [
                new ProjectDataset("public.existing", 4326, "existing.geojson"),
                new ProjectDataset("public.missing", 4326, "missing.geojson"),
            ],
            [
                new ProjectMap(
                    "World",
                    "map",
                    Layers:
                    [
                        new ProjectLayer("public.existing"),
                        new ProjectLayer("public.missing"),
                    ]),
            ]);
        var gateway = new FakeSpatialGateway
        {
            Datasets = [new DatasetSummary("public.existing", "public", "existing", "geom", 4326, 1)],
        };

        var run = await CliHarness.RunAsync(gateway, "project", "plan", "--project", path, "--json");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
        Assert.Empty(gateway.PutCalls);
        using var document = JsonDocument.Parse(run.Output);
        var datasets = document.RootElement.GetProperty("data").GetProperty("datasets");
        Assert.True(FindDataset(datasets, "public.existing").GetProperty("exists").GetBoolean());
        Assert.False(FindDataset(datasets, "public.existing").GetProperty("willIngest").GetBoolean());
        Assert.False(FindDataset(datasets, "public.missing").GetProperty("exists").GetBoolean());
        Assert.True(FindDataset(datasets, "public.missing").GetProperty("willIngest").GetBoolean());
        var maps = document.RootElement.GetProperty("data").GetProperty("maps");
        Assert.Equal(2, Assert.Single(maps.EnumerateArray()).GetProperty("layers").GetInt32());
    }

    [Fact]
    public async Task Apply_ingests_missing_reuses_existing_and_publishes_maps()
    {
        var path = WriteProject(
            [
                new ProjectDataset("public.existing", 4326, "existing.geojson"),
                new ProjectDataset("public.missing", 4326, "missing.geojson"),
            ],
            [
                new ProjectMap(
                    "World",
                    "map",
                    Layers:
                    [
                        new ProjectLayer("public.existing", "Existing", "polygon"),
                        new ProjectLayer("public.missing", "Missing", "point"),
                    ]),
            ]);
        var gateway = new FakeSpatialGateway
        {
            Datasets = [new DatasetSummary("public.existing", "public", "existing", "geom", 4326, 1)],
        };

        var run = await CliHarness.RunAsync(gateway, "project", "apply", "--project", path, "--token", "secret");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var ingest = Assert.Single(gateway.IngestCalls);
        Assert.Equal("public.missing", ingest.Upload.Dataset);
        Assert.Equal("secret", ingest.Token);
        Assert.Equal("missing.geojson", ingest.Upload.FileName);
        var put = Assert.Single(gateway.PutCalls);
        Assert.Equal("secret", put.Token);
        Assert.Equal([MapService.Map], put.Map.Services);
        Assert.Equal(2, put.Map.Layers.Count);
    }

    [Fact]
    public async Task Apply_force_reingests_an_existing_dataset()
    {
        var path = WriteProject(
            [new ProjectDataset("public.existing", 4326, "existing.geojson")],
            []);
        var gateway = new FakeSpatialGateway
        {
            Datasets = [new DatasetSummary("public.existing", "public", "existing", "geom", 4326, 1)],
        };

        var run = await CliHarness.RunAsync(gateway, "project", "apply", "--project", path, "--token", "t", "--force");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Single(gateway.IngestCalls);
    }

    [Fact]
    public async Task Apply_requires_a_token_when_it_will_mutate()
    {
        var project = new SpatialProject(
            1,
            [new ProjectDataset("public.world", 4326, "world.geojson")],
            []);

        await Assert.ThrowsAsync<CliUsageException>(() =>
            ProjectApplier.ApplyAsync(new FakeSpatialGateway(), project, Settings(token: null), force: false));
    }

    [Fact]
    public async Task Apply_rejects_a_missing_source_for_a_missing_dataset()
    {
        var path = WriteProject([new ProjectDataset("public.world", 4326, null)], []);

        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "project", "apply", "--project", path, "--token", "t");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("public.world", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_passes_through_an_http_source()
    {
        var path = WriteProject(
            [new ProjectDataset("public.remote", 4326, "https://example.com/data.geojson")],
            []);
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(gateway, "project", "apply", "--project", path, "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var ingest = Assert.Single(gateway.IngestCalls);
        Assert.Equal("https://example.com/data.geojson", ingest.Source);
        Assert.Equal("data.geojson", ingest.Upload.FileName);
    }

    [Fact]
    public async Task Apply_rejects_an_unknown_map_kind()
    {
        var path = WriteProject([], [new ProjectMap("World", "waffle")]);

        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "project", "apply", "--project", path, "--token", "t");

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("waffle", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_preserves_an_existing_publications_layer_ids()
    {
        var path = WriteProject(
            [],
            [
                new ProjectMap(
                    "World",
                    "map",
                    Layers:
                    [
                        new ProjectLayer("public.b"),
                        new ProjectLayer("public.c"),
                    ]),
            ]);
        var gateway = new FakeSpatialGateway
        {
            Maps =
            [
                new Map(
                    "World",
                    "memory",
                    [
                        new MapLayer("public.a", 0),
                        new MapLayer("public.b", 7),
                    ],
                    [MapService.Map]),
            ],
        };

        var run = await CliHarness.RunAsync(gateway, "project", "apply", "--project", path, "--token", "t");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var layers = Assert.Single(gateway.PutCalls).Map.Layers;
        Assert.Equal(7, layers.Single(layer => layer.Dataset == "public.b").LayerId);
        Assert.Equal(8, layers.Single(layer => layer.Dataset == "public.c").LayerId);
    }

    [Fact]
    public async Task Export_writes_datasets_and_publications_inferring_styles()
    {
        var recipe = new DrawRecipe("#ff0000", 0.5, 4, 5, false);
        var gateway = new FakeSpatialGateway
        {
            Datasets = [new DatasetSummary("public.world", "public", "world", "geom", 4326, 10)],
            Maps =
            [
                new Map(
                    "World",
                    "memory",
                    [
                        new MapLayer(
                            "public.world",
                            0,
                            "Countries",
                            MapLibreStyleBuilder.Lower(recipe, GeometryFamily.Polygon)),
                    ],
                    [MapService.Map],
                    "described",
                    "copyright"),
            ],
        };
        var path = TempPath();

        var run = await CliHarness.RunAsync(gateway, "project", "export", "--project", path);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var project = SpatialProjectFile.Load(path);
        var dataset = Assert.Single(project.Datasets);
        Assert.Equal("public.world", dataset.Dataset);
        Assert.Equal(4326, dataset.Srid);
        Assert.Null(dataset.Source);
        var map = Assert.Single(project.Maps);
        Assert.Equal("World", map.Name);
        Assert.Equal("map", map.Kind);
        Assert.Equal("memory", map.Store);
        Assert.Equal("described", map.Description);
        Assert.Equal("copyright", map.Copyright);
        var layer = Assert.Single(map.Layers!);
        Assert.Equal("public.world", layer.Dataset);
        Assert.Equal("Countries", layer.Name);
        Assert.Equal("polygon", layer.Geometry);
        Assert.Equal(ProjectStyle.FromRecipe(recipe), layer.Style);
    }

    [Fact]
    public async Task Export_uses_mixed_geometry_when_a_layer_has_no_recognised_style()
    {
        var gateway = new FakeSpatialGateway
        {
            Maps =
            [
                new Map("World", "memory", [new MapLayer("public.world", 0)], [MapService.Map]),
            ],
        };
        var path = TempPath();

        var run = await CliHarness.RunAsync(gateway, "project", "export", "--project", path);

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        var map = Assert.Single(SpatialProjectFile.Load(path).Maps);
        var layer = Assert.Single(map.Layers!);
        Assert.Equal("mixed", layer.Geometry);
        Assert.Null(layer.Style);
    }

    [Fact]
    public async Task Apply_dry_run_never_mutates()
    {
        var path = WriteProject(
            [new ProjectDataset("public.world", 4326, "world.geojson")],
            [new ProjectMap("World", "map", Layers: [new ProjectLayer("public.world")])]);
        var gateway = new FakeSpatialGateway();

        var run = await CliHarness.RunAsync(gateway, "project", "apply", "--project", path, "--dry-run");

        Assert.Equal(ExitCodes.Success, run.ExitCode);
        Assert.Empty(gateway.IngestCalls);
        Assert.Empty(gateway.PutCalls);
    }

    [Fact]
    public async Task An_unsupported_version_is_a_usage_error()
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"version\":99,\"datasets\":[],\"maps\":[]}");

        var run = await CliHarness.RunAsync(new FakeSpatialGateway(), "project", "plan", "--project", path);

        Assert.Equal(ExitCodes.Usage, run.ExitCode);
        Assert.Contains("invalid.arguments", run.Error, StringComparison.Ordinal);
    }

    private static JsonElement FindDataset(JsonElement datasets, string id) =>
        datasets.EnumerateArray().Single(element => element.GetProperty("dataset").GetString() == id);

    private static string WriteProject(IReadOnlyList<ProjectDataset> datasets, IReadOnlyList<ProjectMap> maps)
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        SpatialProjectFile.Save(path, new SpatialProject(1, datasets, maps));
        return path;
    }

    private static CliSettings Settings(string? token) => new(
        CliSettings.DefaultHost,
        token,
        CliSettings.DefaultStore,
        CliSettings.DefaultProjectPath,
        CliSettings.DefaultGeoServicesRoot,
        Json: false,
        Quiet: false,
        Verbose: false,
        DryRun: false,
        CliSettings.DefaultTimeout);

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "spatial-cli-tests", Guid.NewGuid().ToString("N"), "spatial.json");
}
