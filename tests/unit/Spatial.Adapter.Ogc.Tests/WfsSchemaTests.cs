using System.Xml.Linq;
using Spatial.PluginSdk.Providers;

namespace Spatial.Adapter.Ogc.Tests;

/// <summary>
/// WFS <c>DescribeFeatureType</c> shaping (ADR-0053 §3): the XSD carries the
/// dataset's fields with kind-mapped XML Schema types and a GML geometry
/// property.
/// </summary>
public sealed class WfsSchemaTests
{
    private static readonly XNamespace Xsd = "http://www.w3.org/2001/XMLSchema";

    private static OgcLayer Layer() =>
        new(OgcFixtures.Map(MapServiceKind.Wfs).Layers[0], "Cities", OgcFixtures.Store, OgcFixtures.Description());

    [Fact]
    public void The_schema_declares_the_feature_type_and_its_fields()
    {
        var xml = WfsSchema.Build([Layer()]);

        var document = XDocument.Parse(xml);
        var complexType = document.Descendants(Xsd + "complexType").Single();
        Assert.Equal("CitiesType", complexType.Attribute("name")!.Value);
        var elements = complexType.Descendants(Xsd + "element")
            .Select(element => (element.Attribute("name")!.Value, element.Attribute("type")!.Value))
            .ToArray();
        Assert.Equal(
            [("name", "xsd:string"), ("population", "xsd:long"), ("geometry", "gml:GeometryPropertyType")],
            elements);
    }

    [Fact]
    public void The_schema_declares_the_global_feature_element()
    {
        var xml = WfsSchema.Build([Layer()]);

        var document = XDocument.Parse(xml);
        var element = document.Root!.Elements(Xsd + "element").Single();

        Assert.Equal("Cities", element.Attribute("name")!.Value);
        Assert.Equal("CitiesType", element.Attribute("type")!.Value);
        Assert.Equal("gml:AbstractFeature", element.Attribute("substitutionGroup")!.Value);
    }

    [Fact]
    public void Nullable_fields_are_nillable()
    {
        var schema = new Spatial.Core.Features.FeatureSchema(
        [
            new Spatial.Core.Features.FieldDefinition("label", Spatial.Core.Features.AttributeKind.String, nullable: true),
            new Spatial.Core.Features.FieldDefinition("geometry", Spatial.Core.Features.AttributeKind.Geometry),
        ]);
        var description = new DatasetDescription("demo.roads", "demo", "roads", "geometry", 4326, "LineString", 0, [], schema);
        var layer = new OgcLayer(OgcFixtures.Map(MapServiceKind.Wfs).Layers[0], "Roads", OgcFixtures.Store, description);

        var document = XDocument.Parse(WfsSchema.Build([layer]));
        var label = document.Descendants(Xsd + "element").Single(element => element.Attribute("name")?.Value == "label");

        Assert.Equal("true", label.Attribute("nillable")!.Value);
        Assert.Equal("0", label.Attribute("minOccurs")!.Value);
    }
}
