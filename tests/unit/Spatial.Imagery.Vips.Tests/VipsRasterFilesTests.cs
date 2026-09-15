using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Imagery.Vips.Raster;

namespace Spatial.Imagery.Vips.Tests;

/// <summary>
/// The opaque file ids of <see cref="VipsRasterFiles"/> (ADR-0051): unknown
/// ids, name mismatches, missing files and catalog misuse all map to typed
/// errors, never to an arbitrary path.
/// </summary>
public sealed class VipsRasterFilesTests
{
    [Fact]
    public void List_names_the_single_file_of_a_plain_dataset()
    {
        using var fixture = new RasterFixture();

        var files = VipsRasterFiles.List(fixture.Dataset(), null);

        var file = Assert.Single(files);
        Assert.Equal("dataset~" + Path.GetFileName(fixture.Path), file.Id);
        Assert.Equal("image/tiff", file.MediaType);
    }

    [Fact]
    public void List_with_an_item_id_on_a_plain_dataset_is_not_found()
    {
        using var fixture = new RasterFixture();

        var error = Assert.Throws<SpatialException>(() => VipsRasterFiles.List(fixture.Dataset(), 7));

        Assert.Equal(SpatialException.NotFound, error.Code);
    }

    [Fact]
    public void List_with_an_unknown_catalog_item_is_not_found()
    {
        using var fixture = new RasterFixture();

        var error = Assert.Throws<SpatialException>(() => VipsRasterFiles.List(fixture.CatalogDataset(), 42));

        Assert.Equal(SpatialException.NotFound, error.Code);
    }

    [Fact]
    public void List_with_a_pathless_catalog_item_is_not_found()
    {
        using var fixture = new RasterFixture();
        var footprint = GeometryFactory.CreatePolygon(
            [
                new Coordinate(0, 0),
                new Coordinate(1, 0),
                new Coordinate(1, 1),
                new Coordinate(0, 1),
                new Coordinate(0, 0),
            ],
            CoordinateReference.Epsg(4326));
        var dataset = fixture.CatalogDataset() with
        {
            Items = [new RasterCatalogItemDescriptor(7, footprint, " ", new Envelope(0, 0, 1, 1), [AttributeValue.FromString("first")])],
        };

        var error = Assert.Throws<SpatialException>(() => VipsRasterFiles.List(dataset, 7));

        Assert.Equal(SpatialException.NotFound, error.Code);
    }

    [Fact]
    public void Read_rejects_a_malformed_file_id()
    {
        using var fixture = new RasterFixture();

        var error = Assert.Throws<SpatialException>(() => VipsRasterFiles.Read(fixture.Dataset(), "no-separator"));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
    }

    [Fact]
    public void Read_rejects_a_name_that_does_not_match_the_item()
    {
        using var fixture = new RasterFixture();
        var dataset = fixture.CatalogDataset();

        var error = Assert.Throws<SpatialException>(() => VipsRasterFiles.Read(dataset, "7~other.tif"));

        Assert.Equal(SpatialException.NotFound, error.Code);
    }

    [Fact]
    public void Read_of_a_missing_file_is_not_found()
    {
        using var fixture = new RasterFixture();
        var dataset = fixture.Dataset() with { Path = Path.Combine(Path.GetTempPath(), $"spatial-missing-{Guid.NewGuid():N}.tif") };
        var missingName = Path.GetFileName(dataset.Path);

        var error = Assert.Throws<SpatialException>(() => VipsRasterFiles.Read(dataset, "dataset~" + missingName));

        Assert.Equal(SpatialException.NotFound, error.Code);
    }

    [Fact]
    public void Read_of_a_catalog_without_naming_an_item_fails()
    {
        using var fixture = new RasterFixture();
        var name = Path.GetFileName(fixture.Path);

        var error = Assert.Throws<SpatialException>(() => VipsRasterFiles.Read(fixture.CatalogDataset(), "dataset~" + name));

        Assert.Equal(SpatialException.InvalidArguments, error.Code);
    }

    [Fact]
    public void Read_returns_the_bytes_of_a_known_file()
    {
        using var fixture = new RasterFixture();
        var name = Path.GetFileName(fixture.Path);

        var content = VipsRasterFiles.Read(fixture.Dataset(), "dataset~" + name);

        Assert.Equal(name, content.Name);
        Assert.Equal("image/tiff", content.MediaType);
        Assert.Equal(new FileInfo(fixture.Path).Length, content.Size);
    }
}
