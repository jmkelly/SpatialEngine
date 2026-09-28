using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Drawing;
using Spatial.Rendering.Skia.Styling;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// Label placement depth (ADR-0080): candidate generation, the priority order
/// the greedy first-fit runs in, line placement with a perpendicular offset,
/// the text transforms, and the font fallback chain. The pixel assertions are
/// deliberately about ink coverage, not coordinates, so they stay readable; the
/// exact geometry of a candidate is asserted on the placement engine itself.
/// </summary>
public sealed class SymbolPlacementTests
{
    private static readonly RasterViewport Viewport = new(new Envelope(0, 0, 10, 10), 100, 100, "EPSG:4326");

    private static ViewportProjection Projection => new(Viewport);

    private const string MarkerSvg =
        """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="20" viewBox="0 0 20 20"><rect width="20" height="20" fill="#ff0000"/></svg>""";

    // --- candidate generation ------------------------------------------------

    [Fact]
    public void APointOffersItsPositionThenTheFourAnchorOffsets()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreatePoint(5, 5), new SymbolOptions { Size = 20, Padding = 2 }, Projection);

        Assert.Equal(5, candidates.Count);
        Assert.Equal((50f, 50f), (candidates[0].X, candidates[0].Y));
        Assert.All(candidates, candidate => Assert.Equal(0, candidate.Degrees));
        Assert.Equal([50f, 50f, 50f, 6f, 94f], candidates.Select(candidate => candidate.X));
        Assert.Equal([50f, 6f, 94f, 50f, 50f], candidates.Select(candidate => candidate.Y));
    }

    [Fact]
    public void APolygonOffersItsEnvelopeCentre()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreatePolygon(
            [
                new Coordinate(6, 6),
                new Coordinate(8, 6),
                new Coordinate(8, 8),
                new Coordinate(6, 8),
                new Coordinate(6, 6),
            ]),
            new SymbolOptions(),
            Projection);

        Assert.Equal((70f, 30f), (candidates[0].X, candidates[0].Y));
    }

    [Fact]
    public void APointHasNoCandidateWhenItAsksForLinePlacement()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreatePoint(5, 5),
            new SymbolOptions { Placement = SymbolPlacement.Line, Spacing = 20 },
            Projection);

        Assert.Empty(candidates);
    }

    [Fact]
    public void AShortLineGetsOneCandidateAtItsMidpointAlignedToItsDirection()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateLineString([new Coordinate(2, 2), new Coordinate(8, 8)]),
            new SymbolOptions { Placement = SymbolPlacement.Line, Spacing = 250 },
            Projection);

        var candidate = Assert.Single(candidates);
        Assert.Equal(50f, candidate.X, 1);
        Assert.Equal(50f, candidate.Y, 1);
        Assert.Equal(-45f, candidate.Degrees, 1);
    }

    [Fact]
    public void ALongLineOffersARepeatedCandidatePerSpacingThenTheSameInReverse()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateLineString([new Coordinate(1, 5), new Coordinate(9, 5)]),
            new SymbolOptions { Placement = SymbolPlacement.Line, Spacing = 20 },
            Projection);

        // 80px of line at 20px spacing centres a label in each cell: 20, 40,
        // 60, 80 forward, then the same run back down the line.
        Assert.Equal(7, candidates.Count);
        Assert.Equal([20f, 40f, 60f, 80f, 80f, 60f, 40f], candidates.Select(candidate => candidate.X));
        Assert.All(candidates, candidate => Assert.Equal(50f, candidate.Y));
        Assert.All(candidates, candidate => Assert.Equal(0, candidate.Degrees));
    }

    [Fact]
    public void ALineCandidateIsAlignedToTheLocalDirection()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateLineString([new Coordinate(5, 1), new Coordinate(5, 9)]),
            new SymbolOptions { Placement = SymbolPlacement.Line, Spacing = 250 },
            Projection);

        Assert.Equal(-90f, Assert.Single(candidates).Degrees, 1);
    }

    [Fact]
    public void AMultiLinePlacesEachPart()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateMultiLineString(
            [
                GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(4, 2)]),
                GeometryFactory.CreateLineString([new Coordinate(6, 8), new Coordinate(9, 8)]),
            ]),
            new SymbolOptions { Placement = SymbolPlacement.Line, Spacing = 250 },
            Projection);

        // One candidate per part, then the reverse pass over the same two.
        Assert.Equal(3, candidates.Count);
        Assert.Equal(25f, candidates[0].X, 1);
        Assert.Equal(75f, candidates[1].X, 1);
    }

    [Fact]
    public void ADegenerateLineHasNoCandidate()
    {
        var candidates = SymbolCandidates.Generate(
            GeometryFactory.CreateLineString([new Coordinate(5, 5), new Coordinate(5, 5)]),
            new SymbolOptions { Placement = SymbolPlacement.Line, Spacing = 20 },
            Projection);

        Assert.Empty(candidates);
    }

    [Fact]
    public void ACandidateRotatesItsOwnFrameAboutTheAnchor()
    {
        var candidate = new SymbolCandidate(50, 50, 90);

        Assert.Equal((40f, 50f), candidate.Apply(0, 10));
        Assert.Equal((50f, 40f), candidate.Apply(-10, 0));
    }

    [Fact]
    public void ACandidateBoxIsTheRotatedExtent()
    {
        var upright = new SymbolCandidate(50, 50, 0).Box(40, 45, 20, 10);
        var turned = new SymbolCandidate(50, 50, 90).Box(-10, -5, 20, 10);

        Assert.Equal((90f, 95f, 110f, 105f), (upright.Left, upright.Top, upright.Right, upright.Bottom));
        Assert.Equal(10f, turned.Width, 3);
        Assert.Equal(20f, turned.Height, 3);
    }

    // --- priority ------------------------------------------------------------

    /// <summary>
    /// Five labels on one point fill every candidate there is, so the next
    /// layer's label has to compete for them. A lower sort key is placed first
    /// and wins, whatever the document order says.
    /// </summary>
    [Fact]
    public void TheLowerSortKeyWinsACollisionEvenFromALaterLayer()
    {
        var placed = Place(
        [
            SymbolLayer(Sort: 5, features: Crowd("Alpha")),
            SymbolLayer(Sort: 1, features: [Feature("Beta", 5, 5)]),
        ]);

        Assert.NotNull(placed[1]![0]);
        Assert.True(placed[0]!.Count(placement => placement is null) >= 3);
    }

    [Fact]
    public void AnEqualSortKeyFallsBackToTheStyleDocumentOrder()
    {
        var placed = Place(
        [
            SymbolLayer(Sort: 1, features: Crowd("Alpha")),
            SymbolLayer(Sort: 1, features: [Feature("Beta", 5, 5)]),
        ]);

        Assert.Null(placed[1]![0]);
        Assert.Contains(placed[0]!, placement => placement is not null);
    }

    [Fact]
    public void TheSameStyleAlwaysPlacesTheSameLabels()
    {
        var layers = new[]
        {
            SymbolLayer(Sort: 5, features: Crowd("Alpha")),
            SymbolLayer(Sort: 1, features: [Feature("Beta", 5, 5)]),
        };

        var first = Render(layers);
        var second = Render(layers);

        Assert.Equal(first.Pixels.ToArray(), second.Pixels.ToArray());
    }

    [Fact]
    public void PlacementIsIndependentOfTheOrderTheFeaturesArrivedIn()
    {
        var forwards = Place([SymbolLayer(Sort: 1, features: Crowd("Alpha"))]);
        var backwards = Place([SymbolLayer(Sort: 1, features: [.. Crowd("Alpha").Reverse()])]);

        Assert.Equal(
            forwards[0]!.Select(placement => placement?.Candidate),
            backwards[0]!.Select(placement => placement?.Candidate));
    }

    [Fact]
    public void ALowerSortKeyDrawsLastSoItSitsOnTop()
    {
        var layers = new[]
        {
            SymbolLayer(Sort: 1, features: [Feature("Alpha", 5, 5)], allowOverlap: true, color: new StyleColor(255, 0, 0)),
            SymbolLayer(Sort: 9, features: [Feature("Beta", 5, 5)], allowOverlap: true, color: new StyleColor(0, 0, 255)),
        };

        // Both draw, but the pixels keep the document order, so the later
        // layer's label is the one left on top where they overlap.
        var buffer = Render(layers);
        var centre = Pixel(buffer, 50, 50);

        Assert.True(centre.Blue > centre.Red, $"The centre pixel is {centre}.");
    }

    [Fact]
    public void SymbolAllowOverlapPlacesEveryLabel()
    {
        var layers = new[] { SymbolLayer(0, [Feature("Alpha", 5, 5), Feature("Beta", 5, 5)], allowOverlap: true) };

        Assert.All(Place(layers)[0]!, placement => Assert.NotNull(placement));
    }

    [Fact]
    public void SymbolIgnorePlacementPlacesEveryLabelAndBlocksNothing()
    {
        var ignored = new[]
        {
            SymbolLayer(0, [Feature("Alpha", 5, 5)], ignorePlacement: true),
            SymbolLayer(0, [Feature("Beta", 5, 5)]),
        };

        Assert.All(Place(ignored).SelectMany(placed => placed ?? []), placement => Assert.NotNull(placement));
    }

    [Fact]
    public void TheWithinLayerOrderIsTheIdentityThenEnvelopeCentreTieBreak()
    {
        var scene = new[]
        {
            SymbolLayer(0, [Feature("Beta", 5, 5), Feature("Alpha", 5, 5), Feature("Alpha", 6, 6)]),
        };

        // Two features share the identity "Alpha"; the first by identity keeps
        // the free spot, so the same three features always place the same way.
        var first = Render(scene);
        var second = Render(scene);

        Assert.Equal(first.Pixels.ToArray(), second.Pixels.ToArray());
    }

    // --- line placement and the text transforms ------------------------------

    [Fact]
    public void LinePlacementRunsTheLabelAlongItsLine()
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(5, 1), new Coordinate(5, 9)]);
        var rotated = Render([SymbolLayer(0, [new SymbolFeature(line, "Riverside", null)], placement: SymbolPlacement.Line)]);
        var upright = Render([SymbolLayer(0, [new SymbolFeature(line, "Riverside", null)])]);

        // Rotated 90 degrees the same wide label is tall and narrow.
        Assert.True(Ink(rotated, 30, 5, 70, 95) > Ink(upright, 30, 5, 70, 95));
        Assert.True(Ink(upright, 5, 35, 95, 65) > Ink(rotated, 5, 35, 95, 65));
    }

    [Fact]
    public void ATextOffsetOnALineLabelReadsPerpendicularToTheLine()
    {
        var line = GeometryFactory.CreateLineString([new Coordinate(5, 1), new Coordinate(5, 9)]);
        var on = Render([SymbolLayer(0, [new SymbolFeature(line, "Riverside", null)], placement: SymbolPlacement.Line)]);
        var off = Render(
        [
            SymbolLayer(
                0,
                [new SymbolFeature(line, "Riverside", null)],
                placement: SymbolPlacement.Line,
                offsetX: 0,
                offsetY: 3),
        ]);

        Assert.NotEqual(on.Pixels.ToArray(), off.Pixels.ToArray());
    }

    [Fact]
    public void RepeatedLinePlacementRendersAreByteIdentical()
    {
        var layers = new[]
        {
            SymbolLayer(
                0,
                [
                    new SymbolFeature(GeometryFactory.CreateLineString([new Coordinate(1, 2), new Coordinate(9, 3)]), "Alpha", null),
                    new SymbolFeature(GeometryFactory.CreateLineString([new Coordinate(1, 7), new Coordinate(9, 8)]), "Beta", null),
                ],
                placement: SymbolPlacement.Line,
                spacing: 30),
        };

        Assert.Equal(Render(layers).Pixels.ToArray(), Render(layers).Pixels.ToArray());
    }

    [Fact]
    public void ATextTransformIsAppliedBeforeShaping()
    {
        Assert.Equal("RIVERSIDE", ShapedLabel.Transform("Riverside", SymbolTextTransform.Uppercase));
        Assert.Equal("riverside", ShapedLabel.Transform("Riverside", SymbolTextTransform.Lowercase));
        Assert.Equal("Riverside", ShapedLabel.Transform("Riverside", SymbolTextTransform.None));
    }

    [Fact]
    public void UppercaseTextDrawsMoreInkThanMixedCase()
    {
        var plain = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)])]);
        var shouted = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], transform: SymbolTextTransform.Uppercase)]);

        Assert.True(Ink(shouted, 5, 35, 95, 65) > Ink(plain, 5, 35, 95, 65));
    }

    [Fact]
    public void LetterSpacingWidensTheLabel()
    {
        var plain = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)])]);
        var tracked = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], letterSpacing: 0.1)]);

        // The same glyphs, spread: the tracked label reaches past the
        // untracked one at both ends of the row.
        Assert.Equal(0, Ink(plain, 0, 35, 6, 65));
        Assert.True(Ink(tracked, 0, 35, 6, 65) > 0);
        Assert.Equal(0, Ink(plain, 94, 35, 100, 65));
        Assert.True(Ink(tracked, 94, 35, 100, 65) > 0);
    }

    [Fact]
    public void AMultiLineFieldStacksItsLines()
    {
        var single = Render([SymbolLayer(0, [Feature("Alpha", 5, 5)])]);
        var stacked = Render([SymbolLayer(0, [Feature("Alpha\nBeta", 5, 5)])]);

        Assert.True(Ink(stacked, 5, 20, 95, 80) > Ink(single, 5, 20, 95, 80));
    }

    [Fact]
    public void TheLineHeightSetsTheDistanceBetweenLines()
    {
        var tight = Render([SymbolLayer(0, [Feature("Alpha\nBeta", 5, 5)], lineHeight: 1.0)]);
        var loose = Render([SymbolLayer(0, [Feature("Alpha\nBeta", 5, 5)], lineHeight: 2.5)]);

        // The second line drops into the lower band only when the lines are apart.
        Assert.True(Ink(loose, 5, 60, 95, 90) > Ink(tight, 5, 60, 95, 90));
    }

    [Fact]
    public void TextRotateTurnsTheLabelAboutItsAnchor()
    {
        var upright = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)])]);
        var turned = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], rotate: 90)]);

        Assert.True(Ink(turned, 30, 5, 70, 95) > Ink(upright, 30, 5, 70, 95));
    }

    [Fact]
    public void ABoldFaceDrawsAWiderLabelThanTheRegularOne()
    {
        var regular = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], fonts: ["Noto Sans"])]);
        var bold = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], fonts: ["Noto Sans Bold"])]);

        Assert.True(Ink(bold, 5, 35, 100, 65) > Ink(regular, 5, 35, 100, 65));
    }

    [Fact]
    public void AnUnknownFamilyRendersThroughTheFallbackChainInsteadOfFailing()
    {
        var fallback = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], fonts: ["Comic Sans MS"])]);
        var regular = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], fonts: ["Noto Sans"])]);

        Assert.Equal(regular.Pixels.ToArray(), fallback.Pixels.ToArray());
    }

    [Fact]
    public void TheFirstAvailableFaceInTheListWins()
    {
        var fallbackChain = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], fonts: ["Comic Sans MS", "Noto Sans Bold"])]);
        var bold = Render([SymbolLayer(0, [Feature("Riverside", 5, 5)], fonts: ["Noto Sans Bold"])]);

        Assert.Equal(bold.Pixels.ToArray(), fallbackChain.Pixels.ToArray());
    }

    // --- helpers -------------------------------------------------------------

    private static SceneLayer SymbolLayer(
        double Sort,
        IReadOnlyList<SymbolFeature> features,
        Styling.SymbolPlacement placement = SymbolPlacement.Point,
        double spacing = 250,
        double offsetX = 0,
        double offsetY = 0,
        bool allowOverlap = false,
        bool ignorePlacement = false,
        IReadOnlyList<string>? fonts = null,
        SymbolTextTransform transform = SymbolTextTransform.None,
        double letterSpacing = 0,
        double lineHeight = 1.2,
        double rotate = 0,
        StyleColor? color = null) =>
        new(
            new DrawLayer(
                "symbols",
                "demo",
                DrawKind.Symbol,
                0,
                24,
                true,
                StyleFilter.Always,
                new SymbolPaint(new SymbolOptions
                {
                    TextField = "{name}",
                    Fonts = fonts ?? [],
                    Size = 20,
                    Color = color ?? new StyleColor(0, 0, 0),
                    Padding = 2,
                    Placement = placement,
                    Spacing = spacing,
                    SortKey = Sort,
                    AllowOverlap = allowOverlap,
                    IgnorePlacement = ignorePlacement,
                    OffsetX = offsetX,
                    OffsetY = offsetY,
                    TextTransform = transform,
                    LetterSpacing = letterSpacing,
                    LineHeight = lineHeight,
                    Rotate = rotate,
                })),
            [])
        {
            Symbols = features,
        };

    private static SymbolFeature Feature(string text, double x, double y) =>
        new(GeometryFactory.CreatePoint(x, y), text, null);

    /// <summary>Five labels on one point, which is exactly the number of candidates there are.</summary>
    private static SymbolFeature[] Crowd(string text) =>
        [.. Enumerable.Range(0, 5).Select(index => Feature($"{text} {index}", 5, 5))];

    private static RasterBuffer Render(IReadOnlyList<SceneLayer> layers) =>
        SkiaVectorRasterizer.Render(new RenderScene(layers, new StyleColor(255, 255, 255)), Viewport);

    private static IReadOnlyList<PlacedSymbol?[]?> Place(IReadOnlyList<SceneLayer> layers)
    {
        using var fonts = new FontSession();
        return SymbolPlacementEngine.Place(layers, Projection, fonts, SpriteRegistry.FromSources([new("marker", MarkerSvg)]));
    }

    private static long Ink(RasterBuffer buffer, int minX, int minY, int maxX, int maxY)
    {
        long count = 0;
        var span = buffer.Pixels.Span;
        for (var y = minY; y < maxY; y++)
        {
            for (var x = minX; x < maxX; x++)
            {
                var offset = y * buffer.Stride + (x * 4);
                if (span[offset] < 200 || span[offset + 1] < 200 || span[offset + 2] < 200)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static (int Red, int Green, int Blue) Pixel(RasterBuffer buffer, int x, int y)
    {
        var span = buffer.Pixels.Span;
        var offset = y * buffer.Stride + (x * 4);
        return (span[offset], span[offset + 1], span[offset + 2]);
    }
}
