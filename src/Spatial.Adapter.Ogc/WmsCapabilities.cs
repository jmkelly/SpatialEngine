using System.Globalization;
using System.Xml.Linq;
using Spatial.Core.Geometry;
using Spatial.PluginSdk;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Writes the WMS 1.3.0 capabilities document (ADR-0053 §3): service
/// metadata, the request verbs with their formats, and one queryable layer
/// per feature layer carrying EPSG:4326/CRS:84/EPSG:3857 bounding boxes.
/// </summary>
internal static class WmsCapabilities
{
    public static async Task<string> BuildAsync(
        Map map, IReadOnlyList<OgcLayer> layers, string baseUrl, OgcRequestServices services, OgcOptions options, CancellationToken cancellationToken)
    {
        var rootLayer = new XElement(
            OgcXml.Wms + "Layer",
            new XElement(OgcXml.Wms + "Title", map.Name),
            CrsElement("EPSG:4326"),
            CrsElement("CRS:84"),
            CrsElement("EPSG:3857"));
        foreach (var layer in layers)
        {
            rootLayer.Add(await LayerAsync(layer, services, cancellationToken));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                OgcXml.Wms + "WMS_Capabilities",
                new XAttribute("version", "1.3.0"),
                new XAttribute(XNamespace.Xmlns + "xlink", OgcXml.Xlink.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute(
                    XName.Get("schemaLocation", "http://www.w3.org/2001/XMLSchema-instance"),
                    "http://www.opengis.net/wms http://schemas.opengis.net/wms/1.3.0/capabilities_1_3_0.xsd"),
                Service(options, baseUrl),
                new XElement(
                    OgcXml.Wms + "Capability",
                    Request(baseUrl),
                    new XElement(OgcXml.Wms + "Exception", new XElement(OgcXml.Wms + "Format", "XML")),
                    rootLayer)));
        return OgcXml.Write(document);
    }

    private static async Task<XElement> LayerAsync(OgcLayer layer, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var element = new XElement(
            OgcXml.Wms + "Layer",
            new XAttribute("queryable", "1"),
            new XElement(OgcXml.Wms + "Name", layer.Name),
            new XElement(OgcXml.Wms + "Title", layer.Name),
            CrsElement("EPSG:4326"),
            CrsElement("CRS:84"),
            CrsElement("EPSG:3857"),
            new XElement(
                OgcXml.Wms + "Style",
                new XElement(OgcXml.Wms + "Name", "default"),
                new XElement(OgcXml.Wms + "Title", "Default")));
        var extent = await OgcGeometry.ExtentAsync(services.Features(layer.Store), layer.Layer.Dataset, cancellationToken);
        if (extent is { } bounds)
        {
            var source = $"EPSG:{layer.Description.Srid.ToString(CultureInfo.InvariantCulture)}";
            var wgs84 = OgcGeometry.Transform(bounds, source, "EPSG:4326", services.Transforms, cancellationToken);
            var mercator = OgcGeometry.ToWebMercator(wgs84, services.Transforms, cancellationToken);
            element.Add(GeographicBoundingBox(wgs84));
            element.Add(BoundingBox("EPSG:4326", wgs84, yFirst: true));
            element.Add(BoundingBox("CRS:84", wgs84, yFirst: false));
            element.Add(BoundingBox("EPSG:3857", mercator, yFirst: false));
        }

        return element;
    }

    private static XElement Service(OgcOptions options, string baseUrl) =>
        new(
            OgcXml.Wms + "Service",
            new XElement(OgcXml.Wms + "Name", "WMS"),
            new XElement(OgcXml.Wms + "Title", options.ServiceTitle),
            new XElement(OgcXml.Wms + "Abstract", "OGC Web Map Service over the Spatial Engine map registry."),
            new XElement(OgcXml.Wms + "OnlineResource", new XAttribute(OgcXml.Xlink + "href", baseUrl)));

    private static XElement Request(string baseUrl) =>
        new(
            OgcXml.Wms + "Request",
            Operation("GetCapabilities", Endpoint(baseUrl), "application/xml"),
            Operation("GetMap", Endpoint(baseUrl), "image/png", "image/jpeg"),
            Operation("GetFeatureInfo", Endpoint(baseUrl), "text/plain", "text/html", "text/xml", "application/json", "application/vnd.ogc.gml"));

    // A DCP Get advertises the service endpoint, not a ready-made request: the
    // WMS 1.3.0 examples (and GeoServer) end it with a bare '?'. Clients that
    // honour the advertised URI (QGIS defaults to doing so) append their own
    // service/request parameters; repeating them because the URI already
    // carried them makes the parameter malformed (SERVICE=WMS,WMS).
    private static string Endpoint(string baseUrl) => $"{baseUrl}?";

    private static XElement Operation(string name, string href, params string[] formats) =>
        new(
            OgcXml.Wms + name,
            formats.Select(format => new XElement(OgcXml.Wms + "Format", format)),
            new XElement(
                OgcXml.Wms + "DCPType",
                new XElement(
                    OgcXml.Wms + "HTTP",
                    new XElement(
                        OgcXml.Wms + "Get",
                        new XElement(OgcXml.Wms + "OnlineResource", new XAttribute(OgcXml.Xlink + "href", href))))));

    private static XElement CrsElement(string crs) => new(OgcXml.Wms + "CRS", crs);

    private static XElement GeographicBoundingBox(Envelope wgs84) =>
        new(
            OgcXml.Wms + "EX_GeographicBoundingBox",
            Number(OgcXml.Wms + "westBoundLongitude", wgs84.MinX),
            Number(OgcXml.Wms + "eastBoundLongitude", wgs84.MaxX),
            Number(OgcXml.Wms + "southBoundLatitude", wgs84.MinY),
            Number(OgcXml.Wms + "northBoundLatitude", wgs84.MaxY));

    private static XElement BoundingBox(string crs, Envelope bounds, bool yFirst) =>
        new(
            OgcXml.Wms + "BoundingBox",
            new XAttribute("CRS", crs),
            new XAttribute("minx", Double(yFirst ? bounds.MinY : bounds.MinX)),
            new XAttribute("miny", Double(yFirst ? bounds.MinX : bounds.MinY)),
            new XAttribute("maxx", Double(yFirst ? bounds.MaxY : bounds.MaxX)),
            new XAttribute("maxy", Double(yFirst ? bounds.MaxX : bounds.MaxY)));

    private static XElement Number(XName name, double value) => new(name, Double(value));

    private static string Double(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
