using System.Globalization;
using System.Text;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
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
    public async Task RenderAsync_encodes_every_attribute_kind_into_the_value_table()
    {
        var stamp = new DateTimeOffset(2026, 9, 26, 6, 30, 0, TimeSpan.Zero);
        var id = Guid.Parse("2f9c1a44-0d1e-4f4a-9c2b-6f2f5d0b7a11");
        var schema = new FeatureSchema([
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("population", AttributeKind.Double),
            new FieldDefinition("wards", AttributeKind.Int64),
            new FieldDefinition("coastal", AttributeKind.Boolean),
            new FieldDefinition("founded", AttributeKind.DateTimeOffset),
            new FieldDefinition("code", AttributeKind.Guid),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]);
        var feature = new Feature(new FeatureId("7"), schema, [
            AttributeValue.FromString("Kinshasa"),
            AttributeValue.FromDouble(17_000_000.5),
            AttributeValue.FromInt64(24),
            AttributeValue.FromBoolean(true),
            AttributeValue.FromDateTimeOffset(stamp),
            AttributeValue.FromGuid(id),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(2, 3)),
        ]);
        var service = new MvtTileService(new NoopTransforms());

        var tile = await service.RenderAsync(new VectorTileRequest(
            new Envelope(0, 0, 4, 4), "EPSG:3857",
            [new VectorTileLayer("cities", "demo.cities", new Store(feature), new Catalogue(schema))]));

        Assert.Contains("Kinshasa", Encoding.UTF8.GetString(tile.Content), StringComparison.Ordinal);
        Assert.Contains(id.ToString(), Encoding.UTF8.GetString(tile.Content), StringComparison.Ordinal);
        Assert.Contains(stamp.ToString("O", CultureInfo.InvariantCulture), Encoding.UTF8.GetString(tile.Content), StringComparison.Ordinal);
        Assert.NotEmpty(tile.Content);
    }

    [Fact]
    public async Task RenderAsync_encodes_line_and_polygon_geometry()
    {
        var schema = new FeatureSchema([new FieldDefinition("geometry", AttributeKind.Geometry)]);
        IGeometry[] geometries =
        [
            GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(1, 1), new Coordinate(2, 0)]),
            GeometryFactory.CreatePolygon(
                GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(3, 0), new Coordinate(3, 3), new Coordinate(0, 0)]),
                [GeometryFactory.CreateLineString([new Coordinate(1, 1), new Coordinate(2, 1), new Coordinate(2, 2), new Coordinate(1, 1)])]),
            GeometryFactory.CreateMultiPoint(GeometryFactory.CreatePoint(0, 0), GeometryFactory.CreatePoint(2, 2)),
        ];
        var store = new Store([.. geometries.Select((geometry, index) =>
            new Feature(new FeatureId(index.ToString(CultureInfo.InvariantCulture)), schema,
                [AttributeValue.FromGeometry(geometry)]))]);

        var service = new MvtTileService(new NoopTransforms());
        var tile = await service.RenderAsync(new VectorTileRequest(
            new Envelope(0, 0, 4, 4), "EPSG:3857",
            [new VectorTileLayer("shapes", "demo.shapes", store, new Catalogue(schema))]));

        Assert.NotEmpty(tile.Content);
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

    private sealed class Store : IFeatureStore
    {
        private readonly IReadOnlyList<Feature> _features;

        public Store(Feature feature) => _features = [feature];

        public Store(IReadOnlyList<Feature> features) => _features = features;

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FeatureQueryPage> QueryAsync(
            string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FeatureQueryPage(
                [.. _features.Select(feature => new FeatureBatch(feature.Schema, [feature]))]));
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
