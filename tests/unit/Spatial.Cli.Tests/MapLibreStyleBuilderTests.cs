using System.Text.Json.Nodes;
using Spatial.Cli;

namespace Spatial.Cli.Tests;

/// <summary>The compact recipe ↔ persisted MapLibre fragment rule (ADR-0047/0052).</summary>
public sealed class MapLibreStyleBuilderTests
{
    [Fact]
    public void Polygon_lowers_to_fill_then_line()
    {
        var fragment = MapLibreStyleBuilder.Lower(new DrawRecipe("#112233", 0.4, 3), GeometryFamily.Polygon);
        var layers = JsonNode.Parse(fragment)!.AsArray();

        Assert.Equal(2, layers.Count);
        Assert.Equal("fill", layers[0]!["type"]!.GetValue<string>());
        Assert.Equal("#112233", layers[0]!["paint"]!["fill-color"]!.GetValue<string>());
        Assert.Equal("line", layers[1]!["type"]!.GetValue<string>());
        Assert.Equal(3, layers[1]!["paint"]!["line-width"]!.GetValue<double>());
        Assert.DoesNotContain("source-layer", fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Point_lowers_to_a_circle()
    {
        var fragment = MapLibreStyleBuilder.Lower(new DrawRecipe(Radius: 9), GeometryFamily.Point);
        var layers = JsonNode.Parse(fragment)!.AsArray();

        var circle = Assert.Single(layers);
        Assert.Equal("circle", circle!["type"]!.GetValue<string>());
        Assert.Equal(9, circle["paint"]!["circle-radius"]!.GetValue<double>());
    }

    [Fact]
    public void Mixed_lowers_to_fill_line_and_circle()
    {
        var layers = JsonNode.Parse(MapLibreStyleBuilder.Lower(new DrawRecipe(), GeometryFamily.Mixed))!.AsArray();

        Assert.Equal(["fill", "line", "circle"], layers.Select(layer => layer!["type"]!.GetValue<string>()));
    }

    [Fact]
    public void Hidden_sets_visibility_none()
    {
        var layers = JsonNode.Parse(MapLibreStyleBuilder.Lower(new DrawRecipe(Visible: false), GeometryFamily.Line))!.AsArray();

        Assert.Equal("none", layers[0]!["layout"]!["visibility"]!.GetValue<string>());
    }

    [Fact]
    public void TryDescribe_round_trips_a_recipe()
    {
        var recipe = new DrawRecipe("#ff0000", 0.5, 4, 7, false);

        var inferred = MapLibreStyleBuilder.TryDescribe(
            MapLibreStyleBuilder.Lower(recipe, GeometryFamily.Mixed), out var parsed, out var family);

        Assert.True(inferred);
        Assert.Equal(GeometryFamily.Mixed, family);
        Assert.Equal(recipe, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("[{\"type\":\"background\"}]")]
    public void TryDescribe_rejects_unrecognisable_fragments(string? fragment)
    {
        Assert.False(MapLibreStyleBuilder.TryDescribe(fragment, out _, out _));
    }
}
