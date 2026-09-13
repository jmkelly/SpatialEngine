using System.Xml.Linq;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// WFS 2.0.0 capabilities shaping (ADR-0052 §3): service identification, the
/// operations metadata and one feature type per feature layer with its CRS,
/// GeoJSON output format and WGS84 bounding box.
/// </summary>
public sealed class WfsCapabilitiesTests
{
    private static readonly XNamespace Wfs = "http://www.opengis.net/wfs/2.0";
    private static readonly XNamespace Ows = "http://www.opengis.net/ows/1.1";

    [Fact]
    public async Task Capabilities_list_each_feature_type_with_geojson_output()
    {
        var map = OgcFixtures.Map(MapService.Wfs);
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676));
        var layer = await services.LoadAsync(map, map.Layers[0], CancellationToken.None);

        var xml = await WfsCapabilities.BuildAsync(
            map, [layer], "http://localhost/ogc/world/wfs", services, new OgcOptions(), CancellationToken.None);
        var document = XDocument.Parse(xml);

        Assert.Equal("2.0.0", document.Root!.Attribute("version")!.Value);
        Assert.Equal("WFS", document.Descendants(Ows + "ServiceType").Single().Value);
        var featureType = document.Descendants(Wfs + "FeatureType").Single();
        Assert.Equal("Cities", featureType.Element(Wfs + "Name")!.Value);
        Assert.Equal("urn:ogc:def:crs:EPSG::4326", featureType.Element(Wfs + "DefaultCRS")!.Value);
        Assert.Equal("urn:ogc:def:crs:EPSG::3857", featureType.Element(Wfs + "OtherCRS")!.Value);
        Assert.Equal(
            "application/geo+json",
            featureType.Element(Wfs + "OutputFormats")!.Element(Wfs + "Format")!.Value);
        var boundingBox = featureType.Element(Ows + "WGS84BoundingBox")!;
        Assert.Equal("4.9041 52.3676", boundingBox.Element(Ows + "LowerCorner")!.Value);
        Assert.Equal("4.9041 52.3676", boundingBox.Element(Ows + "UpperCorner")!.Value);
    }

    [Fact]
    public async Task Capabilities_advertise_the_operations()
    {
        var map = OgcFixtures.Map(MapService.Wfs);
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676));
        var layer = await services.LoadAsync(map, map.Layers[0], CancellationToken.None);

        var xml = await WfsCapabilities.BuildAsync(
            map, [layer], "http://localhost/ogc/world/wfs", services, new OgcOptions(), CancellationToken.None);
        var operations = XDocument.Parse(xml).Descendants(Ows + "Operation")
            .Select(operation => operation.Attribute("name")!.Value)
            .ToArray();

        Assert.Equal(["GetCapabilities", "DescribeFeatureType", "GetFeature"], operations);
    }
}
