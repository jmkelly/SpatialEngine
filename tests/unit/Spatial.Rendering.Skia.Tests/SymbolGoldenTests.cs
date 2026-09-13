using SkiaSharp;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>
/// A fixed-extent symbol render pinned against a committed golden image. On
/// the CI Linux image the PNG bytes must match exactly; on other platforms a
/// bounded per-pixel tolerance is allowed (rendering-implementation-plan.md
/// §9). Set <c>SPATIAL_GOLDEN_UPDATE=1</c> to regenerate the golden.
/// </summary>
public sealed class SymbolGoldenTests
{
    private const string GoldenName = "symbols.png";
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

    [Fact]
    public async Task Golden_RendersSymbolsAtAFixedExtent()
    {
        var content = await RenderAsync();
        var path = GoldenPath();

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

    private static async Task<byte[]> RenderAsync()
    {
        var store = new FakeStore(
            TestFeatures.Schema,
            TestFeatures.Point("Alpha", 0, 0),
            TestFeatures.Point("Beta", 4, 3),
            TestFeatures.Point("Gamma", -4, -3));
        var renderer = new MapRenderer(new IdentityTransforms(), new FakeOperations());
        var request = new MapRenderRequest(
            new RasterViewport(new Envelope(-10, -10, 10, 10), 256, 256, "EPSG:4326"),
            Style,
            [new MapLayerSource("demo.cities", store, new FakeCatalogue(4326))]);

        var image = await renderer.RenderAsync(request);
        return image.Content;
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

    private static string GoldenPath() =>
        Path.Combine(RepositoryRoot(), "tests", "fixtures", "rendering", "golden", GoldenName);

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
