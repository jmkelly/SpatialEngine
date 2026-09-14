using System.Globalization;
using System.Xml.Linq;
using Spatial.Core.Geometry;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Writes the WFS 2.0.0 capabilities document (ADR-0053 §3): service
/// identification, the operations metadata and one feature type per feature
/// layer with its default CRS, GeoJSON output format and WGS84 bounding box.
/// </summary>
internal static class WfsCapabilities
{
    public static async Task<string> BuildAsync(
        Map map, IReadOnlyList<OgcLayer> layers, string baseUrl, OgcRequestServices services, OgcOptions options, CancellationToken cancellationToken)
    {
        var featureTypes = new XElement(OgcXml.Wfs + "FeatureTypeList");
        foreach (var layer in layers)
        {
            featureTypes.Add(await FeatureTypeAsync(layer, services, cancellationToken));
        }

        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                OgcXml.Wfs + "WFS_Capabilities",
                new XAttribute("version", "2.0.0"),
                new XAttribute(XNamespace.Xmlns + "ows", OgcXml.Ows.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xlink", OgcXml.Xlink.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance"),
                new XAttribute(
                    XName.Get("schemaLocation", "http://www.w3.org/2001/XMLSchema-instance"),
                    "http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd"),
                ServiceIdentification(options),
                OperationsMetadata(baseUrl),
                featureTypes));
        return OgcXml.Write(document);
    }

    private static async Task<XElement> FeatureTypeAsync(OgcLayer layer, OgcRequestServices services, CancellationToken cancellationToken)
    {
        var srid = layer.Description.Srid.ToString(CultureInfo.InvariantCulture);
        var element = new XElement(
            OgcXml.Wfs + "FeatureType",
            new XElement(OgcXml.Wfs + "Name", layer.Name),
            new XElement(OgcXml.Wfs + "Title", layer.Name),
            new XElement(OgcXml.Wfs + "DefaultCRS", $"urn:ogc:def:crs:EPSG::{srid}"),
            new XElement(OgcXml.Wfs + "OtherCRS", "urn:ogc:def:crs:EPSG::3857"),
            new XElement(
                OgcXml.Wfs + "OutputFormats",
                new XElement(OgcXml.Wfs + "Format", "application/geo+json"),
                new XElement(OgcXml.Wfs + "Format", "application/json")));
        var extent = await OgcGeometry.ExtentAsync(services.Features(layer.Store), layer.Layer.Dataset, cancellationToken);
        if (extent is { } bounds)
        {
            var source = $"EPSG:{srid}";
            var wgs84 = OgcGeometry.Transform(bounds, source, "EPSG:4326", services.Transforms, cancellationToken);
            element.Add(
                new XElement(
                    OgcXml.Ows + "WGS84BoundingBox",
                    new XElement(OgcXml.Ows + "LowerCorner", $"{Double(wgs84.MinX)} {Double(wgs84.MinY)}"),
                    new XElement(OgcXml.Ows + "UpperCorner", $"{Double(wgs84.MaxX)} {Double(wgs84.MaxY)}")));
        }

        return element;
    }

    private static XElement ServiceIdentification(OgcOptions options) =>
        new(
            OgcXml.Ows + "ServiceIdentification",
            new XElement(OgcXml.Ows + "Title", options.ServiceTitle),
            new XElement(OgcXml.Ows + "ServiceType", "WFS"),
            new XElement(OgcXml.Ows + "ServiceTypeVersion", "2.0.0"));

    private static XElement OperationsMetadata(string baseUrl) =>
        new(
            OgcXml.Ows + "OperationsMetadata",
            Operation("GetCapabilities", baseUrl),
            Operation("DescribeFeatureType", baseUrl),
            new XElement(
                OgcXml.Ows + "Operation",
                new XAttribute("name", "GetFeature"),
                new XElement(
                    OgcXml.Ows + "DCP",
                    new XElement(
                        OgcXml.Ows + "HTTP",
                        new XElement(OgcXml.Ows + "Get", new XAttribute(OgcXml.Xlink + "href", baseUrl)))),
                new XElement(
                    OgcXml.Ows + "Parameter",
                    new XAttribute("name", "outputFormat"),
                    new XElement(
                        OgcXml.Ows + "AllowedValues",
                        new XElement(OgcXml.Ows + "Value", "application/geo+json"),
                        new XElement(OgcXml.Ows + "Value", "application/json")))));

    private static XElement Operation(string name, string href) =>
        new(
            OgcXml.Ows + "Operation",
            new XAttribute("name", name),
            new XElement(
                OgcXml.Ows + "DCP",
                new XElement(
                    OgcXml.Ows + "HTTP",
                    new XElement(OgcXml.Ows + "Get", new XAttribute(OgcXml.Xlink + "href", href)))));

    private static string Double(double value) => value.ToString("R", CultureInfo.InvariantCulture);
}
