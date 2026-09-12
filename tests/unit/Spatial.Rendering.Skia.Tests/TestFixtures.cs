using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Rendering.Skia.Tests;

/// <summary>An identity transform, so placement is a no-op when CRS agree.</summary>
internal sealed class IdentityTransforms : ICoordinateTransforms
{
    public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default) =>
        geometry;
}

/// <summary>A transform that translates points and line/ring coordinates by a fixed offset.</summary>
internal sealed class OffsetTransforms(double dx, double dy) : ICoordinateTransforms
{
    public string? LastSource { get; private set; }

    public string? LastTarget { get; private set; }

    public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default)
    {
        LastSource = source;
        LastTarget = target;
        if (geometry is IPoint point && point.Coordinate is { } coordinate)
        {
            return GeometryFactory.CreatePoint(coordinate.X + dx, coordinate.Y + dy);
        }

        Coordinate[] coordinates = [.. geometry.Coordinates().Select(c => new Coordinate(c.X + dx, c.Y + dy))];
        return GeometryFactory.CreateLineString(coordinates);
    }
}

/// <summary>A geometry service that simplifies to no-op and records calls.</summary>
internal sealed class FakeOperations : IGeometryOperations
{
    public int SimplifyCalls { get; private set; }

    public IGeometry Buffer(IGeometry geometry, double distance, int quadrantSegments = 8, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IGeometry Intersection(IGeometry left, IGeometry right, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public bool Validate(IGeometry geometry, CancellationToken cancellationToken = default) => true;

    public IGeometry Simplify(IGeometry geometry, double tolerance, CancellationToken cancellationToken = default)
    {
        SimplifyCalls++;
        return geometry;
    }
}

/// <summary>An imagery service that records the composite request.</summary>
internal sealed class FakeRasterOperations : IRasterOperations
{
    public RasterCompositeRequest? LastComposite { get; private set; }

    public Task<RasterImage> ReadAsync(RasterReadRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new RasterImage([1, 2, 3], "image/png", request.Viewport.Width, request.Viewport.Height, request.Format));

    public Task<RasterImage> CompositeAsync(RasterCompositeRequest request, CancellationToken cancellationToken = default)
    {
        LastComposite = request;
        return Task.FromResult(new RasterImage([9, 8, 7], "image/png", request.Viewport.Width, request.Viewport.Height, request.Format));
    }
}

/// <summary>An in-memory feature store that records the pushed-down query.</summary>
internal sealed class FakeStore(FeatureSchema schema, params Feature[] features) : IFeatureStore
{
    public BoundingBox? LastBbox { get; private set; }

    public string? LastFilter { get; private set; }

    public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
        string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LastBbox = bbox;
        LastFilter = filter;
        IReadOnlyList<FeatureBatch> batches = [new FeatureBatch(schema, features)];
        return Task.FromResult(batches);
    }

    public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>An in-memory catalogue describing one dataset.</summary>
internal sealed class FakeCatalogue(int srid, string geometryColumn = "geometry") : IDataCatalogue
{
    public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition(geometryColumn, AttributeKind.Geometry),
        ]);
        return Task.FromResult(new DatasetDescription(
            dataset, "public", dataset, geometryColumn, srid, "Point", 1, ["id"], schema));
    }

    public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

/// <summary>Builders for the small feature fixtures the renderer tests need.</summary>
internal static class TestFeatures
{
    public static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    public static Feature Point(string name, double x, double y, long population = 0, int srid = 4326) => new(
        new FeatureId(name),
        Schema,
        [
            AttributeValue.FromString(name),
            AttributeValue.FromInt64(population),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(srid))),
        ]);
}
