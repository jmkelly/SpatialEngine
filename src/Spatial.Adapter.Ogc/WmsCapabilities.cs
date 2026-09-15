using System.Globalization;
using System.Xml.Linq;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Geometry;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Writes the WMS capabilities document (ADR-0053 §3): service
/// metadata, the request verbs with their formats, and one queryable layer
/// per feature layer carrying EPSG:4326/CRS:84/EPSG:3857 bounding boxes.
/// VERSION 1.1.x serves the legacy 1.1.1 dialect instead: unqualified
/// <c>WMS_Capabilities</c> with the 1.1.1 DTD, the SRS vocabulary and
/// <c>LatLonBoundingBox</c> (lon/lat, never swapped); anything else serves
/// the 1.3.0 dialect.
/// </summary>
internal static class WmsCapabilities
{
    public static async Task<string> BuildAsync(
        Map map, IReadOnlyList<OgcLayer> layers, string baseUrl, OgcRequestServices services, OgcOptions options, CancellationToken cancellationToken, string version = "1.3.0")
    {
        if (version.StartsWith("1.1", StringComparison.Ordinal))
        {
            return await Build111Async(map, layers, baseUrl, services, options, cancellationToken);
        }

        return await Build130Async(map, layers, baseUrl, services, options, cancellationToken);
    }

