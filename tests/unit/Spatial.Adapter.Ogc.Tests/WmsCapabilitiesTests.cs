using System.Xml.Linq;
using Spatial.Contracts.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// WMS 1.3.0 capabilities shaping (ADR-0053 §3): service metadata, the
/// request verbs, and one queryable layer per feature layer with its CRS list
/// and geographic/mercator bounding boxes.
/// </summary>
public sealed class WmsCapabilitiesTests
{
    private static readonly XNamespace Wms = "http://www.opengis.net/wms";

    [Fact]
    public async Task Capabilities_describe_the_service_and_each_feature_layer()
    {
        var map = OgcFixtures.Map(MapServiceKind.Wms);
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(
            OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676),
            OgcFixtures.City("Berlin", 3_664_000, 13.4050, 52.5200));
        var layer = await services.LoadAsync(map, map.Layers[0], CancellationToken.None);

        var xml = await WmsCapabilities.BuildAsync(
            map, [layer], "http://localhost/ogc/world/wms", services, new OgcOptions(), CancellationToken.None);
        var document = XDocument.Parse(xml);

        Assert.Equal("1.3.0", document.Root!.Attribute("version")!.Value);
        Assert.Equal("WMS", document.Descendants(Wms + "Service").Single().Element(Wms + "Name")!.Value);
        Assert.Contains(document.Descendants(Wms + "Request").Elements(), element => element.Name == Wms + "GetMap");
        Assert.Contains(document.Descendants(Wms + "Request").Elements(), element => element.Name == Wms + "GetFeatureInfo");
        Assert.Equal(
            ["image/png", "image/jpeg"],
            document.Descendants(Wms + "GetMap").Single().Elements(Wms + "Format").Select(format => format.Value).ToArray());
        var layerElement = document.Descendants(Wms + "Layer").Single(element => element.Attribute("queryable")?.Value == "1");
        Assert.Equal("Cities", layerElement.Element(Wms + "Name")!.Value);
        Assert.Equal(["EPSG:4326", "CRS:84", "EPSG:3857"], layerElement.Elements(Wms + "CRS").Select(crs => crs.Value).ToArray());
    }

    [Fact]
    public async Task Capabilities_advertise_bare_dcp_endpoints()
    {
        // A client that honours the advertised DCP URI (QGIS does by default)
        // appends its own service/request; embedding them here duplicates the
        // parameters and the request is rejected.
        var map = OgcFixtures.Map(MapServiceKind.Wms);
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676));
        var layer = await services.LoadAsync(map, map.Layers[0], CancellationToken.None);

        var xml = await WmsCapabilities.BuildAsync(
            map, [layer], "http://localhost/ogc/world/wms", services, new OgcOptions(), CancellationToken.None);
        var document = XDocument.Parse(xml);

        var hrefs = document.Descendants(Wms + "Request")
            .Descendants(Wms + "OnlineResource")
            .Select(element => element.Attribute(XNamespace.Get("http://www.w3.org/1999/xlink") + "href")!.Value)
            .Distinct()
            .ToArray();
        Assert.Equal(["http://localhost/ogc/world/wms?"], hrefs);
    }

    [Fact]
    public async Task Capabilities_report_the_layer_extent()
    {
        var map = OgcFixtures.Map(MapServiceKind.Wms);
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(
            OgcFixtures.City("Amsterdam", 900_000, 4.9041, 52.3676),
            OgcFixtures.City("Berlin", 3_664_000, 13.4050, 52.5200));
        var layer = await services.LoadAsync(map, map.Layers[0], CancellationToken.None);

        var xml = await WmsCapabilities.BuildAsync(
            map, [layer], "http://localhost/ogc/world/wms", services, new OgcOptions(), CancellationToken.None);
        var document = XDocument.Parse(xml);

        var geographic = document.Descendants(Wms + "EX_GeographicBoundingBox").Single();
        Assert.Equal("4.9041", geographic.Element(Wms + "westBoundLongitude")!.Value);
        Assert.Equal("13.405", geographic.Element(Wms + "eastBoundLongitude")!.Value);
        Assert.Equal("52.3676", geographic.Element(Wms + "southBoundLatitude")!.Value);
        Assert.Equal("52.52", geographic.Element(Wms + "northBoundLatitude")!.Value);
        var epsg4326 = document.Descendants(Wms + "BoundingBox")
            .Single(element => element.Attribute("CRS")?.Value == "EPSG:4326");
        Assert.Equal("52.3676", epsg4326.Attribute("minx")!.Value);
        Assert.Equal("4.9041", epsg4326.Attribute("miny")!.Value);
    }

    [Fact]
    public async Task Capabilities_clamp_the_mercator_bounding_box_to_the_projection_domain()
    {
        // A point at the south pole is a valid WGS84 extent but outside Web
        // Mercator; the advertised mercator box must stay inside the domain.
        var map = OgcFixtures.Map(MapServiceKind.Wms);
        var (services, store) = OgcFixtures.Build(map);
        store.Seed(OgcFixtures.City("Polar", 0, 0, -90));
        var layer = await services.LoadAsync(map, map.Layers[0], CancellationToken.None);

        var xml = await WmsCapabilities.BuildAsync(
            map, [layer], "http://localhost/ogc/world/wms", services, new OgcOptions(), CancellationToken.None);
        var document = XDocument.Parse(xml);

        var mercator = document.Descendants(Wms + "BoundingBox")
            .Single(element => element.Attribute("CRS")?.Value == "EPSG:3857");
        Assert.Equal("-85.05112877980659", mercator.Attribute("miny")!.Value);
        Assert.Equal("-85.05112877980659", mercator.Attribute("maxy")!.Value);
        var geographic = document.Descendants(Wms + "EX_GeographicBoundingBox").Single();
        Assert.Equal("-90", geographic.Element(Wms + "southBoundLatitude")!.Value);
    }
}
