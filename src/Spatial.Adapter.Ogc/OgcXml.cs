using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// OGC XML serialisation (ADR-0053 §3): the service-configured namespaces,
/// the capabilities documents and the <c>ServiceExceptionReport</c> failure
/// envelope. XML is generated here and never enters a contract.
/// </summary>
internal static class OgcXml
{
    /// <summary>The WMS 1.3.0 namespace.</summary>
    public static readonly XNamespace Wms = "http://www.opengis.net/wms";

    /// <summary>The WFS 2.0.0 namespace.</summary>
    public static readonly XNamespace Wfs = "http://www.opengis.net/wfs/2.0";

    /// <summary>The OWS common namespace (WFS operations metadata, exception reports).</summary>
    public static readonly XNamespace Ows = "http://www.opengis.net/ows/1.1";

    /// <summary>The XLink namespace.</summary>
    public static readonly XNamespace Xlink = "http://www.w3.org/1999/xlink";

    /// <summary>The XML Schema namespace.</summary>
    public static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    /// <summary>The GML 3.2 namespace.</summary>
    public static readonly XNamespace Gml = "http://www.opengis.net/gml/3.2";

    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    /// <summary>Serialises a document as indented UTF-8 with an XML declaration.</summary>
    public static string Write(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = true,
            OmitXmlDeclaration = false,
        };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>The OGC <c>ServiceExceptionReport</c> failure envelope.</summary>
    public static string ServiceExceptionReport(string code, string message)
    {
        var document = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                Wms + "ServiceExceptionReport",
                new XAttribute("version", "1.3.0"),
                new XAttribute(XNamespace.Xmlns + "ogc", "http://www.opengis.net/ogc"),
                new XElement(
                    Wms + "ServiceException",
                    new XAttribute("code", code),
                    message)));
        return Write(document);
    }

    /// <summary>A root element carrying the XSI schema-instance attributes.</summary>
    public static XElement Root(XName name, XAttribute schemaLocation) =>
        new(name, new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName), schemaLocation);
}
