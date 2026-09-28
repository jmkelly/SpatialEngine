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
/// The MapLibre expression dialect in paint and filter (T-u2x.19). The
/// reproduction these tests pin: before the evaluator existed every expression
/// form below was rejected with <c>invalid.arguments</c> at compile time, so a
/// style authored by any real MapLibre tool failed to render at all. The
/// conformance table is the contract; the render tests prove the resolved
/// values reach the rasterizer; the cache test proves the per-feature
/// evaluation is shared, not re-run per layer.
/// </summary>
public sealed class MapLibreExpressionTests
{
    private static readonly Feature Feature = TestFeatures.Point("Alpha", 1, 2, population: 40);

    public static TheoryData<string, string> Conformance => new()
    {
        // literals
        { """["literal", 7]""", "7" },
        { """42""", "42" },
        { "\"hello\"", "hello" },
        { """true""", "true" },
        { """["get", "name"]""", "Alpha" },
        { """["get", "population"]""", "40" },
        { """["get", "missing"]""", "null" },
        { """["get", "missing", "fallback"]""", "fallback" },
        { """["has", "population"]""", "true" },
        { """["has", "missing"]""", "false" },
        { """["id"]""", "Alpha" },
        { """["geometry-type"]""", "Point" },
        { """["zoom"]""", "7" },
        // comparison and logic
        { """["==", ["get", "name"], "Alpha"]""", "true" },
        { """["!=", ["get", "name"], "Alpha"]""", "false" },
        { """[">", ["get", "population"], 10]""", "true" },
        { """[">=", ["get", "population"], 40]""", "true" },
        { """["<", ["get", "population"], 10]""", "false" },
        { """["<=", ["get", "population"], 40]""", "true" },
        { """["all", [">", ["get", "population"], 1], ["==", ["get", "name"], "Alpha"]]""", "true" },
        { """["any", false, true]""", "true" },
        { """["!", ["has", "name"]]""", "false" },
        { """["in", "Al", ["get", "name"], "Beta"]""", "true" },
        { """["in", "zz", ["get", "name"], "Beta"]""", "false" },
        { """["concat", ["get", "name"], "-", "x"]""", "Alpha-x" },
        // selection
        { """["case", [">", ["get", "population"], 100], "big", "small"]""", "small" },
        { """["coalesce", ["get", "missing"], ["get", "name"]]""", "Alpha" },
        { """["match", ["get", "name"], "Alpha", 1, "Beta", 2, 0]""", "1" },
        { """["match", ["get", "name"], ["Beta", "Gamma"], 2, 0]""", "0" },
        { """["step", ["get", "population"], 1, 20, 2, 60, 3]""", "2" },
        { """["step", ["get", "population"], 1, 20, 2, 40, 3]""", "3" },
        { """["step", ["get", "population"], 1, 20, 2, 40, 3, 60, 5]""", "3" },
        // interpolation
        { """["interpolate", ["linear"], ["get", "population"], 0, 0, 100, 10]""", "4" },
        { """["interpolate", ["exponential", 2], ["get", "population"], 0, 0, 80, 80]""", "20" },
        { """["interpolate", ["linear"], ["get", "population"], 0, 0, 80, 80]""", "40" },
        { """["interpolate", ["linear"], ["get", "population"], 0, "#000000", 100, "#ffffff"]""", "#666666" },
        // to-color (SpatialEngine-ymh): a number ramped into a colour is
        // expressible only when the coercion is asked for by name.
        { """["to-color", 0]""", "#000000" },
        { """["to-color", 0.5]""", "#808080" },
        { """["to-color", 1]""", "#FFFFFF" },
        { """["to-color", 2]""", "#FFFFFF" },
        { """["to-color", -1]""", "#000000" },
        { """["to-color", "red"]""", "#FF0000" },
        { """["to-color", ["/", ["get", "population"], 100]]""", "#666666" },
        { """["interpolate", ["linear"], ["zoom"], 5, ["to-color", 0], 10, ["to-color", 1]]""", "#666666" },
        // cubic-bezier easing. The hand-computed matrix: with x1 + x2 = 1 the
        // curve passes X(0.5) = 0.5, and Y(0.5) = 0.375 * (y1 + y2) + 0.125.
        // So (0,1,1,1) yields 0.875 and (0,0,1,0) yields 0.125 where linear
        // would yield 0.5; the stops are 0..80 so the feature's 40 is the
        // midpoint of the ramp.
        { """["interpolate", ["cubic-bezier"], ["get", "population"], 0, 0, 80, 8]""", "4" },
        { """["interpolate", ["cubic-bezier", 0, 1, 1, 1], ["get", "population"], 0, 0, 80, 8]""", "7" },
        { """["interpolate", ["cubic-bezier", 0, 0, 1, 0], ["get", "population"], 0, 0, 80, 8]""", "1" },
        { """["interpolate", ["cubic-bezier", 0, 1, 1, 1], ["get", "population"], 0, "#000000", 80, "#ffffff"]""", "#DFDFDF" },
        // at-interpolate: the same ramp sampled at a stop instead of at the
        // input, clamped outside the stop range exactly as interpolate is.
        { """["at-interpolate", ["linear"], ["zoom"], 5, 0, 0, 10, 1]""", "0.5" },
        { """["at-interpolate", ["linear"], ["zoom"], -5, 0, 0, 10, 1]""", "0" },
        { """["at-interpolate", ["linear"], ["zoom"], 20, 0, 0, 10, 1]""", "1" },
        { """["at-interpolate", ["exponential", 2], ["get", "population"], 50, 0, 0, 100, 10]""", "2.5" },
        { """["at-interpolate", ["linear"], ["zoom"], 50, 0, "#000000", 100, "#ffffff"]""", "#808080" },
        { """["at-interpolate", ["cubic-bezier", 0, 1, 1, 1], ["zoom"], 5, 0, 0, 10, 8]""", "7" },
        // let / var
        { """["let", "n", ["get", "population"], ["*", ["var", "n"], 2]]""", "80" },
    };

