using System.Text.Json;
using Spatial.Adapter.GeoServices;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The persisted MapLibre style to Esri <c>drawingInfo</c>/<c>labelingInfo</c>/
/// <c>domains</c> projection (spec §12–15, ADR-0050): flat symbols become
/// <c>simple</c>, equal-value siblings a <c>uniqueValue</c>, interval
/// siblings a <c>classBreaks</c>, single-field <c>symbol</c> fragments label
/// classes, and renderer fields back coded-value/range domains. Every
/// projection is asserted through the served JSON shape.
/// </summary>
public sealed class MapStyleProjectionTests
{
    [Fact]
    public void A_fill_fragment_becomes_a_solid_fill_symbol()
    {
        var drawing = MapStyleProjection.Project(
            """[{"type":"fill","paint":{"fill-color":"#112233","fill-opacity":0.5,"fill-outline-color":"#ffffff","fill-outline-width":2}}]""");

        var symbol = drawing!.Renderer.Symbol!;
        Assert.Equal("simple", drawing.Renderer.Type);
        AssertSerialized(drawing, "renderer.symbol.type", "esriSFS");
        Assert.Equal("esriSFSSolid", symbol.Style);
        Assert.Equal([0x11, 0x22, 0x33, 128], symbol.Color);
        Assert.Equal("esriSLS", symbol.Outline!.Type);
        Assert.Equal(2, symbol.Outline.Width);
    }

    [Fact]
    public void A_line_fragment_becomes_a_solid_line_symbol()
    {
        var symbol = MapStyleProjection.Project("""[{"type":"line","paint":{"line-color":"#ff0000","line-width":3}}]""")!.Renderer.Symbol!;

        Assert.Equal("esriSLS", symbol.Type);
        Assert.Equal(3, symbol.Width);
        Assert.Equal([255, 0, 0, 255], symbol.Color);
    }

    [Fact]
    public void A_circle_fragment_becomes_a_circle_marker_with_diameter_size()
    {
        var symbol = MapStyleProjection.Project(
            """[{"type":"circle","paint":{"circle-color":"#00ff00","circle-radius":7,"circle-stroke-color":"#000000","circle-stroke-width":1}}]""")!.Renderer.Symbol!;

        Assert.Equal("esriSMS", symbol.Type);
        Assert.Equal("esriSMSCircle", symbol.Style);
        Assert.Equal(14, symbol.Size);
        Assert.Equal([0, 255, 0, 255], symbol.Color);
        Assert.Equal("esriSLS", symbol.Outline!.Type);
    }

