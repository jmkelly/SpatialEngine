using SkiaSharp;
using Spatial.Contracts;
using Spatial.Core.Geometry;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// Fixed-extent symbol renders pinned against committed golden images: one for
/// point placement and one for line placement with a bold face, a sprite and a
/// text transform (ADR-0049, ADR-0080). On the CI Linux image the PNG bytes
/// must match exactly; on other platforms a bounded per-pixel tolerance is
/// allowed. Set <c>SPATIAL_GOLDEN_UPDATE=1</c> to regenerate a golden.
/// </summary>
public sealed class SymbolGoldenTests
{
    private const int Tolerance = 8;
    private const double MaxMismatchRatio = 0.01;

    private const string Style = """
        { "version": 8, "layers": [
            { "id": "bg", "type": "background", "paint": { "background-color": "#101820" } },
            { "id": "cities", "type": "circle", "source-layer": "demo.cities",
              "paint": { "circle-color": "#ffd166", "circle-radius": 4 } },
            { "id": "labels", "type": "symbol", "source-layer": "demo.cities",
              "layout": { "text-field": "{name}", "text-size": 16, "text-anchor": "center",
                          "text-offset": [0, 2.2], "text-padding": 2, "icon-image": "default-marker", "icon-size": 1 },
              "paint": { "text-color": "#ffffff", "text-halo-color": "#0b1220", "text-halo-width": 2 } } ] }
        """;

    private const string LineStyle = """
        { "version": 8, "layers": [
            { "id": "bg", "type": "background", "paint": { "background-color": "#101820" } },
            { "id": "rivers", "type": "line", "source-layer": "demo.rivers",
              "paint": { "line-color": "#2a9d8f", "line-width": 2, "line-cap": "round" } },
            { "id": "river-labels", "type": "symbol", "source-layer": "demo.rivers",
              "layout": { "text-field": "{name}", "text-size": 13, "text-font": ["Noto Sans Bold"],
                          "text-transform": "uppercase", "text-letter-spacing": 0.06,
                          "text-anchor": "top", "text-offset": [0, 1.2], "text-padding": 2,
                          "symbol-placement": "line", "symbol-spacing": 90 },
              "paint": { "text-color": "#e9f5f9", "text-halo-color": "#0b1220", "text-halo-width": 2 } },
            { "id": "cities", "type": "symbol", "source-layer": "demo.cities",
              "layout": { "text-field": "{name}", "text-size": 14, "text-offset": [0, 2.4],
                          "icon-image": "marker-circle", "icon-size": 0.8 },
              "paint": { "text-color": "#ffffff", "text-halo-color": "#0b1220", "text-halo-width": 2 } } ] }
        """;

    [Fact]
    public async Task Golden_RendersSymbolsAtAFixedExtent() =>
        await AssertGolden("symbols.png", RenderAsync(Style, PointStore()));

    /// <summary>Line placement, a bold face, a text transform and a sprite, all in one fixed render.</summary>
    [Fact]
    public async Task Golden_RendersLinePlacedLabelsAtAFixedExtent() =>
        await AssertGolden("symbols-line.png", RenderAsync(LineStyle, LineStore()));

    private static FakeStore PointStore() => new(
        TestFeatures.Schema,
        TestFeatures.Point("Alpha", 0, 0),
        TestFeatures.Point("Beta", 4, 3),
        TestFeatures.Point("Gamma", -4, -3));

    private static FakeStore LineStore() => new(
        TestFeatures.Schema,
        TestFeatures.Line("Thames", (-9, -2), (2, 3)),
        TestFeatures.Line("Severn", (-6, 7), (8, 5)),
        TestFeatures.Point("Bristol", 2, 3),
        TestFeatures.Point("Cardiff", 8, 5));

    private static async Task<byte[]> RenderAsync(string style, FakeStore store)
    {
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 256, 256, "EPSG:4326"),
            style,
            [
                new MapLayerSource("demo.cities", store, new FakeCatalogue(4326)),
                new MapLayerSource("demo.rivers", store, new FakeCatalogue(4326)),
            ]);

        var image = await renderer.RenderAsync(request);
        return image.Content;
    }

    private static async Task AssertGolden(string name, Task<byte[]> rendered)
    {
        var content = await rendered;
        var path = GoldenPath(name);

        if (Environment.GetEnvironmentVariable("SPATIAL_GOLDEN_UPDATE") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return;
        }

        Assert.True(File.Exists(path), $"The golden image '{path}' is missing; run with SPATIAL_GOLDEN_UPDATE=1.");
        var golden = File.ReadAllBytes(path);
        if (Strict())
        {
            Assert.Equal(golden, content);
            return;
        }

        AssertWithinTolerance(golden, content);
    }

    private static void AssertWithinTolerance(byte[] golden, byte[] actual)
    {
        using var expected = SKBitmap.Decode(golden);
        using var rendered = SKBitmap.Decode(actual);
        Assert.NotNull(expected);
        Assert.NotNull(rendered);
        Assert.Equal(expected.Width, rendered.Width);
        Assert.Equal(expected.Height, rendered.Height);

        long mismatched = 0;
        for (var y = 0; y < expected.Height; y++)
        {
            for (var x = 0; x < expected.Width; x++)
            {
                if (Differs(expected.GetPixel(x, y), rendered.GetPixel(x, y)))
                {
                    mismatched++;
                }
            }
        }

        var ratio = (double)mismatched / (expected.Width * expected.Height);
        Assert.True(ratio <= MaxMismatchRatio, $"Golden mismatch ratio {ratio:P2} exceeds {MaxMismatchRatio:P2}.");
    }

    private static bool Differs(SKColor left, SKColor right) =>
        Math.Abs(left.Red - right.Red) > Tolerance
        || Math.Abs(left.Green - right.Green) > Tolerance
        || Math.Abs(left.Blue - right.Blue) > Tolerance
        || Math.Abs(left.Alpha - right.Alpha) > Tolerance;

    private static bool Strict() =>
        Environment.GetEnvironmentVariable("CI") is not null
        || Environment.GetEnvironmentVariable("SPATIAL_GOLDEN_STRICT") == "1";

    private static string GoldenPath(string name) =>
        Path.Combine(RepositoryRoot(), "tests", "fixtures", "rendering", "golden", name);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("The repository root could not be located.");
    }
}
