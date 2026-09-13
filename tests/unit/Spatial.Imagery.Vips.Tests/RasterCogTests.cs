using NetVips;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Raster;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Tests;

/// <summary>
/// COG / tiled GeoTIFF support (ADR-0051, plan I4): the provider reports the
/// file's tile and internal-overview structure, export reads an overview for
/// a downscale without upscaling, and the COG writer round-trips a tiled,
/// pyramidal file. All over the pinned NetVips path, no new dependency.
/// </summary>
public sealed class RasterCogTests
{
    private const string Crs = "EPSG:4326";

    [Fact]
    public async Task Describe_reports_the_tile_and_pyramid_structure()
    {
        using var fixture = new TiledRasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var description = await catalogue.DescribeAsync("raster");

        Assert.Equal(TiledRasterFixture.TileSize, description.Raster.BlockWidth);
        Assert.Equal(TiledRasterFixture.TileSize, description.Raster.BlockHeight);
        Assert.Equal(1, description.Raster.FirstPyramidLevel);
        Assert.True(description.Raster.MaxPyramidLevel >= 1);
    }

    [Fact]
    public async Task Describe_reports_no_structure_for_a_striped_raster()
    {
        using var fixture = new RasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var description = await catalogue.DescribeAsync("raster");

        Assert.Equal(0, description.Raster.BlockWidth);
        Assert.Equal(0, description.Raster.BlockHeight);
        Assert.Equal(0, description.Raster.FirstPyramidLevel);
        Assert.Equal(0, description.Raster.MaxPyramidLevel);
    }

    [Theory]
    [InlineData(4, 64, 48, 16, 12, 2)]
    [InlineData(2, 64, 48, 16, 12, 2)]
    [InlineData(2, 64, 48, 33, 25, 0)]
    [InlineData(2, 64, 48, 64, 48, 0)]
    [InlineData(0, 64, 48, 1, 1, 0)]
    [InlineData(4, 8, 6, 4, 3, 1)]
    public void Pyramid_selection_stops_before_upscaling(
        int maxLevel, int sourceWidth, int sourceHeight, int outputWidth, int outputHeight, int expected)
    {
        Assert.Equal(expected, RasterPyramid.Select(maxLevel, sourceWidth, sourceHeight, outputWidth, outputHeight));
    }

    [Fact]
    public void Scale_window_rounds_outwards_and_clamps_to_the_level()
    {
        Assert.Equal((4, 2, 8, 6), RasterPyramid.ScaleWindow((17, 9, 31, 21), 64, 48, 16, 12));
        Assert.Equal((15, 11, 1, 1), RasterPyramid.ScaleWindow((60, 44, 8, 8), 64, 48, 16, 12));
    }

    [Fact]
    public async Task Export_at_full_resolution_matches_the_source()
    {
        using var fixture = new TiledRasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), fixture.Width, fixture.Height, Crs);

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png));

        using var decoded = Image.NewFromBuffer(image.Content);
        Assert.Equal(fixture.Width, decoded.Width);
        Assert.Equal(fixture.Height, decoded.Height);
        Assert.Equal(TiledRasterFixture.Value(3, 2), decoded.Getpoint(3, 2)[0], 3);
    }

    [Fact]
    public async Task Export_downscales_a_pyramidal_raster()
    {
        using var fixture = new TiledRasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), 16, 12, Crs);

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png));

        using var decoded = Image.NewFromBuffer(image.Content);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(12, decoded.Height);
        Assert.True(decoded.Getpoint(15, 6)[0] > decoded.Getpoint(0, 6)[0]);
    }

    [Fact]
    public async Task Export_offsets_the_window_onto_the_overview()
    {
        using var fixture = new TiledRasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());
        var viewport = new RasterViewport(new Envelope(32, 24, 64, 48), 8, 6, Crs);

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png));

        using var decoded = Image.NewFromBuffer(image.Content);
        Assert.Equal(8, decoded.Width);
        Assert.Equal(6, decoded.Height);
        // The lower-right quadrant of the gradient is much brighter than the top-left.
        Assert.True(decoded.Getpoint(7, 5)[0] > TiledRasterFixture.Value(0, 0));
    }

    [Fact]
    public async Task Cog_write_produces_a_tiled_pyramid()
    {
        using var fixture = new TiledRasterFixture(width: 512, height: 512, tiled: false);
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());
        var destination = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-cog-out-{Guid.NewGuid():N}.tif");
        try
        {
            await catalogue.WriteCogAsync("raster", rasterId: null, destination);

            using var image = Image.NewFromFile(destination);
            Assert.Equal(VipsRasterCog.TileSize, VipsRasterStructure.TileWidth(image));
            Assert.Equal(VipsRasterCog.TileSize, VipsRasterStructure.TileHeight(image));
            Assert.True(VipsRasterStructure.PyramidLevels(image) >= 1);
        }
        finally
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }
        }
    }
}

/// <summary>
/// A deterministic gradient TIFF (value = x + y), written either tiled +
/// pyramidal (the COG layout) or as a plain striped file.
/// </summary>
internal sealed class TiledRasterFixture : IDisposable
{
    public const int TileSize = 16;

    public TiledRasterFixture(int width = 64, int height = 48, int tileSize = TileSize, bool tiled = true)
    {
        Width = width;
        Height = height;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-tiled-{Guid.NewGuid():N}.tif");
        var pixels = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                pixels[(y * width) + x] = (byte)Value(x, y);
            }
        }

        using var image = Image.NewFromMemory(pixels, width, height, 1, Enums.BandFormat.Uchar);
        if (tiled)
        {
            image.Tiffsave(
                Path,
                compression: Enums.ForeignTiffCompression.Deflate,
                predictor: Enums.ForeignTiffPredictor.Horizontal,
                tile: true,
                tileWidth: tileSize,
                tileHeight: tileSize,
                pyramid: true,
                subifd: true);
        }
        else
        {
            image.WriteToFile(Path);
        }
    }

    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    public static double Value(int x, int y) => x + y;

    public RasterDatasetDescriptor Dataset() =>
        new("raster", Path, "EPSG:4326", new Envelope(0, 0, Width, Height), 1, 1, "Fixture");

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}
