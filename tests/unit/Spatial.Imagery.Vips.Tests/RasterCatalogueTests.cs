using NetVips;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Imagery;
using Spatial.Imagery.Vips.Raster;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Tests;

/// <summary>
/// The NetVips raster catalogue (ADR-0051): metadata, catalog items,
/// identify, export cropping/resizing, pixel-type rejection, nodata
/// transparency and cancellation.
/// </summary>
public sealed class RasterCatalogueTests
{
    private const string Crs = "EPSG:4326";

    [Fact]
    public async Task Describe_single_raster_reports_core_metadata()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset(statistics: [new RasterBandStatistics(0, 255, 82.707, 39.838)]));

        var description = await catalogue.DescribeAsync("raster");

        Assert.False(description.HasCatalog);
        Assert.Null(description.ObjectIdField);
        Assert.Null(description.CatalogSchema);
        Assert.Equal("EPSG:4326", description.Raster.Crs);
        Assert.Equal(fixture.Width, description.Raster.Width);
        Assert.Equal(fixture.Height, description.Raster.Height);
        Assert.Equal(1, description.Raster.BandCount);
        Assert.Equal(RasterPixelType.U8, description.Raster.PixelType);
        Assert.Equal(1, description.Raster.PixelSizeX);
        Assert.Equal(new Envelope(0, 0, fixture.Width, fixture.Height), description.Raster.Extent);
        Assert.Equal(82.707, description.Raster.BandStatistics![0].Mean, 3);
    }

    [Fact]
    public async Task Describe_catalog_reports_schema_and_union_extent()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.CatalogDataset());

        var description = await catalogue.DescribeAsync("raster");

        Assert.True(description.HasCatalog);
        Assert.Equal("OBJECTID", description.ObjectIdField);
        Assert.Equal(["OBJECTID", "Shape", "Name"], description.CatalogSchema!.Fields.Select(field => field.Name));
        Assert.Equal(AttributeKind.Geometry, description.CatalogSchema[1].Kind);
        var extent = description.Raster.Extent;
        Assert.Equal(0, extent.MinX);
        Assert.Equal(0, extent.MinY);
    }

    [Fact]
    public async Task List_items_maps_footprints_and_attributes()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.CatalogDataset());

        var items = await catalogue.ListItemsAsync("raster");

        Assert.Single(items);
        var item = items[0];
        Assert.Equal(7, item.ObjectId);
        Assert.IsType<Polygon>(item.Footprint);
        Assert.Equal(fixture.Width, item.Raster.Width);
        Assert.Equal(AttributeKind.Int64, item.Attributes[0].Kind);
        Assert.Equal(7, item.Attributes[0].Int64Value);
        Assert.Equal("first", item.Attributes[2].StringValue);
    }

    [Fact]
    public async Task List_items_without_a_catalog_is_empty()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());

        Assert.Empty(await catalogue.ListItemsAsync("raster"));
    }

    [Fact]
    public async Task An_unknown_dataset_is_not_found()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());

        await Assert.ThrowsAsync<SpatialException>(() => catalogue.DescribeAsync("missing"));
    }

    [Fact]
    public async Task Export_crops_and_resizes_to_the_viewport()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(2, 1, 6, 4), 4, 3, Crs);

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png));

        Assert.Equal(4, image.Width);
        Assert.Equal(3, image.Height);
        using var decoded = Image.NewFromBuffer(image.Content);
        Assert.Equal(4, decoded.Width);
        Assert.Equal(3, decoded.Height);
        Assert.Equal(RasterFixture.Value(2, 2), decoded.Getpoint(0, 0)[0]);
        Assert.Equal(RasterFixture.Value(5, 4), decoded.Getpoint(3, 2)[0]);
    }

    [Fact]
    public async Task Export_reprojects_the_viewport_through_the_transform_service()
    {
        using var fixture = new RasterFixture();
        var transforms = new ScaleTransforms(2.0);
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], transforms);
        // Output viewport in the transformed CRS: source bounds become (0,0,8,4) in EPSG:4326.
        var viewport = new RasterViewport(new Envelope(0, 0, 4, 2), 4, 2, "EPSG:3857");

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Tiff));

        Assert.Equal("EPSG:3857", transforms.LastSource);
        Assert.Equal("EPSG:4326", transforms.LastTarget);
        using var decoded = Image.NewFromBuffer(image.Content);
        Assert.Equal(4, decoded.Width);
        Assert.Equal(2, decoded.Height);
        Assert.Contains(decoded.Getpoint(0, 0)[0], new double[] { RasterFixture.Value(0, 2), RasterFixture.Value(1, 2), RasterFixture.Value(0, 3), RasterFixture.Value(1, 3) });
    }

    [Fact]
    public async Task Export_rejects_an_out_of_extent_viewport()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(100, 100, 104, 103), 4, 3, Crs);

        await Assert.ThrowsAsync<SpatialException>(
            () => catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png)));
    }

    [Theory]
    [InlineData(RasterFormat.Png, "image/png")]
    [InlineData(RasterFormat.Jpeg, "image/jpeg")]
    [InlineData(RasterFormat.Tiff, "image/tiff")]
    public async Task Export_encodes_the_requested_format(RasterFormat format, string mediaType)
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), 4, 4, Crs);

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, format));

        Assert.Equal(mediaType, image.MediaType);
        Assert.NotEmpty(image.Content);
    }

    [Theory]
    [InlineData(RasterInterpolation.NearestNeighbor)]
    [InlineData(RasterInterpolation.Bilinear)]
    [InlineData(RasterInterpolation.CubicConvolution)]
    [InlineData(RasterInterpolation.Majority)]
    public async Task Export_supports_each_interpolation(RasterInterpolation interpolation)
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), 4, 4, Crs);

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png, Interpolation: interpolation));

        Assert.NotEmpty(image.Content);
    }

    [Fact]
    public async Task Export_rejects_an_unsupported_interpolation()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), 4, 4, Crs);

        await Assert.ThrowsAsync<SpatialException>(
            () => catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png, Interpolation: (RasterInterpolation)99)));
    }

    [Fact]
    public async Task Export_rejects_an_unsupported_pixel_type()
    {
        using var fixture = new RasterFixture(bands: 3);
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), 4, 4, Crs);

        var failure = await Assert.ThrowsAsync<SpatialException>(
            () => catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Png, PixelType: RasterPixelType.F32)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task Export_converts_a_single_band_pixel_type()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), 4, 4, Crs);

        var image = await catalogue.ExportAsync("raster", new RasterExportRequest(viewport, RasterFormat.Tiff, PixelType: RasterPixelType.F32));

        using var decoded = Image.NewFromBuffer(image.Content);
        Assert.Equal(Enums.BandFormat.Float, decoded.Format);
    }

    [Fact]
    public async Task Export_applies_nodata_as_transparency()
    {
        using var fixture = new RasterFixture(bands: 3, noDataPixel: true);
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), fixture.Width, fixture.Height, Crs);

        var image = await catalogue.ExportAsync(
            "raster", new RasterExportRequest(viewport, RasterFormat.Png, NoData: 0, Transparent: true));

        using var decoded = Image.NewFromBuffer(image.Content);
        Assert.Equal(4, decoded.Bands);
        Assert.Equal(0, decoded.Getpoint(fixture.NoDataX, fixture.NoDataY)[3]);
        Assert.Equal(255, decoded.Getpoint(fixture.NoDataX + 1, fixture.NoDataY)[3]);
    }

    [Fact]
    public async Task Identify_samples_the_pixel_and_finds_overlapping_items()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.CatalogDataset());
        var point = GeometryFactory.CreatePoint(2.5, 1.5, CoordinateReference.Epsg(4326));

        var result = await catalogue.IdentifyAsync("raster", new RasterIdentifyRequest(point, Crs));

        Assert.Equal(7, result.ObjectId);
        Assert.Single(result.PixelValues);
        Assert.Equal(RasterFixture.Value(2, 4), result.PixelValues[0]);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task Identify_outside_the_raster_returns_no_pixels()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var point = GeometryFactory.CreatePoint(100, 100, CoordinateReference.Epsg(4326));

        var result = await catalogue.IdentifyAsync("raster", new RasterIdentifyRequest(point, Crs));

        Assert.Empty(result.PixelValues);
        Assert.Null(result.ObjectId);
    }

    [Fact]
    public async Task Export_honours_cancellation()
    {
        using var fixture = new RasterFixture();
        var catalogue = Catalogue(fixture.Dataset());
        var viewport = new RasterViewport(new Envelope(0, 0, fixture.Width, fixture.Height), 4, 4, Crs);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => catalogue.ExportAsync("raster", new RasterExportRequest(viewport), new CancellationToken(canceled: true)));
    }

    private static VipsRasterCatalogue Catalogue(RasterDatasetDescriptor dataset) =>
        new([dataset], new IdentityTransforms());
}

