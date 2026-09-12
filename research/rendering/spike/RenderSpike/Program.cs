namespace RenderSpike;

using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using Spatial.Operations.NetTopologySuite;
using Spatial.Transformations.ProjNet;

/// <summary>
/// Rendering pipeline spike: core geometry -> CRS transform -> Skia vector
/// raster -> NetVips imagery composite/encode. Run with <c>--bench</c> for
/// per-stage throughput numbers, <c>--imagery path.tif</c> to composite over
/// a real raster basemap.
/// </summary>
public static class Program
{
    private const string SampleCss = """
        /* CSS-ish styling, lowered to per-layer paint recipes. */
        parks  { fill: #2e7d32; fill-opacity: 0.35; fill-outline: #a5d6a7; fill-outline-width: 1.5; }
        roads  { stroke: #ffcc80; stroke-width: 2.5; stroke-dash: 8 4; }
        cities { circle: #ffffff; circle-radius: 5; circle-stroke: #0b0f14; circle-stroke-width: 2; }
        """;

    public static void Main(string[] args)
    {
        var imagery = ArgumentValue(args, "--imagery");
        if (args.Contains("--bench", StringComparer.Ordinal))
        {
            Bench(imagery);
            return;
        }

        RenderSample(imagery);
    }

    private static void RenderSample(string? imagery)
    {
        var styles = StyleSheet.Parse(SampleCss);
        var viewport = Viewport.FromLonLat(-0.28, 51.44, 0.08, 51.56, 1024, 576);
        using var overlay = SkiaVectorRenderer.Render(
            TransformToMercator(SpikeData.Sample()),
            styles,
            viewport,
            new NtsGeometryOperations(),
            simplifyTolerance: viewport.UnitsPerPixel);

        var png = ImageryCompositor.Compose(ToRgba(overlay, viewport), viewport.Width, viewport.Height, imagery);
        var path = Path.Combine(OutputDirectory(), "sample.png");
        File.WriteAllBytes(path, png);

        Console.WriteLine($"viewport   {viewport.Width}x{viewport.Height}  ({viewport.UnitsPerPixel:F1} m/px)");
        Console.WriteLine($"layers     {string.Join(", ", styles.Layers.Keys)}");
        Console.WriteLine($"imagery    {imagery ?? "(synthetic field)"}");
        Console.WriteLine($"output     {path}  ({png.Length / 1024} KiB)");
    }

    private static void Bench(string? imagery)
    {
        var styles = StyleSheet.Parse(SampleCss);
        var operations = new NtsGeometryOperations();
        var viewport = Viewport.FromLonLat(-0.28, 51.44, 0.08, 51.56, 512, 512);
        var tolerance = viewport.UnitsPerPixel;

        Console.WriteLine($"render benchmark — {viewport.Width}x{viewport.Height}, {Environment.ProcessorCount} cores, tolerance {tolerance:F1} m");
        Console.WriteLine($"{"case",-24} {"verts",9} {"simplify",9} {"raster",9} {"compose",9} {"pipe ms",9} {"pipe/s",8}");
        Console.WriteLine(new string('-', 82));

        foreach (var (label, layers) in BenchCases())
        {
            var projected = TransformToMercator(layers);
            var vertices = projected.Sum(l => l.Geometry.CoordinateCount);

            var simplifyMs = Time(5, () =>
            {
                foreach (var layer in projected)
                {
                    _ = operations.Simplify(layer.Geometry, tolerance);
                }
            });

            var simplified = projected
                .Select(layer => layer with { Geometry = operations.Simplify(layer.Geometry, tolerance) })
                .ToList();

            using var raster = SkiaVectorRenderer.Render(simplified, styles, viewport);
            var rgba = ToRgba(raster, viewport);

            var rasterMs = Time(20, () =>
            {
                using var bitmap = SkiaVectorRenderer.Render(simplified, styles, viewport);
            });

            var composeMs = Time(20, () =>
            {
                _ = ImageryCompositor.Compose(rgba, viewport.Width, viewport.Height, imagery);
            });

            var pipeMs = simplifyMs + rasterMs + composeMs;
            Console.WriteLine(
                $"{label,-24} {vertices,9:N0} {simplifyMs,9:F2} {rasterMs,9:F2} {composeMs,9:F2} {pipeMs,9:F2} {1000 / pipeMs,8:F1}");
        }

        ParallelTiles(styles);
        TuningLevers();
    }

