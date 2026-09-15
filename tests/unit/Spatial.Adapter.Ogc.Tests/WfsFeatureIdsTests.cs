using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// T-084: WFS GetFeature ids must be unique across typenames. The memory
/// ingest numbers features per dataset (cap.lines 1,2 + cap.polys 1,2), so a
/// multi-typename collection carried duplicate ids and OpenLayers (same-id
/// features are not added to the source) kept 10 of 12. Ids are scoped per
/// typeName as <c>&lt;typeName&gt;.&lt;id&gt;</c>, so every feature in the
/// collection is addressable. Red first: ids collide before the fix.
/// </summary>
public sealed class WfsFeatureIdsTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static Feature Numbered(string id, string name, double x, double y) =>
        new(
            new FeatureId(id),
            Schema,
            [
                AttributeValue.FromString(name),
                AttributeValue.FromGeometry(GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326))),
            ]);

    private static DatasetDescription Describe(string dataset) =>
        new(dataset, "cap", dataset.Split('.')[1], "geometry", 4326, "Point", 2, [], Schema);

    private static (Map Map, OgcRequestServices Services) Seed()
    {
        var map = new Map(
            "mixed",
            "demo",
            [
                new MapLayer("cap.lines", 0, "Routes"),
                new MapLayer("cap.polys", 1, "Zones"),
            ],
            [MapServiceKind.Wfs]);
        var store = new MultiStore();
        store.Seed("cap.lines", Numbered("1", "Route A", 0, 51), Numbered("2", "Route B", -5, 48));
        store.Seed("cap.polys", Numbered("1", "Zone A", 5, 52), Numbered("2", "Zone B", -2, 47));
        var services = new OgcRequestServices(
            new MultiRegistry(store), new OgcFixtures.FakeRegistry(map),
            new OgcFixtures.FakeRenderer(), new OgcFixtures.IdentityTransforms());
        return (map, services);
    }

    private static async Task<JsonElement> GetFeatureAsync(Map map, OgcRequestServices services, string query)
    {
        var request = new DefaultHttpContext();
        request.Request.Method = "GET";
        request.Request.Scheme = "http";
        request.Request.Host = new HostString("localhost");
        request.Request.Path = new PathString($"/ogc/{map.Name}/wfs");
        request.Request.QueryString = new QueryString(query);
        var parameters = await OgcParameters.ReadAsync(request, CancellationToken.None);
        var response = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        };
        response.Response.Body = new MemoryStream();
        var result = await WfsService.HandleAsync(
            map.Name, parameters, services, new OgcOptions(), request, CancellationToken.None);
        await result.ExecuteAsync(response);
        response.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await new StreamReader(response.Response.Body).ReadToEndAsync(CancellationToken.None);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    [Fact]
    public async Task GetFeature_ids_are_unique_across_typenames()
    {
        var (map, services) = Seed();

        var root = await GetFeatureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Routes,Zones");
        var ids = root.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString()!).ToArray();

        Assert.Equal(4, root.GetProperty("numberMatched").GetInt32());
        Assert.Equal(4, ids.Length);
        Assert.Equal(4, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(["Routes.1", "Routes.2", "Zones.1", "Zones.2"], ids);
    }

    [Fact]
    public async Task GetFeature_single_typename_ids_carry_the_typename()
    {
        var (map, services) = Seed();

        var root = await GetFeatureAsync(
            map, services, "?service=WFS&request=GetFeature&typeNames=Routes");
        var ids = root.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("id").GetString()!).ToArray();

        Assert.Equal(["Routes.1", "Routes.2"], ids);
    }

    [Fact]
    public async Task GetFeature_unaddressable_featureid_stays_an_honest_reject()
    {
        var (map, services) = Seed();
        var request = new DefaultHttpContext();
        request.Request.QueryString = new QueryString("?service=WFS&request=GetFeature&typeNames=Routes&featureId=Routes.1");
        var parameters = await OgcParameters.ReadAsync(request, CancellationToken.None);
        var context = new DefaultHttpContext();

        var failure = await Assert.ThrowsAsync<OgcServiceException>(() =>
            WfsService.HandleAsync(map.Name, parameters, services, new OgcOptions(), context, CancellationToken.None));

        Assert.Equal("InvalidParameterValue", failure.Code);
        Assert.Contains("featureid", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class MultiStore : IFeatureStore, IDataCatalogue
    {
        private readonly Dictionary<string, List<Feature>> _features = new(StringComparer.OrdinalIgnoreCase);

        public MultiStore Seed(string dataset, params Feature[] features)
        {
            if (!_features.TryGetValue(dataset, out var list))
            {
                list = [];
                _features[dataset] = list;
            }

            list.AddRange(features);
            return this;
        }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, FeaturesOf(dataset))]);

        public Task<IReadOnlyList<FeatureBatch>> QueryAsync(
            string dataset, BoundingBox? bbox = null, string? filter = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, FeaturesOf(dataset))]);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatasetSummary>>(
                _features.Keys.Select(key => new DatasetSummary(key, "cap", key.Split('.')[1], "geometry", 4326, _features[key].Count)).ToArray());

        public Task<DatasetDescription> DescribeAsync(string dataset, CancellationToken cancellationToken = default) =>
            Task.FromResult(Describe(dataset));

        public Task<string> CreateAsync(string dataset, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private Feature[] FeaturesOf(string dataset) =>
            _features.TryGetValue(dataset, out var list) ? list.ToArray() : [];
    }

    private sealed class MultiRegistry(MultiStore store) : IStoreRegistry
    {
        public IDataCatalogue Catalogue(string name) => store;

        public IFeatureStore Features(string name) => store;

        public IFeatureEditStore? EditStore(string name) => null;

        public ITransactionStore? Transactions(string name) => null;

        public IDatasetIngest? Ingest(string name) => null;

        public IRasterCatalogue? RasterCatalogue(string name) => null;

        public IFeatureAttachmentStore? AttachmentStore(string name) => null;
    }
}
