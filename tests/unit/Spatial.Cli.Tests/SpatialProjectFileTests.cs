using Spatial.Cli;

namespace Spatial.Cli.Tests;

/// <summary>The project-file load/save contract (ADR-0052).</summary>
public sealed class SpatialProjectFileTests
{
    [Fact]
    public void Save_and_load_round_trip_a_styled_project()
    {
        var path = TempPath();
        var project = new SpatialProject(
            1,
            [new ProjectDataset("public.world", 4326, "world.geojson", Identity: "none")],
            [
                new ProjectMap(
                    "World",
                    "map",
                    "memory",
                    "described",
                    "copyright",
                    [
                        new ProjectLayer(
                            "public.world",
                            "Countries",
                            "polygon",
                            new ProjectStyle("#ff0000", 0.5, 4, 7, false)),
                    ]),
            ]);

        SpatialProjectFile.Save(path, project);
        var loaded = SpatialProjectFile.Load(path);

        Assert.Equal(1, loaded.Version);
        var dataset = Assert.Single(loaded.Datasets);
        Assert.Equal("public.world", dataset.Dataset);
        Assert.Equal(4326, dataset.Srid);
        Assert.Equal("world.geojson", dataset.Source);
        Assert.Equal("geojson", dataset.Format);
        Assert.Equal("none", dataset.Identity);
        Assert.Null(dataset.SourceSrid);
        var map = Assert.Single(loaded.Maps);
        Assert.Equal("World", map.Name);
        Assert.Equal("map", map.Kind);
        Assert.Equal("memory", map.Store);
        Assert.Equal("described", map.Description);
        Assert.Equal("copyright", map.Copyright);
        var layer = Assert.Single(map.Layers!);
        Assert.Equal("public.world", layer.Dataset);
        Assert.Equal("Countries", layer.Name);
        Assert.Equal("polygon", layer.Geometry);
        Assert.Equal(new ProjectStyle("#ff0000", 0.5, 4, 7, false), layer.Style);
    }

    [Fact]
    public void Serialize_writes_camel_case_and_omits_nulls()
    {
        var json = SpatialProjectFile.Serialize(
            new SpatialProject(1, [new ProjectDataset("public.world", 4326, null)], []));

        Assert.Contains("\"datasets\"", json, StringComparison.Ordinal);
        Assert.Contains("\"srid\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"sourceSrid\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"identity\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_version_fails()
    {
        var path = Write("{\"version\":2,\"datasets\":[],\"maps\":[]}");

        Assert.Throws<CliUsageException>(() => SpatialProjectFile.Load(path));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"version\":1}")]
    public void A_document_without_datasets_or_maps_fails(string json)
    {
        var path = Write(json);

        Assert.Throws<CliUsageException>(() => SpatialProjectFile.Load(path));
    }

    [Fact]
    public void Malformed_json_fails()
    {
        var path = Write("{ not valid json");

        Assert.Throws<CliUsageException>(() => SpatialProjectFile.Load(path));
    }

    [Fact]
    public void A_missing_file_fails()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");

        Assert.Throws<CliUsageException>(() => SpatialProjectFile.Load(path));
    }

    [Fact]
    public void Project_style_round_trips_through_a_draw_recipe()
    {
        var style = new ProjectStyle("#123456", 0.25, 3, 8, false);
        Assert.Equal(style, ProjectStyle.FromRecipe(style.ToRecipe()));

        var recipe = new DrawRecipe("#abcdef", 0.5, 6, 2, true);
        Assert.Equal(recipe, ProjectStyle.FromRecipe(recipe).ToRecipe());
    }

    private static string Write(string json)
    {
        var path = TempPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, json);
        return path;
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "spatial-cli-tests", Guid.NewGuid().ToString("N"), "spatial.json");
}
