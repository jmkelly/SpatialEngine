using Spatial.Contracts;
using Spatial.Core.Geometry;
using Spatial.Rendering.Skia.Pipeline;

namespace Spatial.Rendering.Skia.Tests;

public sealed class GeometryPipelineTests
{
    [Fact]
    public void TransformEnvelope_ReturnsBoundsWhenCrsAgree()
    {
        var bounds = new Envelope(-10, -10, 10, 10);
        var result = GeometryPipeline.TransformEnvelope(
            new OffsetTransforms(5, 5), bounds, "EPSG:4326", "EPSG:4326", CancellationToken.None);
        Assert.Equal(bounds, result);
    }

    [Fact]
    public void TransformEnvelope_ReturnsEmptyForEmptyBounds()
    {
        var result = GeometryPipeline.TransformEnvelope(
            new OffsetTransforms(5, 5), Envelope.Empty, "EPSG:4326", "EPSG:3857", CancellationToken.None);
        Assert.True(result.IsEmpty);
    }

    [Fact]
    public void TransformEnvelope_TransformsTheCornersFromTheViewportIntoTheDataset()
    {
        var transforms = new OffsetTransforms(5, -5);

        var result = GeometryPipeline.TransformEnvelope(
            transforms, new Envelope(-10, -10, 10, 10), "EPSG:3857", "EPSG:4326", CancellationToken.None);

        Assert.Equal(new Envelope(-5, -15, 15, 5), result);
        Assert.Equal("EPSG:3857", transforms.LastSource);
        Assert.Equal("EPSG:4326", transforms.LastTarget);
    }

    [Fact]
    public void Project_SkipsTransformWhenCrsAgree()
    {
        var point = GeometryFactory.CreatePoint(1, 2);
        var result = GeometryPipeline.Project(point, 4326, "EPSG:4326", new OffsetTransforms(100, 100), CancellationToken.None);
        Assert.Same(point, result);
    }

    [Fact]
    public void Project_TransformsWhenCrsDiffer()
    {
        var transforms = new OffsetTransforms(1, 1);
        var result = GeometryPipeline.Project(GeometryFactory.CreatePoint(0, 0), 4326, "EPSG:3857", transforms, CancellationToken.None);
        Assert.Equal("EPSG:4326", transforms.LastSource);
        Assert.Equal("EPSG:3857", transforms.LastTarget);
        Assert.Equal(new Coordinate(1, 1), ((IPoint)result).Coordinate);
    }

    [Fact]
    public void Place_SkipsClipAndTransformWhenCrsAgree()
    {
        var point = GeometryFactory.CreatePoint(1, 2);
        var operations = new FakeOperations();
        var features = new LayerFeatures(4326, "geometry", [], new Envelope(-180, -90, 180, 90));
        var viewport = new RasterViewport(new Envelope(-10, -10, 10, 10), 100, 100, "EPSG:4326");

        var result = GeometryPipeline.Place(point, features, viewport, new OffsetTransforms(100, 100), operations, CancellationToken.None);

        Assert.Same(point, result);
        Assert.Equal(0, operations.IntersectionCalls);
    }

    [Fact]
    public void Place_DoesNotClipGeometryInsideTheSourceBounds()
    {
        var operations = new FakeOperations();
        var features = new LayerFeatures(4326, "geometry", [], new Envelope(-180, -85.0511, 180, 85.0511));
        var viewport = new RasterViewport(new Envelope(-20_037_508, -20_037_508, 20_037_508, 20_037_508), 256, 256, "EPSG:3857");

        _ = GeometryPipeline.Place(GeometryFactory.CreatePoint(0, 52), features, viewport, new IdentityTransforms(), operations, CancellationToken.None);

        Assert.Equal(0, operations.IntersectionCalls);
    }

    [Fact]
    public void Place_ClipsGeometryThatReachesBeyondTheSourceBoundsBeforeTransforming()
    {
        var operations = new FakeOperations();
        var features = new LayerFeatures(4326, "geometry", [], new Envelope(-180, -85.0511, 180, 85.0511));
        var viewport = new RasterViewport(new Envelope(-20_037_508, -20_037_508, 20_037_508, 20_037_508), 256, 256, "EPSG:3857");

        var result = GeometryPipeline.Place(PolarBand(), features, viewport, new IdentityTransforms(), operations, CancellationToken.None);

        Assert.Equal(1, operations.IntersectionCalls);
        Assert.NotNull(result);
    }

