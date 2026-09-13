using Spatial.Core.Features;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The MapServer M0 metadata lowering (ADR-0047): neutral <see cref="LayerStyle"/>
/// to a simple renderer, colour parsing, units and layer metadata. Pure, so it
/// is pinned without a host.
/// </summary>
public sealed class EsriMapModelTests
{
    [Theory]
    [InlineData("#ff0000", 1.0, 255, 0, 0, 255)]
    [InlineData("#00ff00", 0.5, 0, 255, 0, 128)]
    [InlineData("#abc", 0.0, 170, 187, 204, 0)]
    public void Color_parses_hex_and_applies_opacity(string hex, double opacity, int r, int g, int b, int a)
    {
        Assert.Equal([r, g, b, a], EsriMapModel.Color(hex, opacity));
    }

    [Fact]
    public void A_point_style_lowers_to_a_circle_marker()
    {
        var renderer = EsriMapModel.Renderer("esriGeometryPoint", new LayerStyle("#123456", Radius: 7, LineWidth: 3));
        var symbol = Assert.IsType<EsriPointSymbol>(renderer.Symbol);

        Assert.Equal("simple", renderer.Type);
        Assert.Equal("esriSMSCircle", symbol.Style);
        Assert.Equal(14, symbol.Size);
        Assert.Equal([18, 52, 86, 255], symbol.Color);
        Assert.Equal(3, symbol.Outline.Width);
    }

    [Fact]
    public void A_polyline_style_lowers_to_a_line()
    {
        var renderer = EsriMapModel.Renderer("esriGeometryPolyline", new LayerStyle("#112233", LineWidth: 2.5));
        var symbol = Assert.IsType<EsriLineSymbol>(renderer.Symbol);

        Assert.Equal("esriSLSSolid", symbol.Style);
        Assert.Equal(2.5, symbol.Width);
    }

    [Fact]
    public void A_polygon_style_lowers_to_a_fill_with_an_outline()
    {
        var renderer = EsriMapModel.Renderer("esriGeometryPolygon", new LayerStyle("#112233", Opacity: 0.4));
        var symbol = Assert.IsType<EsriFillSymbol>(renderer.Symbol);

        Assert.Equal("esriSFSSolid", symbol.Style);
        Assert.Equal(102, symbol.Color[3]);
        Assert.Equal("esriSLSSolid", symbol.Outline.Style);
    }

    [Fact]
    public void A_missing_style_falls_back_to_the_neutral_default()
    {
        var renderer = EsriMapModel.Renderer("esriGeometryPoint", null);
        var symbol = Assert.IsType<EsriPointSymbol>(renderer.Symbol);

        Assert.Equal(EsriMapModel.Color(LayerStyle.Default.Color, 1.0), symbol.Color);
    }

    [Theory]
    [InlineData(4326, "esriDecimalDegrees")]
    [InlineData(3857, "esriMeters")]
    [InlineData(27700, "esriUnknownUnits")]
    public void Units_follows_the_srid(int srid, string expected) => Assert.Equal(expected, EsriMapModel.Units(srid));

    [Fact]
    public void Layer_metadata_carries_the_drawing_info_and_read_only_capabilities()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, nullable: false),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);
        var dataset = new DatasetDescription("memory.parks", "public", "parks", "geometry", 4326, "point", 2, ["id"], schema);

        var layer = EsriMapModel.Layer(3, "Parks", dataset, new LayerStyle("#000000"));

        Assert.Equal(3, layer.Id);
        Assert.Equal("Parks", layer.Name);
        Assert.Equal("esriGeometryPoint", layer.GeometryType);
        Assert.Equal("Query", layer.Capabilities);
        Assert.Equal("simple", layer.DrawingInfo.Renderer.Type);
        Assert.Equal(3, layer.Fields.Count);
    }

    [Fact]
    public void The_root_advertises_query_and_data_but_never_map()
    {
        var publication = new Publication("parks_map", PublicationKind.Map, "memory", [new PublicationLayer("memory.parks", 0)]);

        var root = EsriMapModel.Root(publication, [], null, null, "esriDecimalDegrees");

        Assert.Equal("parks_map", root.MapName);
        Assert.Equal("Query,Data", root.Capabilities);
        Assert.DoesNotContain("Map", root.Capabilities.Split(','));
    }
}
