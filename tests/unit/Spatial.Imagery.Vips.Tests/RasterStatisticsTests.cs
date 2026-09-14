using NetVips;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Raster;
using Spatial.PluginSdk;

namespace Spatial.Imagery.Vips.Tests;

/// <summary>
/// Histogram computation and the raster attribute table plumbing (ADR-0054,
/// T-054): full-range 8-bit histograms clipped to the requested bounds,
/// data-driven 256-bin histograms for other real band formats, typed
/// failures for disjoint bounds and complex bands, cancellation, and
/// fail-fast descriptor validation.
/// </summary>
public sealed class RasterStatisticsTests
{
    private const string Crs = "EPSG:4326";

    [Fact]
    public async Task Compute_returns_full_range_histograms()
    {
        using var fixture = new RasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var histograms = await catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(new Envelope(0, 0, fixture.Width, fixture.Height), Crs));

        var histogram = Assert.Single(histograms);
        Assert.Equal(256, histogram.Size);
        Assert.Equal(-0.5, histogram.Min);
        Assert.Equal(255.5, histogram.Max);
        Assert.Equal(fixture.Width * fixture.Height, histogram.Counts.Sum());
        Assert.Equal(1, histogram.Counts[0]);
        Assert.Equal(1, histogram.Counts[87]);
        Assert.Equal(0, histogram.Counts[88]);
    }

    [Fact]
    public async Task Compute_clips_to_the_requested_bounds()
    {
        using var fixture = new RasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var histograms = await catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(new Envelope(0, 0, 4, 3), Crs));

        Assert.Equal(12, Assert.Single(histograms).Counts.Sum());
    }

    [Fact]
    public async Task Compute_rejects_disjoint_and_empty_bounds()
    {
        using var fixture = new RasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var outside = await Assert.ThrowsAsync<SpatialException>(() => catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(new Envelope(100, 100, 104, 106), Crs)));
        Assert.Equal(SpatialException.InvalidArguments, outside.Code);

        var empty = await Assert.ThrowsAsync<SpatialException>(() => catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(Envelope.Empty, Crs)));
        Assert.Equal(SpatialException.InvalidArguments, empty.Code);
    }

    [Fact]
    public async Task Compute_rejects_unknown_datasets()
    {
        using var fixture = new RasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var missing = await Assert.ThrowsAsync<SpatialException>(() => catalogue.ComputeHistogramsAsync(
            "missing", new RasterHistogramRequest(new Envelope(0, 0, 4, 3), Crs)));
        Assert.Equal(SpatialException.NotFound, missing.Code);
    }

    [Fact]
    public async Task Compute_returns_data_driven_histograms_for_float()
    {
        using var fixture = new FloatFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var histograms = await catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(new Envelope(0, 0, 4, 3), Crs));

        var histogram = Assert.Single(histograms);
        Assert.Equal(256, histogram.Size);
        Assert.Equal(0, histogram.Min);
        Assert.Equal(11, histogram.Max);
        Assert.Equal(12, histogram.Counts.Sum());
        Assert.Equal(1, histogram.Counts[0]);
        Assert.Equal(1, histogram.Counts[255]);
    }

    [Fact]
    public async Task Compute_returns_data_driven_histograms_for_ushort()
    {
        using var fixture = new UshortFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var histograms = await catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(new Envelope(0, 0, 4, 3), Crs));

        var histogram = Assert.Single(histograms);
        Assert.Equal(256, histogram.Size);
        Assert.Equal(-0.5, histogram.Min);
        Assert.Equal(11000.5, histogram.Max);
        Assert.Equal(12, histogram.Counts.Sum());
        Assert.Equal(1, histogram.Counts[0]);
    }

    [Fact]
    public async Task Compute_bins_a_flat_raster_in_the_middle_bin()
    {
        using var fixture = new FlatFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var histograms = await catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(new Envelope(0, 0, 4, 3), Crs));

        var histogram = Assert.Single(histograms);
        Assert.Equal(256, histogram.Size);
        Assert.Equal(4.5, histogram.Min);
        Assert.Equal(5.5, histogram.Max);
        Assert.Equal(12, histogram.Counts.Sum());
        Assert.Equal(12, histogram.Counts[128]);
    }

    [Fact]
    public async Task Compute_rejects_complex_bands()
    {
        using var fixture = new ComplexFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        var failure = await Assert.ThrowsAsync<SpatialException>(() => catalogue.ComputeHistogramsAsync(
            "raster", new RasterHistogramRequest(new Envelope(0, 0, 4, 3), Crs)));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task Compute_honours_cancellation()
    {
        using var fixture = new RasterFixture();
        var catalogue = new VipsRasterCatalogue([fixture.Dataset()], new IdentityTransforms());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalogue.ComputeHistogramsAsync(
            "raster",
            new RasterHistogramRequest(new Envelope(0, 0, 4, 3), Crs),
            new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task Describe_carries_the_configured_attribute_table()
    {
        using var fixture = new RasterFixture();
        var table = new RasterAttributeTable(
            "OBJECTID",
            [new RasterAttributeField("OID", AttributeKind.Int64, Nullable: false),
             new RasterAttributeField("ClassName", AttributeKind.String, Length: 50)],
            [[AttributeValue.FromInt64(0), AttributeValue.FromString("Background")]]);
        var catalogue = new VipsRasterCatalogue([fixture.Dataset() with { AttributeTable = table }], new IdentityTransforms());

        var description = await catalogue.DescribeAsync("raster");

        Assert.Equal(table, description.Raster.AttributeTable);
    }

    [Fact]
    public void Catalogue_rejects_a_ragged_attribute_table()
    {
        using var fixture = new RasterFixture();
        var ragged = new RasterAttributeTable(
            "OBJECTID",
            [new RasterAttributeField("OID", AttributeKind.Int64), new RasterAttributeField("Value", AttributeKind.Int64)],
            [[AttributeValue.FromInt64(0)]]);

        var failure = Assert.Throws<SpatialException>(
            () => new VipsRasterCatalogue([fixture.Dataset() with { AttributeTable = ragged }], new IdentityTransforms()));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public void Catalogue_rejects_an_attribute_table_without_columns()
    {
        using var fixture = new RasterFixture();
        var empty = new RasterAttributeTable("OBJECTID", [], []);

        var failure = Assert.Throws<SpatialException>(
            () => new VipsRasterCatalogue([fixture.Dataset() with { AttributeTable = empty }], new IdentityTransforms()));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    /// <summary>A 16-bit unsigned raster with values spanning more than 256 levels.</summary>
    private sealed class UshortFixture : IDisposable
    {
        public UshortFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-raster-ushort-{Guid.NewGuid():N}.tif");
            var pixels = new ushort[4 * 3];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (ushort)(i * 1000);
            }

            var bytes = new byte[pixels.Length * 2];
            Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
            using var image = Image.NewFromMemory(bytes, 4, 3, 1, Enums.BandFormat.Ushort);
            image.WriteToFile(Path);
        }

        public string Path { get; }

        public RasterDatasetDescriptor Dataset() =>
            new("raster", Path, Crs, new Envelope(0, 0, 4, 3), 1, 1, "Ushort fixture");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    /// <summary>A 32-bit float raster with a single repeated value: no data range to scale.</summary>
    private sealed class FlatFixture : IDisposable
    {
        public FlatFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-raster-flat-{Guid.NewGuid():N}.tif");
            var pixels = new float[4 * 3];
            Array.Fill(pixels, 5f);
            var bytes = new byte[pixels.Length * 4];
            Buffer.BlockCopy(pixels, 0, bytes, 0, bytes.Length);
            using var image = Image.NewFromMemory(bytes, 4, 3, 1, Enums.BandFormat.Float);
            image.WriteToFile(Path);
        }

        public string Path { get; }

        public RasterDatasetDescriptor Dataset() =>
            new("raster", Path, Crs, new Envelope(0, 0, 4, 3), 1, 1, "Flat fixture");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    /// <summary>A complex raster (vips native format, which stores complex samples): histograms over complex values are meaningless.</summary>
    private sealed class ComplexFixture : IDisposable
    {
        public ComplexFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-raster-complex-{Guid.NewGuid():N}.v");
            var pixels = new byte[4 * 3];
            using var image = Image.NewFromMemory(pixels, 4, 3, 1, Enums.BandFormat.Uchar);
            using var cast = image.Cast(Enums.BandFormat.Complex);
            cast.WriteToFile(Path);
        }

        public string Path { get; }

        public RasterDatasetDescriptor Dataset() =>
            new("raster", Path, Crs, new Envelope(0, 0, 4, 3), 1, 1, "Complex fixture");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }

    /// <summary>A 32-bit float raster with values 0..11: histograms scale the data range.</summary>
    private sealed class FloatFixture : IDisposable
    {
        public FloatFixture()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"spatial-raster-float-{Guid.NewGuid():N}.tif");
            var pixels = new byte[4 * 3];
            for (var i = 0; i < pixels.Length; i++)
            {
                pixels[i] = (byte)i;
            }

            using var image = Image.NewFromMemory(pixels, 4, 3, 1, Enums.BandFormat.Uchar);
            using var cast = image.Cast(Enums.BandFormat.Float);
            cast.WriteToFile(Path);
        }

        public string Path { get; }

        public RasterDatasetDescriptor Dataset() =>
            new("raster", Path, Crs, new Envelope(0, 0, 4, 3), 1, 1, "Float fixture");

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
