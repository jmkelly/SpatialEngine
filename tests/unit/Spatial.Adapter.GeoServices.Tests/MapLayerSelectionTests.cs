using Spatial.Adapter.GeoServices;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The MapServer <c>layers</c> selection parameter (spec §4): all/visible/top
/// and empty select everything, <c>show:</c> selects only the listed ids,
/// <c>hide:</c> selects all but them, and a bare id list selects them.
/// </summary>
public sealed class MapLayerSelectionTests
{
    private static readonly IReadOnlyList<PublishedLayer> Layers =
    [
        new(0, "demo.cities", "Cities"),
        new(2, "demo.points", "Points"),
        new(5, "demo.world_cities", "World"),
    ];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("all")]
    [InlineData("visible")]
    [InlineData("top")]
    public void Everything_is_selected_by_default(string? selection) =>
        Assert.Equal([0, 2, 5], MapLayerSelection.Select(Layers, selection).Select(layer => layer.Id));

    [Fact]
    public void Show_selects_only_the_listed_layers() =>
        Assert.Equal([2, 5], MapLayerSelection.Select(Layers, "show:2,5").Select(layer => layer.Id));

    [Fact]
    public void Hide_selects_every_layer_but_the_listed() =>
        Assert.Equal([0, 2], MapLayerSelection.Select(Layers, "hide:5").Select(layer => layer.Id));

    [Fact]
    public void A_bare_id_list_selects_those_layers() =>
        Assert.Equal([0], MapLayerSelection.Select(Layers, "0").Select(layer => layer.Id));
}
