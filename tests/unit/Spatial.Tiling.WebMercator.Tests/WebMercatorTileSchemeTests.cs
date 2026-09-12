using Spatial.PluginSdk;
using Spatial.Tiling.WebMercator;

namespace Spatial.Tiling.WebMercator.Tests;

/// <summary>
/// The Web-Mercator tiling scheme (ADR-0046): the standard XYZ
/// level-of-detail math, tile extents and address validation, pinned against
/// the published origin shift and resolution.
/// </summary>
public sealed class WebMercatorTileSchemeTests
{
    private const double Shift = WebMercatorTileScheme.OriginShift;

    private static readonly WebMercatorTileScheme Scheme = new();

    [Fact]
    public void Scheme_describes_the_standard_web_mercator_shape()
    {
        Assert.Equal("webmercator", Scheme.Id);
        Assert.Equal("EPSG:3857", Scheme.Crs);
        Assert.Equal(256, Scheme.TileSize);
        Assert.Equal(0, Scheme.MinZoom);
        Assert.Equal(23, Scheme.MaxZoom);
        Assert.Equal(24, Scheme.Levels.Count);
    }

    [Fact]
    public void Zoom_zero_covers_the_whole_projected_world()
    {
        var bounds = Scheme.Bounds(new TileCoordinate(0, 0, 0));

        Assert.Equal(-Shift, bounds.MinX, 6);
        Assert.Equal(-Shift, bounds.MinY, 6);
        Assert.Equal(Shift, bounds.MaxX, 6);
        Assert.Equal(Shift, bounds.MaxY, 6);
    }

    [Fact]
    public void Zoom_one_splits_the_world_into_quadrants_with_a_top_left_origin()
    {
        var topLeft = Scheme.Bounds(new TileCoordinate(1, 0, 0));
        var bottomRight = Scheme.Bounds(new TileCoordinate(1, 1, 1));

        Assert.Equal(-Shift, topLeft.MinX, 6);
        Assert.Equal(0, topLeft.MinY, 6);
        Assert.Equal(0, topLeft.MaxX, 6);
        Assert.Equal(Shift, topLeft.MaxY, 6);

        Assert.Equal(0, bottomRight.MinX, 6);
        Assert.Equal(-Shift, bottomRight.MinY, 6);
        Assert.Equal(Shift, bottomRight.MaxX, 6);
        Assert.Equal(0, bottomRight.MaxY, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(23)]
    public void Tile_bounds_are_square_and_match_the_level_resolution(int zoom)
    {
        var tile = new TileCoordinate(zoom, 0, 0);
        var bounds = Scheme.Bounds(tile);

        Assert.Equal(bounds.Width, bounds.Height, 6);
        Assert.Equal(Scheme.Resolution(zoom), bounds.Width / Scheme.TileSize, 6);
    }

    [Fact]
    public void Resolution_halves_at_every_zoom_and_matches_the_published_origin()
    {
        Assert.Equal(156543.03392804097, Scheme.Resolution(0), 6);
        Assert.Equal(Scheme.Resolution(0) / 2, Scheme.Resolution(1), 9);
        Assert.Equal(Scheme.Resolution(10) / 2, Scheme.Resolution(11), 9);
    }

    [Fact]
    public void Levels_are_indexed_by_zoom_with_resolution_and_scale()
    {
        Assert.All(Scheme.Levels, level =>
        {
            Assert.Equal(level.Zoom, Scheme.Levels[level.Zoom].Zoom);
            Assert.Equal(Scheme.Resolution(level.Zoom), level.Resolution, 9);
            Assert.True(level.ScaleDenominator > 0);
        });
        Assert.Equal(591658710.909131, Scheme.Levels[0].ScaleDenominator, 3);
        Assert.Equal(Scheme.Levels[0].ScaleDenominator / 2, Scheme.Levels[1].ScaleDenominator, 6);
    }

    [Theory]
    [InlineData(-1, 0, 0, false)]
    [InlineData(0, 0, 0, true)]
    [InlineData(0, 1, 0, false)]
    [InlineData(1, 0, 1, true)]
    [InlineData(1, 2, 0, false)]
    [InlineData(1, 0, 2, false)]
    [InlineData(24, 0, 0, false)]
    public void IsValid_accepts_only_in_range_addresses(int z, int x, int y, bool valid)
    {
        Assert.Equal(valid, Scheme.IsValid(new TileCoordinate(z, x, y)));
    }

    [Fact]
    public void Bounds_rejects_an_out_of_range_address()
    {
        var exception = Assert.Throws<SpatialException>(() => Scheme.Bounds(new TileCoordinate(2, 4, 0)));

        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public void Tile_size_scales_the_resolution_but_not_the_extent()
    {
        var large = new WebMercatorTileScheme(tileSize: 512);

        Assert.Equal(Scheme.Resolution(0) / 2, large.Resolution(0), 9);
        Assert.Equal(Scheme.Bounds(new TileCoordinate(0, 0, 0)).Width, large.Bounds(new TileCoordinate(0, 0, 0)).Width, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_rejects_a_non_positive_tile_size(int tileSize)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebMercatorTileScheme(tileSize: tileSize));
    }

    [Fact]
    public void Constructor_rejects_an_out_of_range_max_zoom()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebMercatorTileScheme(maxZoom: 31));
    }
}
