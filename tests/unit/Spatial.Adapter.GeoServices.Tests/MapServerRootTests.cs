using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// T-040 cached-root honesty: the MapServer root advertises the served
/// surface per scheme — dynamic layers (now honoured by export), the
/// fused-cache flag with its <c>tileInfo</c> exactly when a
/// scheme is served, and <c>exportTilesAllowed:false</c> (offline packaging
/// is T-041's scope, never silently promised). <c>supportsTimeRelation</c> is
/// <c>false</c>: the engine applies the overlaps relation only and rejects the
/// others by name, so the flag states that rather than promising a distinction
/// the temporal model does not make (ADR-0100).
/// </summary>
public sealed class MapServerRootTests
{
    private static MapLayerInfo Layer(TemporalExtentFields? timeFields = null) => new(
        new PublishedLayer(0, "demo.cities", "Cities"),
        new DatasetDescription("demo.cities", "demo", "cities", "geometry", 4326, "Point", 0, [], new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("geometry", AttributeKind.Geometry),
        ]))
        {
            TimeFields = timeFields,
        },
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
        var root = MapServerResources.Root("world", [Layer()], new FakeScheme(), null, null, new ProjNetTransforms(), CancellationToken.None);

        Assert.True(root.SupportsDynamicLayers);
        Assert.False(root.SupportsTimeRelation);
        Assert.True(root.SingleFusedMapCache);
        Assert.False(root.ExportTilesAllowed);
        Assert.NotNull(root.TileInfo);
        Assert.Equal(2, root.TileInfo.Lods.Count);
    }

    /// <summary>
    /// The flag is advertised exactly when the behaviour proves it
    /// (ADR-0081, ADR-0175 §6): a layer that designates the dates bounding a
    /// feature can answer all three relations, so the root says so, and a
    /// service whose layers designate none keeps the honest reject.
    /// </summary>
    [Fact]
    public void The_root_advertises_the_time_relation_only_when_a_layer_can_serve_it()
    {
        var designated = MapServerResources.Root(
            "world", [Layer(new TemporalExtentFields("begins", "ends"))], null, null, null, new ProjNetTransforms(), CancellationToken.None);
        var undesignated = MapServerResources.Root("world", [Layer()], null, null, null, new ProjNetTransforms(), CancellationToken.None);
        var partly = MapServerResources.Root(
            "world",
            [Layer(), Layer(new TemporalExtentFields("begins", "ends"))],
            null, null, null, new ProjNetTransforms(), CancellationToken.None);

        Assert.True(designated.SupportsTimeRelation);
        Assert.False(undesignated.SupportsTimeRelation);
        Assert.True(partly.SupportsTimeRelation);
    }

    [Fact]
    public void The_root_without_a_scheme_is_not_a_fused_cache()
    {
        var root = MapServerResources.Root("world", [Layer()], null, null, null, new ProjNetTransforms(), CancellationToken.None);

        Assert.False(root.SingleFusedMapCache);
        Assert.Null(root.TileInfo);
        Assert.False(root.ExportTilesAllowed);
    }

    [Fact]
    public void The_tiled_root_advertises_the_tile_scheme_reference()
    {
        // QGIS derives tile indices from the root's spatial reference and
        // full extent: a fused-cache root advertising the data CRS (4326)
        // while its tileInfo is 3857 makes QGIS fetch Null-Island tiles for
        // an Australia canvas — every request 200, every tile blank ocean.
        var root = MapServerResources.Root(
            "world", [Layer()], new FakeScheme(), null, null,
            new ProjNetTransforms(), CancellationToken.None);

        Assert.Equal(3857, root.SpatialReference?.Wkid);
        Assert.Equal("esriMeters", root.Units);
        Assert.Equal(3857, root.FullExtent?.SpatialReference?.Wkid);
        Assert.Equal(3857, root.InitialExtent?.SpatialReference?.Wkid);
        // 0..10 degrees reprojected to metres, not echoed back as degrees.
        Assert.True(root.FullExtent!.Xmax > 1_000_000);
    }

    [Fact]
    public void The_untiled_root_keeps_the_data_reference()
    {
        var root = MapServerResources.Root(
            "world", [Layer()], null, null, null,
            new ProjNetTransforms(), CancellationToken.None);

        Assert.Equal(4326, root.SpatialReference?.Wkid);
        Assert.Equal("esriDecimalDegrees", root.Units);
        Assert.Equal(10, root.FullExtent!.Xmax);
    }
}