/// <summary>
/// A small generated TIFF with deterministic pixel values (value = row * 16 +
/// column) and a descriptor matching the spec's example georeferencing.
/// </summary>
internal sealed class RasterFixture : IDisposable
{
    private readonly byte[] _pixels;

    public RasterFixture(int width = 8, int height = 6, int bands = 1, bool noDataPixel = false)
    {
        Width = width;
        Height = height;
        Bands = bands;
        NoDataX = 1;
        NoDataY = 1;
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-raster-{Guid.NewGuid():N}.tif");
        _pixels = new byte[width * height * bands];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var value = (byte)((y * 16) + x);
                if (noDataPixel && x == NoDataX && y == NoDataY)
                {
                    value = 0;
                }

                for (var band = 0; band < bands; band++)
                {
                    _pixels[((y * width) + x) * bands + band] = value;
                }
            }
        }

        using var image = Image.NewFromMemory(_pixels, width, height, bands, Enums.BandFormat.Uchar);
        image.WriteToFile(Path);
    }

    public string Path { get; }

    public int Width { get; }

    public int Height { get; }

    public int Bands { get; }

    public int NoDataX { get; }

    public int NoDataY { get; }

    public static double Value(int x, int y) => (y * 16) + x;

    public RasterDatasetDescriptor Dataset(IReadOnlyList<RasterBandStatistics>? statistics = null) =>
        new("raster", Path, "EPSG:4326", new Envelope(0, 0, Width, Height), 1, 1, "Fixture", statistics);

    public RasterDatasetDescriptor CatalogDataset()
    {
        var footprint = GeometryFactory.CreatePolygon(
            [
                new Coordinate(0, 0),
                new Coordinate(Width, 0),
                new Coordinate(Width, Height),
                new Coordinate(0, Height),
                new Coordinate(0, 0),
            ],
            CoordinateReference.Epsg(4326));
        var item = new RasterCatalogItemDescriptor(
            7,
            footprint,
            Path,
            new Envelope(0, 0, Width, Height),
            [AttributeValue.FromString("first")]);
        return new RasterDatasetDescriptor(
            "raster",
            Path,
            "EPSG:4326",
            new Envelope(0, 0, Width, Height),
            1,
            1,
            "Fixture catalog",
            CatalogAttributes: [new RasterAttributeDescriptor("Name", AttributeKind.String, Nullable: false)],
            Items: [item]);
    }

    public void Dispose()
    {
        if (File.Exists(Path))
        {
            File.Delete(Path);
        }
    }
}

/// <summary>An identity transform that records the last CRS pair it saw.</summary>
internal sealed class IdentityTransforms : ICoordinateTransforms
{
    public string? LastSource { get; private set; }

    public string? LastTarget { get; private set; }

    public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        LastSource = source;
        LastTarget = target;
        return geometry;
    }
}

/// <summary>A transform that scales every coordinate, standing in for a projected CRS pair.</summary>
internal sealed class ScaleTransforms(double factor) : ICoordinateTransforms
{
    public string? LastSource { get; private set; }

    public string? LastTarget { get; private set; }

    public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        LastSource = source;
        LastTarget = target;
        Coordinate[] coordinates = [.. geometry.Coordinates().Select(c => new Coordinate(c.X * factor, c.Y * factor))];
        return GeometryFactory.CreateLineString(coordinates);
    }
}
