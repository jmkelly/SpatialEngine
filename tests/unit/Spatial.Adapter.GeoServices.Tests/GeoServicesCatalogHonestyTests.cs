using Spatial.Contracts.Providers;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-049 catalog honesty: the services listing only advertises served types.
/// A map may expose Tiles/WMS/WFS (or nothing), but the Esri catalog must
/// never advertise them — and a future GPServer-shaped map must fail this
/// test instead of leaking into the listing. The G1 ground-truth replay
/// (sampleserver6 lists GPServer entries) lives in
/// Spatial.Host.Tests.GeoServicesCatalogHonestyTests.
/// </summary>
public sealed class GeoServicesCatalogHonestyTests
{
    private sealed class FakeRegistry(IReadOnlyList<Map> maps) : IMapRegistry
    {
        public Task<IReadOnlyList<Map>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(maps);

        public Task<Map> GetAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Map> PutAsync(Map map, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private static Map See(string name, params MapServiceKind[] services) =>
        new(name, "demo", [], services);

    [Fact]
    public async Task Only_served_types_are_advertised()
    {
        var catalog = new GeoServicesCatalog(new GeoServicesOptions());
        var registry = new FakeRegistry(
        [
            See("features", MapServiceKind.FeatureServer),
            See("rendered", MapServiceKind.MapServer),
            See("imagery", MapServiceKind.ImageServer),
            See("tiles-only", MapServiceKind.Tiles),
            See("ogc-only", MapServiceKind.Wms, MapServiceKind.Wfs),
            See("draft"),
            See("multi", MapServiceKind.FeatureServer, MapServiceKind.MapServer),
        ]);

        var services = await GeoServicesEndpoints.BuildServicesAsync(catalog, registry, default);

        Assert.Equal("GeometryServer", services[0].Type);
        var byType = services.GroupBy(service => service.Type).ToDictionary(group => group.Key, group => group.Select(service => service.Name).ToArray());
        Assert.Equal(["features", "multi"], byType["FeatureServer"]);
        Assert.Equal(["rendered", "multi"], byType["MapServer"]);
        Assert.Equal(["imagery"], byType["ImageServer"]);
        Assert.DoesNotContain("tiles-only", services.Select(service => service.Name));
        Assert.DoesNotContain("ogc-only", services.Select(service => service.Name));
        Assert.DoesNotContain("draft", services.Select(service => service.Name));
    }

    [Theory]
    [InlineData(MapServiceKind.Tiles)]
    [InlineData(MapServiceKind.Wms)]
    [InlineData(MapServiceKind.Wfs)]
    public async Task A_non_esri_service_never_leaks_into_the_listing(MapServiceKind service)
    {
        var catalog = new GeoServicesCatalog(new GeoServicesOptions());
        var registry = new FakeRegistry([See("other", service)]);

        var services = await GeoServicesEndpoints.BuildServicesAsync(catalog, registry, default);

        Assert.DoesNotContain("other", services.Select(entry => entry.Name));
    }
}
