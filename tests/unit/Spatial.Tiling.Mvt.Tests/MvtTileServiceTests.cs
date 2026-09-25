using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Tiling.Mvt;

namespace Spatial.Tiling.Mvt.Tests;

public sealed class MvtTileServiceTests
{
    [Fact]
    public async Task RenderAsync_encodes_a_point_layer_and_attributes()
    {
        var schema = new FeatureSchema([
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var feature = new Feature(new FeatureId("7"), schema, [
            AttributeValue.FromString("point"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(2, 3)),
        ]);
        var service = new MvtTileService(new NoopTransforms());
        var tile = await service.RenderAsync(new VectorTileRequest(
            new Envelope(0, 0, 4, 4), "EPSG:3857",
            [new VectorTileLayer("places", "demo.points", new Store(feature), new Catalogue(schema))]));

        Assert.NotEmpty(tile.Content);
        Assert.Equal("application/vnd.mapbox-vector-tile", tile.MediaType);
    }

    [Fact]
    public async Task RenderAsync_rejects_invalid_bounds_with_structured_code()
    {
        var service = new MvtTileService(new NoopTransforms());
        var error = await Assert.ThrowsAsync<SpatialException>(() => service.RenderAsync(
            new VectorTileRequest(Envelope.Empty, "EPSG:3857", [])));
        Assert.Equal(SpatialException.InvalidArguments, error.Code);
    }

    [Fact]
    public async Task RenderAsync_honours_cancellation()
    {
        var service = new MvtTileService(new NoopTransforms());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RenderAsync(
            new VectorTileRequest(new Envelope(0, 0, 1, 1), "EPSG:3857", []), new CancellationToken(true)));
    }

    private sealed class Store(Feature feature) : IFeatureStore
    {
        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(feature.Schema, [feature])]);
    }

    private sealed class Catalogue(FeatureSchema schema) : IDataCatalogue
    {
        public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DatasetDescription(dataset, "public", "points", "geometry", 3857, "Point", 0, [], schema));
        public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopTransforms : ICoordinateTransforms
    {
        public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default) => geometry;
    }
}
