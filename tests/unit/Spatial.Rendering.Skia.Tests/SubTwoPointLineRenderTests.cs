using SkiaSharp;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Pipeline;
using Spatial.Rendering.Skia.Styling;
using Spatial.Stores.Memory;
using Spatial.Transformations.ProjNet;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// Pins how the render path treats a LineString with fewer than two
/// coordinates (SpatialEngine-a74.2, ADR-0144).
///
/// <para>
/// The core model admits two distinct states a two-point-only view does not:
/// zero coordinates is the <em>empty</em> LineString, and one coordinate is a
/// <em>degenerate</em> LineString — neither empty nor a segment, but a value
/// the canonical codec, the WKB/EWKB readers and the GeoJSON ingest all carry
/// without complaint, so a client can put one in the engine. Neither is
/// rejected at construction, because core is structural and validation is a
/// plugin verb; what each consumer does with one is therefore a decision that
/// has to be written down and pinned rather than left to fall out of a loop.
/// </para>
///
/// <para>
/// The decision, in short: a sub-2-point LineString is a <em>position</em>, not
/// a segment. A line layer strokes nothing for it (SpatialEngine-a74 pinned the
/// stroke half; these tests pin the rest of the path), a symbol layer still
/// labels the one position it carries, and placement never hands it to the
/// planar clip — the clip bounds extent, and a single position has none.
/// </para>
/// </summary>
public sealed class SubTwoPointLineRenderTests
{
    private const string LineStyle = """
        { "version": 8, "layers": [
            { "id": "rivers", "type": "line", "source-layer": "demo.cities",
              "paint": { "line-color": "#00ff00", "line-width": 6 } } ] }
        """;

    private const string LabelStyle = """
        { "version": 8, "layers": [
            { "id": "labels", "type": "symbol", "source-layer": "demo.cities",
              "layout": { "text-field": "{name}", "text-size": 16, "text-anchor": "center" },
              "paint": { "text-color": "#00ff00" } } ] }
        """;

    private static readonly RasterViewport Viewport = new(new Envelope(-10, -10, 10, 10), 64, 64, "EPSG:4326");

    private static readonly RasterViewport Mercator =
        new(new Envelope(-1_113_194, -1_113_194, 1_113_194, 1_113_194), 64, 64, "EPSG:3857");

    // --- the states themselves ------------------------------------------------

