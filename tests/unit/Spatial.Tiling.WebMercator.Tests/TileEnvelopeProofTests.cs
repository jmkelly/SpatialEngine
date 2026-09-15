using System.Text.Json;
using Spatial.Contracts;

namespace Spatial.Tiling.WebMercator.Tests;

/// <summary>
/// T-048 LOD byte-proof (research/compat/tiles.md §2, G1
/// <c>map-root-cached.WorldTopo.json</c>): envelope-equality of our
/// <c>tile/{z}/{y}/{x}</c> with the cached grid. Pixels differ by style
/// (ours) vs Esri cartography, so pixel-equality is never the assertion —
/// envelopes must agree. The expected envelopes are derived from the cached
/// <c>tileInfo</c> itself (top-left origin plus LOD resolution × 256 px per
/// tile, row 0 at the top), so this replays the ground-truth grid rather
/// than our own constants. Agreement is asserted to within half a pixel
/// per zoom: Esri truncates its LOD table (their L0 resolution
/// <c>156543.03392800014</c> vs the exact <c>2πR/256</c>) and its origin
/// shift at the micrometre, so the truncation drift grows with the tile
/// index and byte-equality is unachievable — but the served tile still
/// covers the same pixels as the cached grid. The MapServer <c>tile/{z}/{y}/{x}</c> route renders
/// <c>ITileScheme.Bounds</c> directly, so this scheme-level proof covers the
/// served envelope.
/// </summary>
public sealed class TileEnvelopeProofTests
{
    private static readonly JsonElement TileInfo = GroundTruthTileInfo();

    private static JsonElement GroundTruthTileInfo()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-root-cached.WorldTopo.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("body").GetProperty("tileInfo").Clone();
    }

    public static TheoryData<int, int, int> SampleTiles => new()
    {
        { 0, 0, 0 },
        { 1, 0, 0 },
        { 1, 1, 1 },
        { 5, 10, 20 },
        { 10, 300, 511 },
        { 15, 12345, 22222 },
        { 23, 0, 0 },
        { 23, 4194303, 4194303 },
        { 23, 2345678, 1234567 },
    };

    [Theory]
    [MemberData(nameof(SampleTiles))]
    public void Tile_envelopes_agree_with_the_cached_grid(int zoom, int x, int y)
    {
        var scheme = new WebMercatorTileScheme();
        var expected = CachedEnvelope(zoom, x, y);
        var actual = scheme.Bounds(new TileCoordinate(zoom, x, y));
        var halfPixel = TileInfo.GetProperty("lods")[zoom].GetProperty("resolution").GetDouble() / 2;

        Assert.True(Math.Abs(expected.MinX - actual.MinX) < halfPixel, $"MinX drifts at {zoom}/{x}/{y}.");
        Assert.True(Math.Abs(expected.MinY - actual.MinY) < halfPixel, $"MinY drifts at {zoom}/{x}/{y}.");
        Assert.True(Math.Abs(expected.MaxX - actual.MaxX) < halfPixel, $"MaxX drifts at {zoom}/{x}/{y}.");
        Assert.True(Math.Abs(expected.MaxY - actual.MaxY) < halfPixel, $"MaxY drifts at {zoom}/{x}/{y}.");
    }

    [Theory]
    [MemberData(nameof(SampleTiles))]
    public void Tile_spans_match_the_cached_lod_resolution(int zoom, int x, int y)
    {
        var scheme = new WebMercatorTileScheme();
        var resolution = TileInfo.GetProperty("lods")[zoom].GetProperty("resolution").GetDouble();
        var actual = scheme.Bounds(new TileCoordinate(zoom, x, y));

        Assert.Equal(resolution * 256, actual.Width, 3);
        Assert.Equal(resolution * 256, actual.Height, 3);
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) CachedEnvelope(int zoom, int x, int y)
    {
        var origin = TileInfo.GetProperty("origin");
        var resolution = TileInfo.GetProperty("lods")[zoom].GetProperty("resolution").GetDouble();
        var size = TileInfo.GetProperty("cols").GetInt32();
        var step = resolution * size;
        var minX = origin.GetProperty("x").GetDouble() + (x * step);
        var maxY = origin.GetProperty("y").GetDouble() - (y * step);
        return (minX, maxY - step, minX + step, maxY);
    }
}
