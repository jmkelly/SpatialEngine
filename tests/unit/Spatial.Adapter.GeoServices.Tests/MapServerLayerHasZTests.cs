using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The <c>hasZ</c>/<c>hasM</c> a MapServer layer record advertises. The map
/// surface projects the same <see cref="DatasetDescription"/> the Feature
/// Server layer resource describes, so a dataset that is 3D describes itself as
/// 2D when only the Feature Server carries the keys — a real MapServer layer
/// does not: a 3D upstream layer carries <c>hasZ: true</c> and a 2D one omits
/// both keys. The rule is ADR-0084's, projected onto the map record: advertise
/// an ordinate only when the store proves the geometry column declares it, and
/// omit it otherwise rather than reporting <c>false</c>.
/// </summary>
public sealed class MapServerLayerHasZTests
{
    private static MapLayerInfo Layer(CoordinateLayout layout) => new(
        new PublishedLayer(0, "demo.survey", "Survey"),
        new DatasetDescription(
            "demo.survey", "demo", "survey", "geometry", 4326, "Point", 0, ["id"], new FeatureSchema(
            [
                new FieldDefinition("id", AttributeKind.Int64),
                new FieldDefinition("geometry", AttributeKind.Geometry),
            ]), layout),
        new Envelope(0, 0, 10, 10));

    [Fact]
    public void A_declared_z_dataset_advertises_hasZ()
    {
        var layer = MapServerResources.Layer(Layer(CoordinateLayout.Xyz));

        Assert.True(layer.HasZ);
        Assert.Null(layer.HasM);
    }

    [Fact]
    public void A_declared_zm_dataset_advertises_both_ordinates()
    {
        var layer = MapServerResources.Layer(Layer(CoordinateLayout.Xyzm));

        Assert.True(layer.HasZ);
        Assert.True(layer.HasM);
    }

    [Fact]
    public void A_declared_m_dataset_advertises_hasM_only()
    {
        var layer = MapServerResources.Layer(Layer(CoordinateLayout.Xym));

        Assert.Null(layer.HasZ);
        Assert.True(layer.HasM);
    }

    [Fact]
    public void A_two_dimensional_dataset_advertises_no_ordinates()
    {
        var layer = MapServerResources.Layer(Layer(CoordinateLayout.Xy));

        Assert.Null(layer.HasZ);
        Assert.Null(layer.HasM);
    }
}
