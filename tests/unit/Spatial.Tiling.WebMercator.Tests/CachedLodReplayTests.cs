using System.Text.Json;

namespace Spatial.Tiling.WebMercator.Tests;

/// <summary>
/// T-040 cached-root honesty (research/compat/tiles.md §2, G1
/// <c>map-root-cached.WorldTopo.json</c>): the served Web-Mercator scheme's
/// LOD grid must reproduce the ground-truth rows for the overlapping levels
/// (0–23). Esri's own scale table carries display rounding (their scale is
/// not exactly resolution·96/0.0254), so scales compare with a display
/// tolerance while resolutions compare tightly.
/// </summary>
public sealed class CachedLodReplayTests
{
    private static readonly JsonElement TileInfo = GroundTruthTileInfo();

    private static JsonElement GroundTruthTileInfo()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "map-root-cached.WorldTopo.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("body").GetProperty("tileInfo").Clone();
    }

    [Fact]
    public void The_scheme_serves_twenty_four_levels_like_the_cached_root()
    {
        var scheme = new WebMercatorTileScheme();

        Assert.Equal(24, scheme.Levels.Count);
        Assert.Equal(24, TileInfo.GetProperty("lods").GetArrayLength());
    }

    [Fact]
    public void Lod_resolutions_reproduce_the_ground_truth_rows()
    {
        var scheme = new WebMercatorTileScheme();
        var lods = TileInfo.GetProperty("lods").EnumerateArray().ToArray();

        for (var zoom = 0; zoom <= scheme.MaxZoom; zoom++)
        {
            var expected = lods[zoom].GetProperty("resolution").GetDouble();
            Assert.Equal(expected, scheme.Resolution(zoom), expected * 1e-9);
        }
    }

    [Fact]
    public void Lod_scales_reproduce_the_ground_truth_rows_within_display_rounding()
    {
        var scheme = new WebMercatorTileScheme();
        var lods = TileInfo.GetProperty("lods").EnumerateArray().ToArray();

        for (var zoom = 0; zoom <= scheme.MaxZoom; zoom++)
        {
            var expected = lods[zoom].GetProperty("scale").GetDouble();
            Assert.Equal(expected, scheme.Levels[zoom].ScaleDenominator, expected * 1e-5);
        }
    }

    [Fact]
    public void The_scheme_origin_matches_the_cached_root_origin()
    {
        var origin = TileInfo.GetProperty("origin");

        Assert.Equal(-WebMercatorTileScheme.OriginShift, origin.GetProperty("x").GetDouble(), 1e-5);
        Assert.Equal(WebMercatorTileScheme.OriginShift, origin.GetProperty("y").GetDouble(), 1e-5);
    }
}