    [Theory]
    [MemberData(nameof(Conformance))]
    public void Evaluate_MatchesTheMapLibreConformance(string expression, string expected)
    {
        Assert.Equal(expected, Evaluate(expression, Feature, 7));
    }

    [Fact]
    public void Evaluate_ReadsTheViewportZoomAndTheFeatureIdentity()
    {
        Assert.Equal(
            50d,
            double.Parse(Evaluate("""["interpolate", ["linear"], ["zoom"], 0, 0, 20, 100]""", Feature, 10), CultureInfo.InvariantCulture),
            6);
        Assert.Equal("Alpha", Evaluate("""["id"]""", Feature, 10));
    }

    [Fact]
    public void Evaluate_ReportsTheGeometryTypeOfTheFeature()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var polygon = new Feature(
            new FeatureId("area"),
            schema,
            [AttributeValue.FromGeometry(GeometryFactory.CreatePolygon(
            GeometryFactory.CreateLineString(
            [new Coordinate(0, 0), new Coordinate(1, 0), new Coordinate(1, 1), new Coordinate(0, 0)])))]);

        var value = new ExpressionScope(polygon, polygon[0].GeometryValue, 0)
            .Evaluate(ExpressionReader.Read(Json("""["geometry-type"]""")));
        Assert.Equal("Polygon", value.AsText());
    }

    [Theory]
    [InlineData("""["interpolate", ["linear"], ["get", "population"], 0, 1]""")]
    [InlineData("""["step", ["get", "population"], 1, 20]""")]
    [InlineData("""["case", true]""")]
    [InlineData("""["match", ["get", "name"], "Alpha", 1]""")]
    [InlineData("""["get"]""")]
    [InlineData("""["get", 5]""")]
    [InlineData("""["coalesce"]""")]
    [InlineData("""["let", "a", 1]""")]
    [InlineData("""["var", "missing"]""")]
    [InlineData("""["*"]""")]
    [InlineData("""["bogus", 1]""")]
    [InlineData("""{"op": "=="}""")]
    [InlineData("""["interpolate", ["linear"], ["get", "population"], 10, 1, 0, 2]""")]
    [InlineData("""["cubic-bezier", 0, 0, 1, 1]""")]
    public void Read_RejectsUnsupportedExpressionsNamingThePath(string expression)
    {
        var error = Assert.Throws<SpatialException>(() => ExpressionReader.Read(Json(expression)));
        Assert.Equal(SpatialException.InvalidArguments, error.Code);
    }

    [Theory]
    [InlineData("""["interpolate", ["cubic-bezier", 0, 0, 1, 1, 1], ["zoom"], 0, 0, 1, 1]""")]
    [InlineData("""["interpolate", ["cubic-bezier", 1.5, 0, 1, 1], ["zoom"], 0, 0, 1, 1]""")]
    [InlineData("""["at-interpolate", ["linear"], ["zoom"], 5]""")]
    [InlineData("""["at-interpolate", ["linear"], ["zoom"], "x", 0, 0, 10, 1]""")]
    [InlineData("""["at-interpolate", ["linear"], ["zoom"], 5, 0, 10, 0]""")]
    [InlineData("""["at-interpolate", ["linear"], ["zoom"]]""")]
    [InlineData("""["to-color"]""")]
    [InlineData("""["to-color", 0, 1]""")]
    [InlineData("""["to-color", "#ff0000"]""")]
    public void Read_RejectsAMalformedInterpolationOrToColorNamingThePath(string expression)
    {
        var error = Assert.Throws<SpatialException>(() => ExpressionReader.Read(Json(expression)));
        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains(
            expression.Contains("to-color", StringComparison.Ordinal) ? "to-color" : "interpolate",
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RejectsATextOperandToColorCannotRead()
    {
        // The number form and the colour-name form are both served; a string
        // that is neither is a typed failure naming the operator, not a
        // silently flattened value.
        var error = Assert.Throws<SpatialException>(() =>
            Evaluate("""["to-color", ["get", "name"]]""", Feature, 7));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains("to-color", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_AcceptsANumberRampOnAColourPropertyWhenToColorAsksForIt()
    {
        var style = new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [
                { "id": "land", "type": "fill", "source-layer": "demo.land",
                  "paint": { "fill-color": ["interpolate", ["linear"], ["zoom"], 5, ["to-color", 0], 10, ["to-color", 1]],
                             "fill-opacity": ["at-interpolate", ["cubic-bezier", 0, 1, 1, 1], ["zoom"], 5, 0, 0.1, 10, 1] } } ] }
            """);

        var fill = Assert.IsType<FillPaint>(Assert.Single(style.Layers).Paint);
        Assert.True(fill.IsDataDriven());
    }

    [Fact]
    public void Read_RejectsAFilterThatDoesNotYieldABoolean()
    {
        var error = Assert.Throws<SpatialException>(() =>
            FilterReader.Read(Json("""["get", "population"]""")));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains("filter", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Compile_AcceptsAnExpressionInPaint()
    {
        var style = new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-color": ["match", ["get", "name"], "Alpha", "#ff0000", "#00ff00"],
                             "circle-radius": ["interpolate", ["linear"], ["get", "population"], 0, 2, 100, 10] } } ] }
            """);

        // The constant stays the documented default — it is what the paint
        // falls back to when an expression has no value for a feature.
        var circle = Assert.IsType<CirclePaint>(Assert.Single(style.Layers).Paint);
        Assert.Equal(new StyleColor(0, 0, 0), circle.Fill);
        Assert.NotNull(circle.Expressions);
        Assert.True(circle.IsDataDriven());
    }

    [Fact]
    public void Compile_RejectsANumberForAColourProperty()
    {
        var error = Assert.Throws<SpatialException>(() => new StyleCompiler().Compile(
            """
            { "version": 8, "layers": [
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-color": ["interpolate", ["linear"], ["get", "population"], 0, 0.2, 100, 1] } } ] }
            """));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains("circle-color", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_RejectsAnAttributeOfTheWrongTypeForANumberProperty()
    {
        // The type of a `get` is only known per feature, so the mismatch is a
        // typed render-time failure naming the property — not a silent coercion.
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-radius": ["get", "name"] } } ] }
            """,
            [new MapLayerSource("demo.cities", new FakeStore(TestFeatures.Schema, Feature), new FakeCatalogue(4326))]);

        var error = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains("circle-radius", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_RejectsANegativeSizeResolvedFromAnExpression()
    {
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-radius": ["*", ["get", "population"], -1] } } ] }
            """,
            [new MapLayerSource("demo.cities", new FakeStore(TestFeatures.Schema, Feature), new FakeCatalogue(4326))]);

        var error = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains("circle-radius", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_CubicBezierIsNotTheLinearRamp()
    {
        // The same input and stops: linear lands on the midpoint, the
        // ease-out curve is already most of the way there, the ease-in curve
        // has barely left the start.
        Assert.Equal(5d, Number("""["interpolate", ["linear"], ["zoom"], 0, 0, 10, 10]"""));
        Assert.True(Number("""["interpolate", ["cubic-bezier", 0, 1, 1, 1], ["zoom"], 0, 0, 10, 10]""") > 8);
        Assert.True(Number("""["interpolate", ["cubic-bezier", 0, 0, 1, 0], ["zoom"], 0, 0, 10, 10]""") < 2);
    }

    [Theory]
    [InlineData("""["interpolate", ["linear"], ["zoom"], 0, 0, 10, 10]""", 5d)]
    [InlineData("""["interpolate", ["cubic-bezier", 0, 1, 1, 1], ["zoom"], 0, 0, 10, 10]""", 8.75d)]
    [InlineData("""["interpolate", ["cubic-bezier", 0, 0, 1, 0], ["zoom"], 0, 0, 10, 10]""", 1.25d)]
    [InlineData("""["at-interpolate", ["cubic-bezier", 0, 1, 1, 1], ["zoom"], 5, 0, 0, 10, 10]""", 8.75d)]
    public void Evaluate_SolvesTheBezierWithinATolerance(string expression, double expected) =>
        Assert.Equal(expected, Number(expression), 9);

    private static double Number(string expression) =>
        double.Parse(Evaluate(expression, Feature, 5), CultureInfo.InvariantCulture);

    [Fact]
    public async Task Render_RejectsATextOperandToColorCannotRead()
    {
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-color": ["to-color", ["get", "name"]] } } ] }
            """,
            [new MapLayerSource("demo.cities", new FakeStore(TestFeatures.Schema, Feature), new FakeCatalogue(4326))]);

        var error = await Assert.ThrowsAsync<SpatialException>(() => renderer.RenderAsync(request));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
        Assert.Contains("to-color", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_DrawsAToColorRampPerFeature()
    {
        var store = new FakeStore(
            TestFeatures.Schema,
            TestFeatures.Point("Alpha", -4, 0, 0),
            TestFeatures.Point("Beta", 4, 0, 100));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#000000" } },
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-color": ["interpolate", ["linear"], ["get", "population"], 0, ["to-color", 0], 100, ["to-color", 1]],
                             "circle-radius": 3 } } ] }
            """,
            [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

        var bitmap = SkiaSharp.SKBitmap.Decode((await renderer.RenderAsync(request)).Content);

        // The ramp is per feature: black at 0, white at 100.
        Assert.Equal(0, Red(bitmap, 19, 32));
        Assert.Equal(255, Red(bitmap, 45, 32));
    }

    [Fact]
    public async Task Render_DrawsDataDrivenPaintPerFeature()
    {
        var store = new FakeStore(
            TestFeatures.Schema,
            TestFeatures.Point("Alpha", -4, 0, 40),
            TestFeatures.Point("Beta", 4, 0, 40));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#000000" } },
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "paint": { "circle-color": ["match", ["get", "name"], "Alpha", "#ff0000", "#0000ff"],
                             "circle-radius": 3 } } ] }
            """,
            [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

        var image = await renderer.RenderAsync(request);

        var bitmap = SkiaSharp.SKBitmap.Decode(image.Content);
        Assert.Equal(64, bitmap.Width);
        Assert.Equal(255, Red(bitmap, 19, 32));
        Assert.Equal(255, Blue(bitmap, 45, 32));
    }

    [Fact]
    public async Task Render_AppliesAnExpressionFilter()
    {
        var store = new FakeStore(
            TestFeatures.Schema,
            TestFeatures.Point("Alpha", -4, 0, 40),
            TestFeatures.Point("Beta", 4, 0, 40));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            """
            { "version": 8, "layers": [
                { "id": "bg", "type": "background", "paint": { "background-color": "#000000" } },
                { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                  "filter": ["==", ["get", "name"], "Alpha"],
                  "paint": { "circle-color": "#ff0000", "circle-radius": 3 } } ] }
            """,
            [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

        var bitmap = SkiaSharp.SKBitmap.Decode((await renderer.RenderAsync(request)).Content);

        Assert.Equal(255, Red(bitmap, 19, 32));
        Assert.Equal(0, Red(bitmap, 45, 32));
    }

    [Fact]
    public async Task Render_EvaluatesEachExpressionOncePerFeatureAcrossLayers()
    {
        var one = await RenderWithLayersAsync(one: true);
        var two = await RenderWithLayersAsync(one: false);

        // Two features, so two scopes in both renders.
        Assert.Equal(2, one.Features);
        Assert.Equal(2, two.Features);
        // The second layer costs one memo probe per feature and no new
        // evaluation: the same expression text compiles to the same node, and
        // the node is already in that feature's memo.
        Assert.Equal(one.Evaluations, two.Evaluations);
        Assert.Equal(2, two.MemoHits);
        // Interpolate plus the attribute read, once per feature; the stop
        // literals are not evaluated at all.
        Assert.Equal(4, one.Evaluations);
    }

    private static async Task<RenderStatistics> RenderWithLayersAsync(bool one)
    {
        var store = new FakeStore(
            TestFeatures.Schema,
            TestFeatures.Point("Alpha", -4, 0, 40),
            TestFeatures.Point("Beta", 4, 0, 40));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var radius = """["interpolate", ["linear"], ["get", "population"], 0, 2, 100, 10]""";
        var layers = one
            ? $$"""
               { "id": "a", "type": "circle", "source-layer": "demo.cities",
                 "paint": { "circle-color": "#ff0000", "circle-radius": {{radius}} } }
               """
            : $$"""
               { "id": "a", "type": "circle", "source-layer": "demo.cities",
                 "paint": { "circle-color": "#ff0000", "circle-radius": {{radius}} } },
               { "id": "b", "type": "circle", "source-layer": "demo.cities",
                 "paint": { "circle-color": "#0000ff", "circle-radius": {{radius}} } }
               """;
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
            $$"""
            { "version": 8, "layers": [ {{layers}} ] }
            """,
            [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

        await renderer.RenderAsync(request);
        return Assert.IsType<RenderStatistics>(renderer.LastStatistics);
    }

    [Fact]
    public async Task Render_CancelsBeforeTheExpressionCacheIsBuilt()
    {
        var store = new FakeStore(
            TestFeatures.Schema, TestFeatures.Point("Alpha", 0, 0, 40));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderAsync(
            new MapRenderRequest(
                new RasterViewport(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326"),
                """
                { "version": 8, "layers": [
                    { "id": "cities", "type": "circle", "source-layer": "demo.cities",
                      "paint": { "circle-radius": ["get", "population"] } } ] }
                """,
                [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]),
            cancellation.Token));
    }

    private static string Evaluate(string expression, Spatial.Core.Features.IFeature feature, double zoom) =>
        new ExpressionScope(feature, Geometry(feature), zoom).Evaluate(ExpressionReader.Read(Json(expression))).ToString();

    private static Spatial.Core.Geometry.IGeometry? Geometry(Spatial.Core.Features.IFeature feature)
    {
        var index = feature.Schema.IndexOf("geometry");
        return index < 0 ? null : feature[index].GeometryValue;
    }

    private static int Red(SkiaSharp.SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y).Red;

    private static int Blue(SkiaSharp.SKBitmap bitmap, int x, int y) => bitmap.GetPixel(x, y).Blue;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