    private static void TuningLevers()
    {
        var viewport = Viewport.FromLonLat(-0.28, 51.44, 0.08, 51.56, 512, 512);
        var operations = new NtsGeometryOperations();
        var layers = TransformToMercator(SpikeData.BenchmarkNetwork(500, 64))
            .Select(layer => layer with { Geometry = operations.Simplify(layer.Geometry, viewport.UnitsPerPixel) })
            .ToList();

        var dashed = StyleSheet.Parse("roads { stroke: #ffcc80; stroke-width: 2.5; stroke-dash: 8 4; }");
        var solid = StyleSheet.Parse("roads { stroke: #ffcc80; stroke-width: 2.5; }");
        var dashedMs = Time(20, () =>
        {
            using var bitmap = SkiaVectorRenderer.Render(layers, dashed, viewport);
        });
        var solidMs = Time(20, () =>
        {
            using var bitmap = SkiaVectorRenderer.Render(layers, solid, viewport);
        });

        Console.WriteLine(
            $"levers     dashed {dashedMs:F1} ms vs solid {solidMs:F1} ms for 32k vertices " +
            $"({dashedMs / solidMs:F2}x)");
    }

    private static void ParallelTiles(StyleSheet styles)
    {
        var viewport = Viewport.FromLonLat(-0.28, 51.44, 0.08, 51.56, 512, 512);
        var operations = new NtsGeometryOperations();
        var layers = TransformToMercator(SpikeData.BenchmarkNetwork(500, 64))
            .Select(layer => layer with { Geometry = operations.Simplify(layer.Geometry, viewport.UnitsPerPixel) })
            .ToList();

        const int tiles = 96;
        for (var i = 0; i < 4; i++)
        {
            using var warm = SkiaVectorRenderer.Render(layers, styles, viewport);
        }

        var watch = Stopwatch.StartNew();
        Parallel.For(0, tiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, tile =>
        {
            using var bitmap = SkiaVectorRenderer.Render(layers, styles, viewport);
            _ = ImageryCompositor.Compose(ToRgba(bitmap, viewport), viewport.Width, viewport.Height);
        });

        watch.Stop();
        Console.WriteLine();
        Console.WriteLine(
            $"parallel   {tiles} tiles on {Environment.ProcessorCount} threads in {watch.ElapsedMilliseconds} ms => " +
            $"{tiles / watch.Elapsed.TotalSeconds:F1} tiles/s end-to-end");
    }

    private static double Time(int iterations, Action action)
    {
        action();
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < iterations; i++)
        {
            action();
        }

        watch.Stop();
        return watch.Elapsed.TotalMilliseconds / iterations;
    }

    private static IEnumerable<(string Label, IReadOnlyList<RenderLayer> Layers)> BenchCases()
    {
        yield return ("lines=50   verts=32", SpikeData.BenchmarkNetwork(50, 32));
        yield return ("lines=500  verts=64", SpikeData.BenchmarkNetwork(500, 64));
        yield return ("lines=2000 verts=64", SpikeData.BenchmarkNetwork(2000, 64));
    }

    private static List<RenderLayer> TransformToMercator(IReadOnlyList<RenderLayer> layers)
    {
        var transforms = new ProjNetTransforms();
        return layers
            .Select(layer => layer with { Geometry = transforms.Transform(layer.Geometry, "EPSG:4326", "EPSG:3857") })
            .ToList();
    }

    private static byte[] ToRgba(SKBitmap bitmap, Viewport viewport)
    {
        var bytes = new byte[viewport.Width * viewport.Height * 4];
        Marshal.Copy(bitmap.GetPixels(), bytes, 0, bytes.Length);
        return bytes;
    }

    private static string? ArgumentValue(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string OutputDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "render-spike");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
