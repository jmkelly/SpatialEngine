using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// Shared fixtures for the OGC unit tests: an in-memory feature store and
/// catalogue, an identity transform service, a one-map registry and the
/// resolver built over them. No host and no real store is involved, so the
/// XML/GeoJSON shaping is exercised in isolation (ADR-0053 §3).
/// </summary>
internal static class OgcFixtures
{
    public const string MapName = "world";
    public const string Store = "demo";
    public const string Dataset = "demo.cities";

    public static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    public static Feature City(string name, long population, double x, double y) =>
        new(
            new FeatureId(name.ToLowerInvariant()),
            Schema,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromInt64(population),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    public static DatasetDescription Description(string dataset = Dataset) =>
        new(dataset, "demo", "cities", "geometry", 4326, "Point", 2, ["name"], Schema);

    public static Map Map(params MapService[] services) =>
        new(MapName, Store, [new MapLayer(Dataset, 0, "Cities")], services.Length == 0 ? [MapService.Wms] : services);

    public static (OgcRequestServices Services, FakeStore Store) Build(Map map)
    {
        var store = new FakeStore(map.Layers[0].Dataset);
        return (new OgcRequestServices(new FakeStoreRegistry(store), new FakeRegistry(map), new FakeRenderer(), new IdentityTransforms()), store);
    }

    /// <summary>One keyed store exposed through the typed registry seam (ADR-0033).</summary>
    internal sealed class FakeStoreRegistry(FakeStore store) : IStoreRegistry
    {
        public IDataCatalogue Catalogue(string name) => store;

        public IFeatureStore Features(string name) => store;

        public IFeatureEditStore? EditStore(string name) => null;

        public IFeatureAttachmentStore? AttachmentStore(string name) => null;

        public ITransactionStore? Transactions(string name) => null;

        public IDatasetIngest? Ingest(string name) => null;

        public IRasterCatalogue? RasterCatalogue(string name) => null;
    }

    internal sealed class FakeStore(string dataset) : IFeatureStore, IDataCatalogue
    {
        private readonly List<Feature> _features = [];

        public FakeStore Seed(params Feature[] features)
        {
            _features.AddRange(features);
            return this;
        }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Page());

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
            string id, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default)
        {
            var matched = bbox is null
                ? _features
                : _features.Where(feature => Matches(feature, bbox)).ToList();
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, matched.ToArray())]);
        }

        public Task<int> WriteAsync(string id, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatasetSummary>>([new DatasetSummary(dataset, "demo", "cities", "geometry", 4326, _features.Count)]);

        public Task<DatasetDescription> DescribeAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Description(id));

        public Task<string> CreateAsync(string id, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private IReadOnlyList<FeatureBatch> Page() => [new FeatureBatch(Schema, _features.ToArray())];

        private static bool Matches(Feature feature, BoundingBox bbox)
        {
            var geometry = feature["geometry"].GeometryValue;
            return geometry.Envelope is { } envelope
                && envelope.MinX <= bbox.MaxX && envelope.MaxX >= bbox.MinX
                && envelope.MinY <= bbox.MaxY && envelope.MaxY >= bbox.MinY;
        }
    }

    internal sealed class FakeRegistry(Map map) : IMapRegistry
    {
        public Task<IReadOnlyList<Map>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Map>>([map]);

        public Task<Map> GetAsync(string name, CancellationToken cancellationToken = default) =>
            string.Equals(name, map.Name, StringComparison.OrdinalIgnoreCase)
                ? Task.FromResult(map)
                : throw SpatialException.Missing($"Map '{name}' does not exist.");

        public Task<Map> PutAsync(Map value, CancellationToken cancellationToken = default) => Task.FromResult(value);

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) => Task.FromResult(false);
    }

    internal sealed class FakeRenderer : IMapRenderer
    {
        public Task<RasterImage> RenderAsync(MapRenderRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new RasterImage([1, 2, 3], "image/png", request.Viewport.Width, request.Viewport.Height, RasterFormat.Png));
    }

    internal sealed class IdentityTransforms : ICoordinateTransforms
    {
        public IGeometry Transform(IGeometry geometry, string? source, string target, CancellationToken cancellationToken = default) =>
            geometry;
    }
}
