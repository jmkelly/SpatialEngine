using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia;
using Spatial.Rendering.Skia.Pipeline;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// The data-driven paint of every layer kind (T-u2x.19): the resolution of a
/// recipe's expressions against one feature, the DPI scaling of a
/// data-driven size, and the statistics the per-feature cache reports. The
/// conformance table in <see cref="MapLibreExpressionTests"/> pins the
/// expression semantics; this pins that every layer kind actually resolves.
/// </summary>
public sealed class ExpressionPaintTests
{
    private static readonly Feature Feature = TestFeatures.Point("Alpha", 1, 2, population: 40);

    [Fact]
    public void Fill_ResolvesColourOpacityAndOutlinePerFeature()
    {
        var style = new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [ { "id": "z", "type": "fill", "source-layer": "demo.zones",
              "paint": {
                "fill-color": ["match", ["get", "name"], "Alpha", "#ff0000", "#00ff00"],
                "fill-opacity": ["/", 1, 2],
                "fill-outline-color": ["step", ["get", "population"], "#000000", 10, "#ffffff"],
                "fill-outline-width": ["+", 1, 1] } } ] }
            """);

        var fill = Resolve(Fill(style));

        Assert.Equal(new StyleColor(255, 0, 0, 128), fill.Fill);
        Assert.Equal(new StyleColor(255, 255, 255, 128), fill.Outline);
        Assert.Equal(2, fill.OutlineWidth);
        Assert.False(fill.IsDataDriven());
    }

    [Fact]
    public void Circle_AppliesAConstantOpacityOnce_WhenAnotherPropertyIsDataDriven()
    {
        // The opacity is a constant, so a data-driven recipe stores its constant
        // colour untinted and the resolution applies the alpha exactly once —
        // not twice, and not zero times.
        var style = new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [ { "id": "c", "type": "circle", "source-layer": "demo.cities",
              "paint": { "circle-color": "#ff0000", "circle-opacity": 0.5,
                         "circle-radius": ["get", "population"] } } ] }
            """);

        var circle = Assert.IsType<CirclePaint>(Assert.Single(style.Layers).Paint);
        var resolved = (CirclePaint)circle.Resolve(new ExpressionScope(Feature, Geometry(Feature), 0));

        Assert.Equal(new StyleColor(255, 0, 0, 128), resolved.Fill);
        Assert.Equal(40, resolved.Radius);
    }

    [Fact]
    public void Line_ResolvesColourAndWidthPerFeature()
    {
        var style = new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [ { "id": "r", "type": "line", "source-layer": "demo.roads",
              "paint": { "line-color": ["match", ["get", "name"], "Alpha", "#ff0000", "#00ff00"],
                         "line-width": ["+", 1, ["get", "population"]] } } ] }
            """);

        var line = (LinePaint)Line(style).Resolve(new ExpressionScope(Feature, Geometry(Feature), 0));

        Assert.Equal(new StyleColor(255, 0, 0), line.Stroke);
        Assert.Equal(41, line.Width);
    }

    [Fact]
    public void Symbol_ResolvesTextAndHaloPaintPerFeature()
    {
        var style = new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [ { "id": "l", "type": "symbol", "source-layer": "demo.cities",
              "layout": { "text-field": "{name}" },
              "paint": { "text-color": ["match", ["get", "name"], "Alpha", "#ff0000", "#00ff00"],
                         "text-opacity": 0.5,
                         "text-halo-color": ["literal", "#0000ff"],
                         "text-halo-width": ["*", 2, 3] } } ] }
            """);

        var symbol = Assert.IsType<SymbolPaint>(Assert.Single(style.Layers).Paint);
        var resolved = (SymbolPaint)symbol.Resolve(new ExpressionScope(Feature, Geometry(Feature), 0));

        Assert.Equal(new StyleColor(255, 0, 0, 128), resolved.Options.Color);
        Assert.Equal(new StyleColor(0, 0, 255, 128), resolved.Options.HaloColor);
        Assert.Equal(6, resolved.Options.HaloWidth);
    }

    [Fact]
    public async Task Render_DrawsADataDrivenCircleOverADataDrivenBackground()
    {
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("Alpha", 0, 0, 40));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 32, 32, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "bg", "type": "background",
                  "paint": { "background-color": ["interpolate", ["linear"], ["zoom"], 0, "#000000", 24, "#ffffff"] } },
                { "id": "z", "type": "circle", "source-layer": "demo.cities",
                  "filter": ["all", ["==", "name", "Alpha"], ["!", ["has", "absent"]]],
                  "paint": { "circle-color": ["match", ["get", "name"], "Alpha", "#ff0000", "#00ff00"],
                             "circle-radius": 8 } } ] }
            """,
            [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

        var bitmap = SkiaSharp.SKBitmap.Decode((await renderer.RenderAsync(request)).Content);

        // A legacy-shaped all/! filter over literals keeps the compiled filter
        // model and still matches; the data-driven colour draws over it.
        Assert.Equal(255, bitmap.GetPixel(16, 16).Red);
        Assert.Equal(0, bitmap.GetPixel(16, 16).Blue);
        // The background is a zoom ramp, so it is a grey the zoom decides, not
        // an authored constant.
        Assert.InRange(bitmap.GetPixel(1, 1).Red, 1, 30);
    }

    [Fact]
    public async Task Render_DrawsDataDrivenLineAndSymbolPaint()
    {
        var store = new FakeStore(
            TestFeatures.Schema,
            TestFeatures.Point("Alpha", -4, 0, 40),
            TestFeatures.Point("Beta", 4, 0, 40),
            Line("Alpha-way", -9, 9, 9),
            Line("Beta-way", -9, 9, 6));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#000000" } },
                { "id": "roads", "type": "line", "source-layer": "demo.cities",
                  "paint": { "line-color": ["match", ["get", "name"], "Alpha-way", "#ff0000", "#00ff00"],
                             "line-width": 6 } },
                { "id": "labels", "type": "symbol", "source-layer": "demo.cities",
                  "layout": { "text-field": "{name}", "text-size": 20, "text-allow-overlap": true },
                  "paint": { "text-color": ["match", ["get", "name"], "Alpha", "#ffff00", "#00ffff"] } } ] }
            """,
            [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

        var bitmap = SkiaSharp.SKBitmap.Decode((await renderer.RenderAsync(request)).Content);

        // Two lines at different y, each with the colour its own expression
        // resolved to, plus a label per point: the line layer and the symbol
        // layer both carry data-driven paint.
        Assert.True(HasPixel(bitmap, 255, 0, 0), "the Alpha line colour was not drawn.");
        Assert.True(HasPixel(bitmap, 0, 255, 0), "the Beta line colour was not drawn.");
        Assert.True(HasPixel(bitmap, 255, 255, 0), "the Alpha text colour was not drawn.");
        Assert.True(HasPixel(bitmap, 0, 255, 255), "the Beta text colour was not drawn.");
    }

    private static bool HasPixel(SkiaSharp.SKBitmap bitmap, int red, int green, int blue)
    {
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Red == red && pixel.Green == green && pixel.Blue == blue)
                {
                    return true;
                }
            }
        }

        return false;
    }

    [Fact]
    public async Task Render_ScalesADataDrivenSizeWithTheRequestDpi()
    {
        const string Style = """
            { "version": 8, "layers": [ { "id": "c", "type": "circle", "source-layer": "demo.cities",
              "paint": { "circle-color": "#ff0000", "circle-radius": ["get", "population"] } } ] }
            """;
        var store = new FakeStore(TestFeatures.Schema, TestFeatures.Point("Alpha", 0, 0, 40));
        var viewport = new RasterViewport(new Envelope(-10, -10, 10, 10), 256, 256, "EPSG:4326");

        var normal = await RenderAsync(store, viewport, Style, 96);
        var doubled = await RenderAsync(store, viewport, Style, 192);

        // The data-driven radius scales with the request DPI exactly as a
        // constant one does, so a 192-dpi print is the same physical size.
        Assert.True(doubled > normal * 1.7, $"expected the data-driven radius to scale, saw {normal} then {doubled}.");
    }

    [Fact]
    public void Filter_MatchesAFeatureThroughTheFeatureOnlyFace()
    {
        var filter = FilterReader.Read(Json("""["!=", ["get", "name"], "Beta"]"""));

        Assert.True(filter.Matches(Feature));
        Assert.False(filter.Matches(TestFeatures.Point("Beta", 0, 0)));
    }

    [Fact]
    public void Filter_ReadsALegacyOperatorFilterThroughTheScopeFace()
    {
        var filter = FilterReader.Read(Json("""["none", ["==", "name", "Paris"], ["==", "name", "Rome"]]"""));

        Assert.True(filter.Matches(new ExpressionScope(Feature, Geometry(Feature), 0)));
        Assert.False(filter.Matches(new ExpressionScope(TestFeatures.Point("Paris", 0, 0), null, 0)));
    }

    [Fact]
    public void Evaluate_ReadsTheRemainingExpressionForms()
    {
        Assert.Equal("-40", Evaluate("""["-", ["get", "population"]]"""));
        Assert.Equal("20", Evaluate("""["/", 80, 4]"""));
        Assert.Equal("5", Evaluate("""["%", 40, 7]"""));
        Assert.Equal("40x", Evaluate("""["+", ["get", "population"], "x"]"""));
        Assert.Equal("4", Evaluate("""["interpolate", ["exponential"], ["get", "population"], 0, 0, 4, 4]"""));
        Assert.Equal(
            "80",
            Evaluate("""["let", "a", ["get", "population"], ["let", "b", ["var", "a"], ["*", ["var", "b"], 2]]]"""));
    }

    [Theory]
    [InlineData("""["interpolate", ["exponential", "two"], ["zoom"], 0, 0, 4, 4]""")]
    [InlineData("""["interpolate", ["bezier"], ["zoom"], 0, 0, 4, 4]""")]
    [InlineData("""["interpolate", "linear", ["zoom"], 0, 0, 4, 4]""")]
    [InlineData("""["case", true, 1]""")]
    [InlineData("""["*", 2]""")]
    [InlineData("""["/", 2]""")]
    public void Read_RejectsTheRemainingMalformedExpressions(string expression) =>
        Assert.Throws<SpatialException>(() => ExpressionReader.Read(Json(expression)));

    [Fact]
    public void Compile_AcceptsACaseWhoseOutputsDisagree()
    {
        // Mixed output types unify to "any", which fits any property; the
        // property check catches the value at render time if it is wrong.
        var style = new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [ { "id": "c", "type": "circle", "source-layer": "demo.cities",
              "paint": { "circle-radius": ["case", ["has", "name"], ["get", "name"], 3] } } ] }
            """);

        Assert.True(Assert.IsType<CirclePaint>(Assert.Single(style.Layers).Paint).IsDataDriven());
    }

    [Fact]
    public void Cache_ReportsItsCounters()
    {
        var statistics = new RenderStatistics();
        var cache = new FeatureScopeCache(4, statistics);
        var scope = cache.For(Feature, Geometry(Feature));
        var expression = ExpressionReader.Read(Json("""["+", 1, 2]"""));
        scope.Evaluate(expression);
        scope.Evaluate(expression);

        Assert.Equal(1, cache.Count);
        cache.Report();

        Assert.Equal(1, statistics.Features);
        Assert.Equal(1, statistics.Evaluations);
        Assert.Equal(1, statistics.MemoHits);
        Assert.Contains("memo hits", statistics.ToString(), StringComparison.Ordinal);
    }

    private static async Task<int> RenderAsync(
        FakeStore store, RasterViewport viewport, string style, double dpi)
    {
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var image = await renderer.RenderAsync(
            new MapRenderRequest(
                viewport,
                style,
                [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))],
                Dpi: dpi));
        return image.Content.Length;
    }

    /// <summary>A named two-point horizontal line at <paramref name="y"/>.</summary>
    private static Feature Line(string name, double fromX, double toX, double y) => new(
        new FeatureId(name),
        TestFeatures.Schema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromInt64(40),
            AttributeValue.FromGeometry(GeometryFactory.CreateLineString(
            [new Coordinate(fromX, y), new Coordinate(toX, y)], CoordinateReference.Epsg(4326))),
        ]);

    private static FillPaint Fill(CompiledStyle style) => Assert.IsType<FillPaint>(Assert.Single(style.Layers).Paint);

    private static LinePaint Line(CompiledStyle style) => Assert.IsType<LinePaint>(Assert.Single(style.Layers).Paint);

    private static FillPaint Resolve(PaintRecipe paint) =>
        (FillPaint)paint.Resolve(new ExpressionScope(Feature, Geometry(Feature), 0));

    private static IGeometry? Geometry(IFeature feature)
    {
        var index = feature.Schema.IndexOf("geometry");
        return index < 0 ? null : feature[index].GeometryValue;
    }

    private static string Evaluate(string expression) =>
        new ExpressionScope(Feature, Geometry(Feature), 7).Evaluate(ExpressionReader.Read(Json(expression))).ToString();

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
