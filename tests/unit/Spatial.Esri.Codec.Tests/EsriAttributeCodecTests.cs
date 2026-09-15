using System.Text;
using System.Text.Json;
using Spatial.Core.Features;

namespace Spatial.Esri.Codec.Tests;

/// <summary>
/// The attribute half of the Esri feature JSON codec: each core
/// <see cref="AttributeKind"/> has one wire shape and unknown shapes are
/// rejected rather than coerced (spec §9.1.1).
/// </summary>
public sealed class EsriAttributeCodecTests
{
    private static JsonElement WriteValue(AttributeValue value)
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("value");
            EsriAttributeCodec.WriteValue(writer, value);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement.GetProperty("value");
    }

    private static JsonElement Wrap(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Every_scalar_kind_has_a_wire_shape()
    {
        Assert.Equal(JsonValueKind.Null, WriteValue(AttributeValue.Null).ValueKind);
        Assert.True(WriteValue(AttributeValue.FromBoolean(true)).GetBoolean());
        Assert.Equal(42L, WriteValue(AttributeValue.FromInt64(42)).GetInt64());
        Assert.Equal(2.5, WriteValue(AttributeValue.FromDouble(2.5)).GetDouble());
        Assert.Equal("x", WriteValue(AttributeValue.FromString("x")).GetString());
        Assert.Equal(1_700_000_000_000L, WriteValue(AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000))).GetInt64());
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        Assert.Equal(guid.ToString(), WriteValue(AttributeValue.FromGuid(guid)).GetString());
    }

    [Fact]
    public void A_geometry_value_writes_as_null()
    {
        var geometry = AttributeValue.FromGeometry(Spatial.Core.Geometry.GeometryFactory.CreatePoint(1, 2));

        Assert.Equal(JsonValueKind.Null, WriteValue(geometry).ValueKind);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Booleans_read_from_each_accepted_shape(string json, bool expected)
    {
        var value = EsriAttributeCodec.Read(Wrap($"{{\"flag\":{json}}}"), new FieldDefinition("flag", AttributeKind.Boolean));

        Assert.Equal(expected, value.BooleanValue);
    }

    [Fact]
    public void Booleans_reject_a_non_boolean_shape()
    {
        Assert.Throws<EsriInteropException>(() =>
            EsriAttributeCodec.Read(Wrap("""{"flag":"yes"}"""), new FieldDefinition("flag", AttributeKind.Boolean)));
    }

    [Fact]
    public void Scalar_values_read_from_their_shapes()
    {
        Assert.Equal(42L, EsriAttributeCodec.Read(Wrap("""{"v":42}"""), new FieldDefinition("v", AttributeKind.Int64)).Int64Value);
        Assert.Equal(2.5, EsriAttributeCodec.Read(Wrap("""{"v":2.5}"""), new FieldDefinition("v", AttributeKind.Double)).DoubleValue);
        Assert.Equal("x", EsriAttributeCodec.Read(Wrap("""{"v":"x"}"""), new FieldDefinition("v", AttributeKind.String)).StringValue);
    }

    [Fact]
    public void Integers_and_doubles_reject_a_non_numeric_shape()
    {
        Assert.Throws<EsriInteropException>(() =>
            EsriAttributeCodec.Read(Wrap("""{"v":"x"}"""), new FieldDefinition("v", AttributeKind.Int64)));
        Assert.Throws<EsriInteropException>(() =>
            EsriAttributeCodec.Read(Wrap("""{"v":"x"}"""), new FieldDefinition("v", AttributeKind.Double)));
        Assert.Throws<EsriInteropException>(() =>
            EsriAttributeCodec.Read(Wrap("""{"v":true}"""), new FieldDefinition("v", AttributeKind.String)));
    }

    [Fact]
    public void Dates_read_from_epoch_milliseconds_or_an_iso_string()
    {
        var fromEpoch = EsriAttributeCodec.Read(Wrap("""{"v":1700000000000}"""), new FieldDefinition("v", AttributeKind.DateTimeOffset));
        var fromIso = EsriAttributeCodec.Read(Wrap("""{"v":"2023-11-14T22:13:20+00:00"}"""), new FieldDefinition("v", AttributeKind.DateTimeOffset));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), fromEpoch.DateTimeOffsetValue);
        Assert.Equal(fromEpoch.DateTimeOffsetValue, fromIso.DateTimeOffsetValue);
        Assert.Throws<EsriInteropException>(() =>
            EsriAttributeCodec.Read(Wrap("""{"v":true}"""), new FieldDefinition("v", AttributeKind.DateTimeOffset)));
    }

    [Fact]
    public void Guids_read_from_a_guid_string()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal(guid, EsriAttributeCodec.Read(Wrap($"{{\"v\":\"{guid}\"}}"), new FieldDefinition("v", AttributeKind.Guid)).GuidValue);
        Assert.Throws<EsriInteropException>(() =>
            EsriAttributeCodec.Read(Wrap("""{"v":"not-a-guid"}"""), new FieldDefinition("v", AttributeKind.Guid)));
    }

    [Fact]
    public void A_missing_or_null_property_reads_as_null()
    {
        var field = new FieldDefinition("v", AttributeKind.String, nullable: true);

        Assert.True(EsriAttributeCodec.Read(Wrap("""{}"""), field).IsNull);
        Assert.True(EsriAttributeCodec.Read(Wrap("""{"v":null}"""), field).IsNull);
        Assert.True(EsriAttributeCodec.Read(Wrap("[]"), field).IsNull);
    }

    [Fact]
    public void Attribute_names_match_case_insensitively()
    {
        // Real MapServer responses do not always echo the layer metadata's
        // field casing (declared Population, emitted population).
        var field = new FieldDefinition("Population", AttributeKind.Int64);

        Assert.Equal(42L, EsriAttributeCodec.Read(Wrap("""{"population":42}"""), field).Int64Value);
    }

    [Fact]
    public void A_geometry_field_cannot_be_read_from_attributes()
    {
        Assert.Throws<EsriInteropException>(() =>
            EsriAttributeCodec.Read(Wrap("""{"v":1}"""), new FieldDefinition("v", AttributeKind.Geometry)));
    }

    [Fact]
    public void A_null_field_definition_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => EsriAttributeCodec.Read(Wrap("{}"), null!));
    }

    [Fact]
    public void The_named_write_overload_writes_the_property_name()
    {
        var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            EsriAttributeCodec.Write(writer, "name", AttributeValue.FromString("x"));
            writer.WriteEndObject();
        }

        var element = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).RootElement;
        Assert.Equal("x", element.GetProperty("name").GetString());
    }

}
