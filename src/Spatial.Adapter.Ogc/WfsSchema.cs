using System.Xml.Linq;
using Spatial.Core.Features;

namespace Spatial.Adapter.Ogc;

/// <summary>
/// Builds the WFS <c>DescribeFeatureType</c> XML Schema (ADR-0053 §3) from the
/// dataset descriptions: one complex type per feature layer with an element
/// per field (kind-mapped, nullability preserved) and a GML geometry property.
/// </summary>
internal static class WfsSchema
{
    public static string Build(IReadOnlyList<OgcLayer> layers)
    {
        var schema = new XElement(
            OgcXml.Xsd + "schema",
            new XAttribute(XNamespace.Xmlns + "xsd", OgcXml.Xsd.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "gml", OgcXml.Gml.NamespaceName),
            new XAttribute("elementFormDefault", "qualified"),
            new XElement(
                OgcXml.Xsd + "import",
                new XAttribute("namespace", OgcXml.Gml.NamespaceName),
                new XAttribute("schemaLocation", "http://schemas.opengis.net/gml/3.2.1/gml.xsd")));
        foreach (var layer in layers)
        {
            schema.Add(Type(layer));
            schema.Add(
                new XElement(
                    OgcXml.Xsd + "element",
                    new XAttribute("name", layer.Name),
                    new XAttribute("type", $"{layer.Name}Type"),
                    new XAttribute("substitutionGroup", "gml:AbstractFeature")));
        }

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), schema);
        return OgcXml.Write(document);
    }

    private static XElement Type(OgcLayer layer)
    {
        var sequence = new XElement(OgcXml.Xsd + "sequence");
        foreach (var field in layer.Description.Schema.Fields)
        {
            sequence.Add(Element(field));
        }

        return new XElement(
            OgcXml.Xsd + "complexType",
            new XAttribute("name", $"{layer.Name}Type"),
            new XElement(
                OgcXml.Xsd + "complexContent",
                new XElement(
                    OgcXml.Xsd + "extension",
                    new XAttribute("base", "gml:AbstractFeatureType"),
                    sequence)));
    }

    private static XElement Element(FieldDefinition field) =>
        new(
            OgcXml.Xsd + "element",
            new XAttribute("name", field.Name),
            new XAttribute("minOccurs", field.Nullable ? "0" : "1"),
            new XAttribute("nillable", field.Nullable ? "true" : "false"),
            new XAttribute("type", XsdType(field.Kind)));

    private static string XsdType(AttributeKind kind) => kind switch
    {
        AttributeKind.Boolean => "xsd:boolean",
        AttributeKind.Int64 => "xsd:long",
        AttributeKind.Double => "xsd:double",
        AttributeKind.DateTimeOffset => "xsd:dateTime",
        AttributeKind.Geometry => "gml:GeometryPropertyType",
        _ => "xsd:string",
    };
}
