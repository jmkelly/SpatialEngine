using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-040 cached-root honesty: the MapServer root advertises the served
/// surface per scheme — time relations and dynamic layers (now honoured by
/// export), the fused-cache flag with its <c>tileInfo</c> exactly when a
/// scheme is served, and <c>exportTilesAllowed:false</c> (offline packaging
/// is T-041's scope, never silently promised).
/// </summary>
public sealed class MapServerRootTests
{
    private static MapLayerInfo Layer() => new(
        new PublishedLayer(0, "demo.cities", "Cities"),
        new DatasetDescription("demo.cities", "demo", "cities", "geometry", 4326, "Point", 0, [], new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ])),
        new Envelope(0, 0, 10, 10));

    private sealed class FakeScheme : ITileScheme
    {
        public string Id => "fake";

        public string Crs => "EPSG:3857";

        public int TileSize => 256;

        public int MinZoom => 0;

        public int MaxZoom => 1;

        public IReadOnlyList<TileLevel> Levels =>
        [
            new(0, 156543.033928, 591657527.591555),
            new(1, 78271.516964, 295828763.795777),
        ];

        public bool IsValid(TileCoordinate coordinate) => true;

        public double Resolution(int zoom) => Levels[zoom].Resolution;

        public Envelope Bounds(TileCoordinate coordinate) => new(-10, -10, 10, 10);
    }

    [Fact]
    public void The_root_advertises_the_served_surface()
    {
        var root = MapServerResources.Root("world", [Layer()], new FakeScheme(), null, null);

        Assert.True(root.SupportsDynamicLayers);
        Assert.True(root.SupportsTimeRelation);
        Assert.True(root.SingleFusedMapCache);
        Assert.False(root.ExportTilesAllowed);
        Assert.NotNull(root.TileInfo);
        Assert.Equal(2, root.TileInfo.Lods.Count);
    }

    [Fact]
    public void The_root_without_a_scheme_is_not_a_fused_cache()
    {
        var root = MapServerResources.Root("world", [Layer()], null, null, null);

        Assert.False(root.SingleFusedMapCache);
        Assert.Null(root.TileInfo);
        Assert.False(root.ExportTilesAllowed);
    }
}