    [Fact]
    public void AOnePointLineStringIsNeitherEmptyNorASegment()
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(5, 5)]);

        Assert.False(line.IsEmpty);
        Assert.Equal(1, line.CoordinateCount);
        Assert.Equal(new Envelope(5, 5, 5, 5), line.Envelope);
    }

    [Fact]
    public void AZeroPointLineStringIsEmpty()
    {
        var line = GeometryFactory.CreateEmptyLineString();

        Assert.True(line.IsEmpty);
        Assert.Equal(0, line.CoordinateCount);
        Assert.Null(line.Envelope);
    }

    // --- placement: the clip must not be handed a value it cannot represent --

    /// <summary>
    /// The repro. The source-CRS bounds of a dataset are its own extent, so a
    /// feature at the extent's edge is not contained by them and the placement
    /// stage clips before transforming. The planar clip cannot build a
    /// one-point LineString, so the whole render request failed with an opaque
    /// <c>invalid.arguments</c> SpatialException for a geometry the model
    /// admits and that a symbol layer draws a label for.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void Place_LeavesASubTwoPointLineStringAlone(int coordinateCount)
    {
        var line = coordinateCount == 1
            ? GeometryFactory.CreateLineString([new Coordinate(5, 5)])
            : GeometryFactory.CreateEmptyLineString();
        var features = new LayerFeatures(4326, "geometry", [], new Envelope(-1, -1, 1, 1));

        var result = GeometryPipeline.Place(
            line, features, Mercator, new ProjNetTransforms(), new NtsGeometryOperations(), CancellationToken.None);

        // Placement must not fail, and the shape it hands on must still be a
        // LineString: a degenerate line placed is still a line, not a point.
        var placed = Assert.IsType<LineString>(result);
        Assert.Equal(line.CoordinateCount, placed.CoordinateCount);
    }

    [Fact]
    public void ClipToBounds_LeavesASubTwoPointLineStringAlone()
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(5, 5)]);

        var result = GeometryPipeline.ClipToBounds(
            line, new Envelope(-1, -1, 1, 1), new NtsGeometryOperations(), CancellationToken.None);

        Assert.Same(line, result);
    }

    /// <summary>
    /// A degenerate member reaches the clip inside a compound too, and the
    /// adapter converts a whole multi-line at once, so the guard has to see
    /// members rather than only the top-level type.
    /// </summary>
    [Fact]
    public void ClipToBounds_LeavesACompoundHoldingOneAlone()
    {
        var multi = GeometryFactory.CreateMultiLineString(
            [GeometryFactory.CreateLineString([new Coordinate(5, 5)]),
             GeometryFactory.CreateLineString([new Coordinate(5, 0), new Coordinate(5, 8)])]);

        var result = GeometryPipeline.ClipToBounds(
            multi, new Envelope(-1, -1, 1, 1), new NtsGeometryOperations(), CancellationToken.None);

        Assert.Same(multi, result);
    }

    /// <summary>The guard is narrow: an ordinary two-point line is still clipped.</summary>
    [Fact]
    public void ClipToBounds_StillClipsATwoPointLine()
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(5, 0), new Coordinate(5, 8)]);

        var result = GeometryPipeline.ClipToBounds(
            line, new Envelope(-1, -1, 1, 1), new NtsGeometryOperations(), CancellationToken.None);

        Assert.NotSame(line, result);
    }

    // --- the cull: a position in view is a position in view --------------------

    [Theory]
    [InlineData(5, 5)]
    [InlineData(-10, 0)]
    [InlineData(10, 10)]
    public void SimplifyAndCull_KeepsAOnePointLineStringInView(double x, double y)
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(x, y)]);

        var result = GeometryPipeline.SimplifyAndCull(
            line, 0.5, Viewport.Bounds, new NtsGeometryOperations(), CancellationToken.None);

        Assert.Same(line, result);
    }

    // --- the stroke: nothing is a segment, so nothing is stroked ---------------

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task RenderAsync_DrawsNothingForASubTwoPointLineString(int coordinateCount)
    {
        var image = await RenderAsync(LineStyle, SubTwoPoint(coordinateCount), Viewport);

        using var bitmap = SKBitmap.Decode(image);
        Assert.Equal(0, CountGreen(bitmap!));
    }

    /// <summary>
    /// The same thing on the production stack through a MemoryStore and a
    /// reprojected viewport, so the clip stage the repro lives in actually
    /// runs rather than short-circuiting on a shared CRS.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task RenderAsync_OnTheProductionStack_DrawsNothingForASubTwoPointLineString(int coordinateCount)
    {
        var store = new MemoryStore();
        var batch = new FeatureBatch(TestFeatures.Schema, [SubTwoPoint(coordinateCount)]);
        await store.CreateAsync("demo.cities", batch, 4326);
        await store.WriteAsync("demo.cities", batch);
        var renderer = new MapRenderer(new ProjNetTransforms(), new NtsGeometryOperations());

        var image = await renderer.RenderAsync(new MapRenderRequest(
            Mercator, LineStyle, [new MapLayerSource("demo.cities", store, store)]));

        using var bitmap = SKBitmap.Decode(image.Content);
        Assert.Equal(0, CountGreen(bitmap!));
    }

    // --- the label: a degenerate line is still a position ----------------------

    [Fact]
    public void AOnePointLineStringOffersItsPositionForAPointLabel()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateLineString([new Coordinate(5, 5)]),
            new SymbolOptions { Placement = SymbolPlacement.Point },
            new ViewportProjection(Viewport));

        Assert.Equal((48f, 16f), (candidates[0].X, candidates[0].Y));
    }

    [Fact]
    public void AOnePointLineStringHasNothingToRunALabelAlong()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateLineString([new Coordinate(5, 5)]),
            new SymbolOptions { Placement = SymbolPlacement.Line, Spacing = 20 },
            new ViewportProjection(Viewport));

        Assert.Empty(candidates);
    }

    [Fact]
    public void AnEmptyLineStringHasNothingToLabel()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateEmptyLineString(),
            new SymbolOptions { Placement = SymbolPlacement.Point },
            new ViewportProjection(Viewport));

        Assert.Empty(candidates);
    }

    /// <summary>
    /// The end of the decision: the same one-point LineString that strokes
    /// nothing on a line layer is a legitimate one-point label on a symbol
    /// layer, and it is inked rather than silently dropped.
    /// </summary>
    [Fact]
    public async Task RenderAsync_LabelsAOnePointLineString()
    {
        var image = await RenderAsync(LabelStyle, SubTwoPoint(1), Viewport);

        using var bitmap = SKBitmap.Decode(image);
        Assert.True(CountGreen(bitmap!) > 0, "a one-point LineString on a symbol layer drew no label");
    }

    [Fact]
    public async Task RenderAsync_LabelsNothingForAnEmptyLineString()
    {
        var image = await RenderAsync(LabelStyle, SubTwoPoint(0), Viewport);

        using var bitmap = SKBitmap.Decode(image);
        Assert.Equal(0, CountGreen(bitmap!));
    }

    // --- helpers ---------------------------------------------------------------

    private static Feature SubTwoPoint(int coordinateCount)
    {
        var geometry = coordinateCount == 1
            ? GeometryFactory.CreateLineString([new Coordinate(0.1, 0.1)])
            : GeometryFactory.CreateEmptyLineString();
        return new Feature(
            new FeatureId("place"),
            TestFeatures.Schema,
            [
                AttributeValue.FromString("place"),
                AttributeValue.FromInt64(1),
                AttributeValue.FromGeometry(geometry),
            ]);
    }

    private static async Task<byte[]> RenderAsync(string style, Feature feature, RasterViewport viewport)
    {
        var store = new FakeStore(TestFeatures.Schema, feature);
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        return (await renderer.RenderAsync(new MapRenderRequest(
            viewport, style, [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]))).Content;
    }

    private static long CountGreen(SKBitmap bitmap)
    {
        long count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.Alpha > 0 && pixel.Green > 128 && pixel.Red < 128)
                {
                    count++;
                }
            }
        }

        return count;
    }
}