    [Fact]
    public void Fill_precedes_line_and_circle_when_several_fragments_exist()
    {
        var symbol = MapStyleProjection.Project(
            """[{"type":"circle","paint":{"circle-color":"#000000"}},{"type":"fill","paint":{"fill-color":"#abcdef"}}]""")!.Renderer.Symbol!;

        Assert.Equal("esriSFS", symbol.Type);
        Assert.Equal([0xab, 0xcd, 0xef, 255], symbol.Color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void No_usable_style_yields_no_drawing_info(string? style) =>
        Assert.Null(MapStyleProjection.Project(style));

    [Fact]
    public void An_opacity_scales_the_symbol_alpha()
    {
        var symbol = MapStyleProjection.Project("""[{"type":"line","paint":{"line-color":"#123456","line-opacity":0.25}}]""")!.Renderer.Symbol!;

        Assert.Equal(64, symbol.Color[3]);
    }

    [Fact]
    public void Named_and_functional_colors_are_understood()
    {
        Assert.Equal([255, 0, 0, 255], Color("red"));
        Assert.Equal([1, 2, 3, 128], Color("rgba(1,2,3,0.5)"));
        Assert.Equal([0, 0, 0, 0], Color("transparent"));
    }

    [Fact]
    public void Equal_value_siblings_project_a_unique_value_renderer()
    {
        var drawing = MapStyleProjection.Project(
            """
            [
              {"type":"circle","filter":["==","country","DE"],"paint":{"circle-color":"#ff0000"}},
              {"type":"circle","filter":["==","country","FR"],"paint":{"circle-color":"#0000ff"}},
              {"type":"circle","paint":{"circle-color":"#888888"}}
            ]
            """);

        var renderer = drawing!.Renderer;
        Assert.Equal("uniqueValue", renderer.Type);
        Assert.Equal("country", renderer.Field1);
        var infos = renderer.UniqueValueInfos!;
        Assert.Equal(2, infos.Count);
        Assert.Equal(["DE", "FR"], infos.Select(info => info.Value));
        Assert.Equal([255, 0, 0, 255], infos[0].Symbol.Color);
        Assert.Equal([136, 136, 136, 255], renderer.DefaultSymbol!.Color);

        var json = Json(drawing);
        Assert.Equal("uniqueValue", json.GetProperty("renderer").GetProperty("type").GetString());
        Assert.Equal("<Other values>", json.GetProperty("renderer").GetProperty("defaultLabel").GetString());
        Assert.Equal("DE", json.GetProperty("renderer").GetProperty("uniqueValueInfos")[0].GetProperty("value").GetString());
    }

    [Fact]
    public void An_in_filter_expands_into_one_info_per_value()
    {
        var renderer = MapStyleProjection.Project(
            """
            [
              {"type":"line","filter":["in","kind","a","b"],"paint":{"line-color":"#010203"}},
              {"type":"line","filter":["==","kind","c"],"paint":{"line-color":"#040506"}}
            ]
            """)!.Renderer;

        Assert.Equal("uniqueValue", renderer.Type);
        var infos = renderer.UniqueValueInfos!;
        Assert.Equal(["a", "b", "c"], infos.Select(info => info.Value));
        Assert.Equal(3, infos.Count);
    }

    [Fact]
    public void Differing_unique_value_fields_do_not_project_a_rich_renderer()
    {
        var renderer = MapStyleProjection.Project(
            """
            [
              {"type":"circle","filter":["==","country","DE"],"paint":{"circle-color":"#ff0000"}},
              {"type":"circle","filter":["==","name","Paris"],"paint":{"circle-color":"#0000ff"}}
            ]
            """)!.Renderer;

        Assert.Equal("simple", renderer.Type);
    }

    [Fact]
    public void Interval_siblings_project_a_class_breaks_renderer()
    {
        var drawing = MapStyleProjection.Project(
            """
            [
              {"type":"fill","filter":["all",[">=","population",0],["<","population",1000]],"paint":{"fill-color":"#ffffcc"}},
              {"type":"fill","filter":["all",[">=","population",1000],["<","population",1000000]],"paint":{"fill-color":"#ff0000"}}
            ]
            """);

        var renderer = drawing!.Renderer;
        Assert.Equal("classBreaks", renderer.Type);
        Assert.Equal("population", renderer.Field);
        Assert.Equal(0, renderer.MinValue);
        var breaks = renderer.ClassBreakInfos!;
        Assert.Equal([1000, 1000000], breaks.Select(info => info.ClassMaxValue));
        Assert.Equal(["1000", "1000000"], breaks.Select(info => info.Label));

        var json = Json(drawing!);
        Assert.Equal("classBreaks", json.GetProperty("renderer").GetProperty("type").GetString());
        Assert.Equal(0, json.GetProperty("renderer").GetProperty("minValue").GetDouble());
        Assert.Equal(1000, json.GetProperty("renderer").GetProperty("classBreakInfos")[0].GetProperty("classMaxValue").GetDouble());
    }

    [Fact]
    public void A_single_filtered_fragment_stays_simple()
    {
        var renderer = MapStyleProjection.Project(
            """[{"type":"circle","filter":[">","population",1000],"paint":{"circle-color":"#ff0000"}}]""")!.Renderer;

        Assert.Equal("simple", renderer.Type);
    }

    [Fact]
    public void A_single_field_symbol_fragment_projects_a_label_class()
    {
        var labels = MapStyleProjection.Labels(
            """
            [
              {"type":"circle","paint":{"circle-color":"#ff0000"}},
              {"type":"symbol","layout":{"text-field":["get","name"],"text-size":11,"text-font":["Noto Sans","Arial"],"text-anchor":"top"},"paint":{"text-color":"#262626"}}
            ]
            """,
            Cities());

        var label = Assert.Single(labels!);
        Assert.Equal("[name]", label.LabelExpression);
        Assert.Equal("esriServerPointLabelPlacementAboveCenter", label.LabelPlacement);
        Assert.Equal(11, label.Symbol.Font.Size);
        Assert.Equal("Noto Sans", label.Symbol.Font.Family);
        Assert.Equal([38, 38, 38, 255], label.Symbol.Color);
        Assert.Equal("top", label.Symbol.VerticalAlignment);
        Assert.Equal("center", label.Symbol.HorizontalAlignment);
    }

    [Fact]
    public void A_brace_text_field_and_polygon_geometry_project_a_label_class()
    {
        var labels = MapStyleProjection.Labels(
            """[{"type":"symbol","layout":{"text-field":"{name}"},"paint":{"text-color":"#000000"}}]""",
            Cities(geometryType: "polygon"));

        var label = Assert.Single(labels!);
        Assert.Equal("esriServerPolygonPlacementAlwaysHorizontal", label.LabelPlacement);
        Assert.Equal("[name]", label.LabelExpression);
    }

    [Fact]
    public void Multi_field_and_picture_labels_are_not_projected()
    {
        Assert.Null(MapStyleProjection.Labels(
            """[{"type":"symbol","layout":{"text-field":["concat",["get","name"]," ",["get","country"]]}}]""",
            Cities()));
        Assert.Null(MapStyleProjection.Labels(
            """[{"type":"symbol","layout":{"icon-image":"pin"}}]""",
            Cities()));
    }

    [Fact]
    public void A_unique_value_renderer_projects_a_coded_value_domain()
    {
        var drawing = MapStyleProjection.Project(
            """
            [
              {"type":"circle","filter":["==","country","DE"],"paint":{"circle-color":"#ff0000"}},
              {"type":"circle","filter":["==","country","FR"],"paint":{"circle-color":"#0000ff"}}
            ]
            """,
            Cities());

        var domains = MapStyleProjection.Domains(drawing, Cities())!;

        var domain = domains["country"];
        Assert.Equal("codedValue", domain.Type);
        var coded = domain.CodedValues!;
        Assert.Equal(["DE", "FR"], coded.Select(value => value.Code));
        Assert.Equal(["DE", "FR"], coded.Select(value => value.Name));

        var json = Json(domain);
        Assert.Equal("DE", json.GetProperty("codedValues")[0].GetProperty("code").GetString());
    }

    [Fact]
    public void A_class_breaks_renderer_projects_a_range_domain()
    {
        var drawing = MapStyleProjection.Project(
            """
            [
              {"type":"fill","filter":["all",[">=","population",0],["<","population",1000]],"paint":{"fill-color":"#ffffcc"}},
              {"type":"fill","filter":["all",[">=","population",1000],["<","population",1000000]],"paint":{"fill-color":"#ff0000"}}
            ]
            """,
            Cities());

        var domain = MapStyleProjection.Domains(drawing, Cities())!["population"];
        Assert.Equal("range", domain.Type);
        Assert.Equal([0, 1000000], domain.Range);
    }

    [Fact]
    public void A_domain_for_a_field_outside_the_catalogue_is_not_projected()
    {
        var drawing = MapStyleProjection.Project(
            """[{"type":"circle","filter":["==","subtype","a"],"paint":{"circle-color":"#ff0000"}},{"type":"circle","filter":["==","subtype","b"],"paint":{"circle-color":"#0000ff"}}]""",
            Cities());

        Assert.Null(MapStyleProjection.Domains(drawing, Cities()));
    }

    [Fact]
    public void A_simple_renderer_has_no_domains()
    {
        var drawing = MapStyleProjection.Project("""[{"type":"circle","paint":{"circle-color":"#ff0000"}}]""", Cities());

        Assert.Null(MapStyleProjection.Domains(drawing, Cities()));
    }

    [Fact]
    public void A_drawing_info_serializes_labels_inside_it()
    {
        var drawing = MapStyleProjection.Project(
            """
            [
              {"type":"circle","paint":{"circle-color":"#ff0000"}},
              {"type":"symbol","layout":{"text-field":["get","name"]},"paint":{"text-color":"#000000"}}
            ]
            """,
            Cities());

        var labeling = Json(drawing!).GetProperty("labelingInfo");
        Assert.Equal("[name]", labeling[0].GetProperty("labelExpression").GetString());
        Assert.Equal("esriTS", labeling[0].GetProperty("symbol").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("top", "esriServerPointLabelPlacementAboveCenter")]
    [InlineData("bottom", "esriServerPointLabelPlacementBelowCenter")]
    [InlineData("left", "esriServerPointLabelPlacementCenterLeft")]
    [InlineData("right", "esriServerPointLabelPlacementCenterRight")]
    [InlineData("top-left", "esriServerPointLabelPlacementAboveLeft")]
    [InlineData("top-right", "esriServerPointLabelPlacementAboveRight")]
    [InlineData("bottom-left", "esriServerPointLabelPlacementBelowLeft")]
    [InlineData("bottom-right", "esriServerPointLabelPlacementBelowRight")]
    [InlineData("center", "esriServerPointLabelPlacementCenterCenter")]
    public void Every_anchor_projects_a_distinct_point_placement(string anchor, string expected)
    {
        var labels = MapStyleProjection.Labels(
            $$$"""[{"type":"symbol","layout":{"text-field":"{name}","text-anchor":"{{{anchor}}}"},"paint":{"text-color":"#000000"}}]""",
            Cities());

        Assert.Equal(expected, Assert.Single(labels!).LabelPlacement);
    }

    [Theory]
    [InlineData("line", "esriServerLinePlacementCenterAlong")]
    [InlineData("multiLineString", "esriServerLinePlacementCenterAlong")]
    [InlineData("polygon", "esriServerPolygonPlacementAlwaysHorizontal")]
    [InlineData("multiPolygon", "esriServerPolygonPlacementAlwaysHorizontal")]
    public void The_geometry_family_selects_the_placement_family(string geometryType, string expected)
    {
        var labels = MapStyleProjection.Labels(
            """[{"type":"symbol","layout":{"text-field":"{name}"},"paint":{"text-color":"#000000"}}]""",
            Cities(geometryType));

        Assert.Equal(expected, Assert.Single(labels!).LabelPlacement);
    }

    [Theory]
    [InlineData("""["==","country","DE"]""", "DE")]
    [InlineData("""["==","country",true]""", "true")]
    [InlineData("""["==","country",false]""", "false")]
    [InlineData("""["==","country",42]""", "42")]
    [InlineData("""["==","country",1.5]""", "1.5")]
    [InlineData("""["==","country",null]""", "")]
    [InlineData("""["==","country",[]]""", "")]
    public void Every_filter_value_kind_becomes_a_coded_value(string filter, string expected)
    {
        var renderer = MapStyleProjection.Project(
            $$$"""[{"type":"circle","filter":{{{filter}}},"paint":{"circle-color":"#ff0000"}}]""")!.Renderer;

        Assert.Equal("uniqueValue", renderer.Type);
        Assert.Equal(expected, Assert.Single(renderer.UniqueValueInfos!).Value);
    }

    [Theory]
    [InlineData("""["all",[">","population",0],["<","population",1000]]""")]
    [InlineData("""["all",[">=","population",0],["<=","population",1000]]""")]
    [InlineData("""["all",[">=","population",0],["<","name",1000]]""")]
    [InlineData("""["all",[">=","population",0]]""")]
    [InlineData("""["all",[">=","population","x"],["<","population",1000]]""")]
    [InlineData("""["all","x",["<","population",1000]]""")]
    public void An_unreadable_interval_falls_back_to_a_simple_renderer(string filter)
    {
        var style =
            $$$"""[{"type":"fill","filter":{{{filter}}},"paint":{"fill-color":"#ffffcc"}},{"type":"fill","filter":{{{filter}}},"paint":{"fill-color":"#ff0000"}}]""";

        Assert.Equal("simple", MapStyleProjection.Project(style)!.Renderer.Type);
    }

    [Theory]
    [InlineData("""{"text-field":"{name}"}""", "Arial")]
    [InlineData("""{"text-field":"{name}","text-font":"Courier New"}""", "Courier New")]
    [InlineData("""{"text-field":"{name}","text-font":["Noto Sans","Arial"]}""", "Noto Sans")]
    [InlineData("""{"text-field":"{name}","text-font":[]}""", "Arial")]
    [InlineData("""{"text-field":"{name}","text-font":[7]}""", "Arial")]
    public void Text_font_forms_project_a_family(string layout, string expected)
    {
        var labels = MapStyleProjection.Labels(
            $$$"""[{"type":"symbol","layout":{{{layout}}},"paint":{"text-color":"#000000"}}]""",
            Cities());

        Assert.Equal(expected, Assert.Single(labels!).Symbol.Font.Family);
    }

    [Fact]
    public void Domains_without_drawing_info_or_a_classifying_renderer_are_null()
    {
        Assert.Null(MapStyleProjection.Domains(null, Cities()));
        Assert.Null(MapStyleProjection.Domains(
            new EsriDrawingInfo(new EsriRenderer("simple", new EsriSymbol("esriSFS", "esriSFSSolid", [0, 0, 0, 255]))), Cities()));
        Assert.Null(MapStyleProjection.Domains(
            new EsriDrawingInfo(new EsriRenderer("uniqueValue", Field1: "country")), Cities()));
        Assert.Null(MapStyleProjection.Domains(
            new EsriDrawingInfo(new EsriRenderer("classBreaks", Field: "population", MinValue: 0)), Cities()));
    }

    [Theory]
    [InlineData("#abc", "170,187,204,255")]
    [InlineData("#abcd", "170,187,204,221")]
    [InlineData("#aabbcc", "170,187,204,255")]
    [InlineData("#aabbccdd", "170,187,204,221")]
    [InlineData("#ab", "0,0,0,0")]
    [InlineData("#aabbccddee", "0,0,0,0")]
    public void Hex_colours_parse_every_supported_length(string css, string expected)
    {
        Assert.Equal(expected.Split(',').Select(int.Parse), EsriColor.ToRgba(css, 1.0));
    }

    [Theory]
    [InlineData("rgb(1,2,3)", "1,2,3,255")]
    [InlineData("rgba(1,2,3,0.5)", "1,2,3,128")]
    [InlineData("rgb(1,2)", "0,0,0,0")]
    [InlineData("rgb 1,2,3", "0,0,0,0")]
    [InlineData("rgb(1,2,300)", "1,2,255,255")]
    [InlineData("rgb(-5,2,3)", "0,2,3,255")]
    [InlineData("rgb(a,b,c)", "0,0,0,0")]
    public void Functional_colours_parse_their_channels(string css, string expected)
    {
        Assert.Equal(expected.Split(',').Select(int.Parse), EsriColor.ToRgba(css, 1.0));
    }

    private static DatasetDescription Cities(string geometryType = "point") => new(
        "demo.cities",
        "public",
        "cities",
        "geometry",
        4326,
        geometryType,
        3,
        ["name"],
        new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("country", AttributeKind.String),
            new FieldDefinition("population", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]));

    private static int[] Color(string css) =>
        MapStyleProjection.Project("[{\"type\":\"line\",\"paint\":{\"line-color\":\"" + css + "\"}}]")!.Renderer.Symbol!.Color.ToArray();

    private static JsonElement Json(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, EsriJson.Options)).RootElement;

    private static void AssertSerialized(EsriDrawingInfo drawing, string path, string expected)
    {
        var element = Json(drawing);
        foreach (var segment in path.Split('.'))
        {
            element = element.GetProperty(segment);
        }

        Assert.Equal(expected, element.GetString());
    }
}