    [Fact]
    public void ClipToBounds_LeavesGeometryInsideTheBounds()
    {
        var operations = new FakeOperations();

        var result = GeometryPipeline.ClipToBounds(
            GeometryFactory.CreatePoint(0, 0), new Envelope(-10, -10, 10, 10), operations, CancellationToken.None);

        Assert.Equal(0, operations.IntersectionCalls);
        Assert.NotNull(result);
    }

    [Fact]
    public void ClipToBounds_ReturnsGeometryWithoutEnvelope()
    {
        var empty = GeometryFactory.CreateEmptyPoint();
        var operations = new FakeOperations();

        var result = GeometryPipeline.ClipToBounds(empty, new Envelope(-10, -10, 10, 10), operations, CancellationToken.None);

        Assert.Same(empty, result);
        Assert.Equal(0, operations.IntersectionCalls);
    }

    [Fact]
    public void ClipToBounds_ClipsGeometryThatExceedsTheBounds()
    {
        var operations = new FakeOperations();

        var result = GeometryPipeline.ClipToBounds(PolarBand(), new Envelope(-180, -85.0511, 180, 85.0511), operations, CancellationToken.None);

        Assert.Equal(1, operations.IntersectionCalls);
        Assert.NotNull(result);
    }

    private static Polygon PolarBand() => GeometryFactory.CreatePolygon(
    [
        new Coordinate(-180, -90),
        new Coordinate(180, -90),
        new Coordinate(180, -60),
        new Coordinate(-180, -60),
        new Coordinate(-180, -90),
    ]);

    [Fact]
    public void SimplifyAndCull_DropsGeometryOutsideTheViewport()
    {
        var line = GeometryFactory.CreateLineString(
        [
            new Coordinate(100, 100),
            new Coordinate(101, 101),
            new Coordinate(102, 102),
        ]);
        var result = GeometryPipeline.SimplifyAndCull(
            line, 1, new Envelope(0, 0, 10, 10), new FakeOperations(), CancellationToken.None);
        Assert.Null(result);
    }

    [Fact]
    public void SimplifyAndCull_SimplifiesGeometryInsideTheViewport()
    {
        var line = GeometryFactory.CreateLineString(
        [
            new Coordinate(1, 1),
            new Coordinate(5, 5),
            new Coordinate(9, 9),
        ]);
        var operations = new FakeOperations();
        var result = GeometryPipeline.SimplifyAndCull(
            line, 1, new Envelope(0, 0, 10, 10), operations, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(1, operations.SimplifyCalls);
    }

    [Fact]
    public void SimplifyAndCull_SkipsSimplifyForSmallGeometries()
    {
        var operations = new FakeOperations();
        var result = GeometryPipeline.SimplifyAndCull(
            GeometryFactory.CreatePoint(5, 5), 1, new Envelope(0, 0, 10, 10), operations, CancellationToken.None);
        Assert.NotNull(result);
        Assert.Equal(0, operations.SimplifyCalls);
    }

    [Fact]
    public void SimplifyAndCull_SkipsSimplifyWhenToleranceIsZero()
    {
        var operations = new FakeOperations();
        var line = GeometryFactory.CreateLineString(
        [
            new Coordinate(1, 1),
            new Coordinate(5, 5),
            new Coordinate(9, 9),
        ]);
        _ = GeometryPipeline.SimplifyAndCull(line, 0, new Envelope(0, 0, 10, 10), operations, CancellationToken.None);
        Assert.Equal(0, operations.SimplifyCalls);
    }

    [Fact]
    public void SimplifyAndCull_DropsGeometryWithoutEnvelope()
    {
        var operations = new FakeOperations();
        var result = GeometryPipeline.SimplifyAndCull(
            GeometryFactory.CreateEmptyPoint(), 1, new Envelope(0, 0, 10, 10), operations, CancellationToken.None);
        Assert.Null(result);
    }
}
