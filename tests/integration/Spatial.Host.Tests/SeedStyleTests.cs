using System.Text.Json.Nodes;
using Spatial.Contracts;
using Spatial.Contracts.Http;
using Spatial.Host.Api;

namespace Spatial.Host.Tests;

/// <summary>
/// The seed map layer lowering (ADR-0047/ADR-0053): the compact draw recipe
/// becomes the persisted MapLibre fragment, with one spec per geometry the
/// layer draws.
/// </summary>
public sealed class SeedStyleTests
{
    private static SeedMapLayer Layer(string geometry, SeedLayerStyle? style = null) =>
        new("public.cities", "Cities", geometry, style);

    private static JsonArray Specs(string json) => JsonNode.Parse(json)!.AsArray();

    [Fact]
    public void A_polygon_layer_lowers_to_fill_and_line()
    {
        var specs = Specs(SeedStyle.Lower(Layer("polygon", new SeedLayerStyle("#ff0000", 0.5, 3, 7, true))));

        Assert.Equal(["fill", "line"], specs.Select(spec => spec!["type"]!.GetValue<string>()));
        Assert.Equal("#ff0000", specs[0]!["paint"]!["fill-color"]!.GetValue<string>());
        Assert.Equal(0.5, specs[0]!["paint"]!["fill-opacity"]!.GetValue<double>());
        Assert.Equal("#ff0000", specs[0]!["paint"]!["fill-outline-color"]!.GetValue<string>());
        Assert.Equal(3.0, specs[0]!["paint"]!["fill-outline-width"]!.GetValue<double>());
        Assert.Equal("#ff0000", specs[1]!["paint"]!["line-color"]!.GetValue<string>());
        Assert.Equal(3.0, specs[1]!["paint"]!["line-width"]!.GetValue<double>());
    }

    [Fact]
    public void A_mixed_layer_lowers_to_fill_line_and_circle()
    {
        var specs = Specs(SeedStyle.Lower(Layer("mixed")));

        Assert.Equal(["fill", "line", "circle"], specs.Select(spec => spec!["type"]!.GetValue<string>()));
        Assert.Equal("#4fc3f7", specs[0]!["paint"]!["fill-color"]!.GetValue<string>());
        Assert.Equal(5.0, specs[2]!["paint"]!["circle-radius"]!.GetValue<double>());
    }

    [Fact]
    public void A_point_layer_lowers_to_a_circle_alone()
    {
        var specs = Specs(SeedStyle.Lower(Layer("point")));

        Assert.Equal(["circle"], specs.Select(spec => spec!["type"]!.GetValue<string>()));
    }

    [Fact]
    public void A_line_layer_lowers_to_a_line_alone()
    {
        var specs = Specs(SeedStyle.Lower(Layer("line", new SeedLayerStyle(Visible: false))));

        var spec = Assert.Single(specs);
        Assert.Equal("line", spec!["type"]!.GetValue<string>());
        Assert.Equal("none", spec["layout"]!["visibility"]!.GetValue<string>());
    }

    [Fact]
    public void An_outline_color_separates_the_stroke_from_the_fill()
    {
        // Hollow linework (the Esri Census states/counties look): a
        // transparent fill color with an explicit opaque dark outline. The
        // fill opacity stays 1 — the renderer scales the stroke by it.
        var specs = Specs(SeedStyle.Lower(Layer("polygon", new SeedLayerStyle("#00000000", 1, 2, 7, true, "#000000", 1))));

        Assert.Equal("#00000000", specs[0]!["paint"]!["fill-color"]!.GetValue<string>());
        Assert.Equal(1.0, specs[0]!["paint"]!["fill-opacity"]!.GetValue<double>());
        Assert.Equal("#000000", specs[0]!["paint"]!["fill-outline-color"]!.GetValue<string>());
        Assert.Equal(2.0, specs[0]!["paint"]!["fill-outline-width"]!.GetValue<double>());
        Assert.Equal("#000000", specs[1]!["paint"]!["line-color"]!.GetValue<string>());
        Assert.Equal(2.0, specs[1]!["paint"]!["line-width"]!.GetValue<double>());
        Assert.Equal(1.0, specs[1]!["paint"]!["line-opacity"]!.GetValue<double>());
    }

    [Fact]
    public void Without_an_outline_color_the_stroke_follows_the_fill()
    {
        var specs = Specs(SeedStyle.Lower(Layer("polygon", new SeedLayerStyle("#ff0000", 0.5, 3, 7, true))));

        Assert.Equal("#ff0000", specs[0]!["paint"]!["fill-outline-color"]!.GetValue<string>());
        Assert.Equal("#ff0000", specs[1]!["paint"]!["line-color"]!.GetValue<string>());
    }

    [Fact]
    public void An_unknown_geometry_is_refused_by_name()
    {
        var failure = Assert.Throws<SpatialException>(() => SeedStyle.Lower(Layer("triangle")));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("triangle", failure.Message, StringComparison.Ordinal);
    }
}