    private static async Task<string> Build130Async(
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
            rootLayer.Add(await LayerAsync(layer, baseUrl, services, cancellationToken));
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
                    new XElement(
                        OgcXml.Wms + "Exception",
                        new XElement(OgcXml.Wms + "Format", "XML"),
                        new XElement(OgcXml.Wms + "Format", "INIMAGE"),
                        new XElement(OgcXml.Wms + "Format", "BLANK"),
                        new XElement(OgcXml.Wms + "Format", "application/vnd.ogc.se_xml"),
                        new XElement(OgcXml.Wms + "Format", "application/vnd.ogc.se_inimage"),
                        new XElement(OgcXml.Wms + "Format", "application/vnd.ogc.se_blank")),
                    rootLayer)));
        return OgcXml.Write(document);
    }

    private static async Task<XElement> LayerAsync(OgcLayer layer, string baseUrl, OgcRequestServices services, CancellationToken cancellationToken)
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
                new XElement(OgcXml.Wms + "Title", "Default"),
                new XElement(
                    OgcXml.Wms + "LegendURL",
                    new XAttribute("width", WmsService.LegendWidth),
                    new XAttribute("height", WmsService.LegendHeight),
                    new XElement(OgcXml.Wms + "Format", "image/png"),
                    new XElement(
                        OgcXml.Wms + "OnlineResource",
                        new XAttribute(OgcXml.Xlink + "href", LegendUrl(baseUrl, layer.Name))))));
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
            Operation("GetLegendGraphic", Endpoint(baseUrl), "image/png", "image/jpeg"),
            Operation("GetFeatureInfo", Endpoint(baseUrl), "text/plain", "text/html", "text/xml", "application/json", "application/vnd.ogc.gml"));

    // A DCP Get advertises the service endpoint, not a ready-made request: the
    // WMS 1.3.0 examples (and GeoServer) end it with a bare '?'. Clients that
    // honour the advertised URI (QGIS defaults to doing so) append their own
    // service/request parameters; repeating them because the URI already
    // carried them makes the parameter malformed (SERVICE=WMS,WMS).
    private static string Endpoint(string baseUrl) => $"{baseUrl}?";

    // QGIS falls back to the style's LegendURL when it has no legend cache:
    // point it at GetLegendGraphic for the advertised default style.
    private static string LegendUrl(string baseUrl, string layer) =>
        $"{baseUrl}?service=WMS&request=GetLegendGraphic&layer={Uri.EscapeDataString(layer)}&style=default&format=image/png";

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

    /// <summary>
    /// The WMS 1.1.1 capabilities dialect (T-045 item 2): DTD-validated,
    /// unqualified elements, the SRS vocabulary (CRS:84 is a 1.3.0-ism and
    /// stays out) and lon/lat boxes only — LatLonBoundingBox plus one
    /// BoundingBox per SRS, both in axis order. The layer
    /// content (one default style with its LegendURL) matches the 1.3.0
    /// dialect.
    /// </summary>
    private static async Task<string> Build111Async(
        Map map, IReadOnlyList<OgcLayer> layers, string baseUrl, OgcRequestServices services, OgcOptions options, CancellationToken cancellationToken)
    {
        var rootLayer = new XElement(
            "Layer",
            new XElement("Title", map.Name),
            SrsElement("EPSG:4326"),
            SrsElement("EPSG:3857"));
        foreach (var layer in layers)
        {
            rootLayer.Add(await Layer111Async(layer, baseUrl, services, cancellationToken));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XDocumentType(
                "WMS_Capabilities",
                null,
                "http://schemas.opengis.net/wms/1.1.1/WMS_MS_Capabilities.dtd",
                null),
            new XElement(
                "WMS_Capabilities",
                new XAttribute("version", "1.1.1"),
                Service111(options, baseUrl),
                new XElement(
                    "Capability",
                    Request111(baseUrl),
                    new XElement(
                        "Exception",
                        new XElement("Format", "application/vnd.ogc.se_xml"),
                        new XElement("Format", "application/vnd.ogc.se_inimage"),
                        new XElement("Format", "application/vnd.ogc.se_blank")),
                    rootLayer)));
        return OgcXml.Write(document);
    }

    private static async Task<XElement> Layer111Async(OgcLayer layer, string baseUrl, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var element = new XElement(
            "Layer",
            new XAttribute("queryable", "1"),
            new XElement("Name", layer.Name),
            new XElement("Title", layer.Name),
            SrsElement("EPSG:4326"),
            SrsElement("EPSG:3857"),
            new XElement(
                "Style",
                new XElement("Name", "default"),
                new XElement("Title", "Default"),
                new XElement(
                    "LegendURL",
                    new XAttribute("width", WmsService.LegendWidth),
                    new XAttribute("height", WmsService.LegendHeight),
                    new XElement("Format", "image/png"),
                    OnlineResource111(LegendUrl(baseUrl, layer.Name)))));
        var extent = await OgcGeometry.ExtentAsync(services.Features(layer.Store), layer.Layer.Dataset, cancellationToken);
        if (extent is { } bounds)
        {
            var source = $"EPSG:{layer.Description.Srid.ToString(CultureInfo.InvariantCulture)}";
            var wgs84 = OgcGeometry.Transform(bounds, source, "EPSG:4326", services.Transforms, cancellationToken);
            var mercator = OgcGeometry.ToWebMercator(wgs84, services.Transforms, cancellationToken);
            element.Add(LatLonBoundingBox(wgs84));
            element.Add(BoundingBox111("EPSG:4326", wgs84));
            element.Add(BoundingBox111("EPSG:3857", mercator));
        }

        return element;
    }

    private static XElement Service111(OgcOptions options, string baseUrl) =>
        new(
            "Service",
            new XElement("Name", "WMS"),
            new XElement("Title", options.ServiceTitle),
            new XElement("Abstract", "OGC Web Map Service over the Spatial Engine map registry."),
            OnlineResource111(baseUrl));

    private static XElement Request111(string baseUrl) =>
        new(
            "Request",
            Operation111("GetCapabilities", Endpoint(baseUrl), "application/vnd.ogc.wms_xml"),
            Operation111("GetMap", Endpoint(baseUrl), "image/png", "image/jpeg"),
            Operation111("GetLegendGraphic", Endpoint(baseUrl), "image/png", "image/jpeg"),
            Operation111(
                "GetFeatureInfo",
                Endpoint(baseUrl),
                "text/plain",
                "text/html",
                "text/xml",
                "application/json",
                "application/vnd.ogc.gml"));

    private static XElement Operation111(string name, string href, params string[] formats) =>
        new(
            name,
            formats.Select(format => new XElement("Format", format)),
            new XElement(
                "DCPType",
                new XElement(
                    "HTTP",
                    new XElement("Get", OnlineResource111(href)))));

    private static XElement OnlineResource111(string href) =>
        new(
            "OnlineResource",
            new XAttribute(XNamespace.Xmlns + "xlink", OgcXml.Xlink.NamespaceName),
            new XAttribute(OgcXml.Xlink + "type", "simple"),
            new XAttribute(OgcXml.Xlink + "href", href));

    private static XElement SrsElement(string srs) => new("SRS", srs);

    private static XElement LatLonBoundingBox(Envelope wgs84) =>
        new(
            "LatLonBoundingBox",
            new XAttribute("minx", Double(wgs84.MinX)),
            new XAttribute("miny", Double(wgs84.MinY)),
            new XAttribute("maxx", Double(wgs84.MaxX)),
            new XAttribute("maxy", Double(wgs84.MaxY)));

    private static XElement BoundingBox111(string srs, Envelope bounds) =>
        new(
            "BoundingBox",
            new XAttribute("SRS", srs),
            new XAttribute("minx", Double(bounds.MinX)),
            new XAttribute("miny", Double(bounds.MinY)),
            new XAttribute("maxx", Double(bounds.MaxX)),
            new XAttribute("maxy", Double(bounds.MaxY)));
}
