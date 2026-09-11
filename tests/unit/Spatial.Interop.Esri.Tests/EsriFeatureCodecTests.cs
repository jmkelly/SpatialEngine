using System.Text;
using System.Text.Json;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Interop.Esri.Tests;

public sealed class EsriFeatureCodecTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("population", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static Feature Feature() => new(
        new FeatureId("amsterdam"),
        Schema,
        [
            AttributeValue.FromString("Amsterdam"),
            AttributeValue.FromInt64(900_000),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(4.9041, 52.3676, CoordinateReference.Epsg(4326))),
        ]);

    private static JsonElement Write(EsriFeatureWriteOptions options)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("feature");
            EsriFeatureCodec.Write(writer, Feature(), options);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement.GetProperty("feature");
    }

    [Fact]
    public void A_feature_writes_its_object_id_attributes_and_geometry()
    {
        var element = Write(new EsriFeatureWriteOptions("OBJECTID", 7));

        Assert.Equal(7, element.GetProperty("attributes").GetProperty("OBJECTID").GetInt64());
        Assert.Equal("Amsterdam", element.GetProperty("attributes").GetProperty("name").GetString());
        Assert.Equal(4.9041, element.GetProperty("geometry").GetProperty("x").GetDouble(), 4);
        Assert.Equal(4326, element.GetProperty("geometry").GetProperty("spatialReference").GetProperty("wkid").GetInt32());
    }

    [Fact]
    public void Out_fields_project_the_attributes()
    {
        var element = Write(new EsriFeatureWriteOptions("OBJECTID", 1, OutFields: ["population"]));

        var attributes = element.GetProperty("attributes");
        Assert.Equal(900_000, attributes.GetProperty("population").GetInt64());
        Assert.False(attributes.TryGetProperty("name", out _));
    }

    [Fact]
    public void Return_geometry_false_omits_the_geometry()
    {
        var element = Write(new EsriFeatureWriteOptions("OBJECTID", 1, ReturnGeometry: false));

        Assert.False(element.TryGetProperty("geometry", out _));
    }

    [Fact]
    public void A_feature_decodes_back_to_core_values()
    {
        var element = JsonDocument.Parse(
            """
            {
              "attributes": {"OBJECTID": 7, "name": "Amsterdam", "population": 900000},
              "geometry": {"x": 4.9041, "y": 52.3676, "spatialReference": {"wkid": 4326}}
            }
            """).RootElement;

        var feature = EsriFeatureCodec.Decode(element, Schema, "OBJECTID", "geometry", CoordinateReference.Epsg(4326));

        Assert.Equal("7", feature.Id.Value);
        Assert.Equal("Amsterdam", feature["name"].StringValue);
        Assert.Equal(900_000, feature["population"].Int64Value);
        Assert.Equal(4.9041, feature["geometry"].GeometryValue.Envelope!.Value.MinX, 4);
    }

    [Fact]
    public void A_feature_without_its_object_id_is_rejected()
    {
        var element = JsonDocument.Parse("""{"attributes":{"name":"x"},"geometry":{"x":1,"y":2}}""").RootElement;

        Assert.Throws<EsriInteropException>(() =>
            EsriFeatureCodec.Decode(element, Schema, "OBJECTID", "geometry", CoordinateReference.Epsg(4326)));
    }

    [Fact]
    public void A_missing_attribute_becomes_null_and_respects_nullability()
    {
        var nullable = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String, nullable: true),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);
        var element = JsonDocument.Parse("""{"attributes":{"OBJECTID":1},"geometry":null}""").RootElement;

        var feature = EsriFeatureCodec.Decode(element, nullable, "OBJECTID", "geometry", CoordinateReference.Epsg(4326));

        Assert.True(feature["name"].IsNull);
        Assert.True(feature["geometry"].IsNull);
    }

    [Fact]
    public void Date_and_guid_attributes_use_their_wire_forms()
    {
        var stream = new MemoryStream();
        var date = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            EsriAttributeCodec.Write(writer, "when", AttributeValue.FromDateTimeOffset(date));
            EsriAttributeCodec.Write(writer, "id", AttributeValue.FromGuid(guid));
            writer.WriteEndObject();
        }

        var element = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement;
        Assert.Equal(1_700_000_000_000, element.GetProperty("when").GetInt64());
        Assert.Equal(guid.ToString(), element.GetProperty("id").GetString());

        Assert.Equal(date, EsriAttributeCodec.Read(element, new FieldDefinition("when", AttributeKind.DateTimeOffset)).DateTimeOffsetValue);
        Assert.Equal(guid, EsriAttributeCodec.Read(element, new FieldDefinition("id", AttributeKind.Guid)).GuidValue);
    }
}
