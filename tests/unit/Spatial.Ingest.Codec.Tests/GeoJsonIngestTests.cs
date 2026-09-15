using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Ingest.Codec.Tests;

public sealed class GeoJsonIngestTests
{
    private static DecodedDataset Decode(string json, DecodeOptions? options = null) =>
        DatasetDecoder.Decode(new MemoryStream(Encoding.UTF8.GetBytes(json)), IngestFormat.GeoJson, options);

    private static string Feature(string properties, string geometry) =>
        $$"""{"type":"Feature","properties":{{properties}},"geometry":{{geometry}}}""";

    private static string Collection(params string[] features) =>
        $$"""{"type":"FeatureCollection","features":[{{string.Join(",", features)}}]}""";

    [Fact]
    public void Feature_collection_infers_the_schema_in_first_seen_order_with_geometry_last()
    {
        var decoded = Decode(Collection(
            Feature("""{"name":"A","count":2,"ratio":1.5,"active":true}""", """{"type":"Point","coordinates":[1,2]}"""),
            Feature("""{"name":"B","count":3,"ratio":2.5,"active":false}""", """{"type":"Point","coordinates":[3,4]}""")));

        Assert.Equal(["name", "count", "ratio", "active", "geometry"], decoded.Schema.Fields.Select(field => field.Name));
        Assert.Equal(AttributeKind.String, decoded.Schema[0].Kind);
        Assert.Equal(AttributeKind.Int64, decoded.Schema[1].Kind);
        Assert.Equal(AttributeKind.Double, decoded.Schema[2].Kind);
        Assert.Equal(AttributeKind.Boolean, decoded.Schema[3].Kind);
        Assert.Equal(AttributeKind.Geometry, decoded.Schema[4].Kind);
        Assert.All(decoded.Schema.Fields.Take(4), field => Assert.False(field.Nullable));
        Assert.True(decoded.Schema[4].Nullable);
        Assert.Single(decoded.Pages);
        Assert.Equal(2, decoded.Pages[0].Count);
    }

    [Fact]
    public void Decoded_values_are_core_values_stamped_with_the_decode_crs()
    {
        var decoded = Decode(
            Collection(Feature("""{"name":"A","count":2}""", """{"type":"Point","coordinates":[1,2]}""")),
            new DecodeOptions { Srid = 3857 });

        var feature = decoded.Pages[0][0];
        Assert.Equal("A", feature["name"].StringValue);
        Assert.Equal(2, feature["count"].Int64Value);
        var point = Assert.IsType<Point>(feature["geometry"].GeometryValue);
        Assert.Equal(1, point.X);
        Assert.Equal(2, point.Y);
        Assert.Equal(CoordinateReference.Epsg(3857), point.CoordinateReference);
    }

    [Fact]
    public void A_missing_or_null_property_makes_its_field_nullable()
    {
        var decoded = Decode(Collection(
            Feature("""{"a":1}""", "null"),
            Feature("""{}""", """{"type":"Point","coordinates":[0,0]}""")));

        Assert.True(decoded.Schema[0].Nullable);
        Assert.True(decoded.Schema[1].Nullable);
        Assert.False(decoded.Pages[0][0]["a"].IsNull);
        Assert.True(decoded.Pages[0][0]["geometry"].IsNull);
        Assert.True(decoded.Pages[0][1]["a"].IsNull);
    }

    [Fact]
    public void Mixed_numbers_widen_to_double_and_mixed_boolean_and_number_becomes_string()
    {
        var decoded = Decode(Collection(
            Feature("""{"n":1,"mixed":true}""", "null"),
            Feature("""{"n":1.5,"mixed":2}""", "null")));

        Assert.Equal(AttributeKind.Double, decoded.Schema[0].Kind);
        Assert.Equal(AttributeKind.String, decoded.Schema[1].Kind);
    }

    [Fact]
    public void Nested_property_objects_are_preserved_as_raw_json_text()
    {
        var decoded = Decode(Collection(Feature("""{"meta":{"k":1}}""", "null")));

        Assert.Equal(AttributeKind.String, decoded.Schema[0].Kind);
        Assert.Equal("""{"k":1}""", decoded.Pages[0][0]["meta"].StringValue);
    }

    [Fact]
    public void Features_are_split_into_pages_of_the_requested_batch_size()
    {
        var features = Enumerable.Range(0, 5)
            .Select(index => Feature($$"""{"n":{{index}}}""", "null"))
            .ToArray();
        var decoded = Decode(Collection(features), new DecodeOptions { BatchSize = 2 });

        Assert.Equal(3, decoded.Pages.Count);
        Assert.Equal(2, decoded.Pages[0].Count);
        Assert.Equal(2, decoded.Pages[1].Count);
        Assert.Equal(1, decoded.Pages[2].Count);
    }

    [Fact]
    public void A_single_feature_document_is_accepted()
    {
        var decoded = Decode(Feature("""{"name":"only"}""", """{"type":"Point","coordinates":[5,6]}"""));

        Assert.Single(decoded.Pages);
        Assert.Equal(1, decoded.Pages[0].Count);
    }

    [Theory]
    [InlineData("""{"type":"Point","coordinates":[1,2]}""", typeof(Point))]
    [InlineData("""{"type":"Point","coordinates":[1,2,3]}""", typeof(Point))]
    [InlineData("""{"type":"LineString","coordinates":[[1,2],[3,4]]}""", typeof(LineString))]
    [InlineData("""{"type":"Polygon","coordinates":[[[0,0],[0,1],[1,1],[0,0]]]}""", typeof(Polygon))]
    [InlineData("""{"type":"MultiPoint","coordinates":[[1,2],[3,4]]}""", typeof(MultiPoint))]
    [InlineData("""{"type":"MultiLineString","coordinates":[[[1,2],[3,4]]]}""", typeof(MultiLineString))]
    [InlineData("""{"type":"MultiPolygon","coordinates":[[[[0,0],[0,1],[1,1],[0,0]]]]}""", typeof(MultiPolygon))]
    [InlineData("""{"type":"GeometryCollection","geometries":[{"type":"Point","coordinates":[1,2]}]}""", typeof(GeometryCollection))]
    public void Every_geojson_geometry_type_decodes(string geometry, Type expected)
    {
        var decoded = Decode(Collection(Feature("""{"id":1}""", geometry)));

        Assert.IsType(expected, decoded.Pages[0][0]["geometry"].GeometryValue);
    }

    [Fact]
    public void A_polygon_keeps_its_interior_rings()
    {
        var decoded = Decode(Collection(Feature(
            "{}",
            """{"type":"Polygon","coordinates":[[[0,0],[0,4],[4,4],[0,0]],[[1,1],[1,2],[2,2],[1,1]]]}""")));

        var polygon = Assert.IsType<Polygon>(decoded.Pages[0][0]["geometry"].GeometryValue);
        Assert.Single(polygon.InteriorRings);
        Assert.Equal(4, polygon.ExteriorRing.CoordinateCount);
    }

    [Fact]
    public void Empty_coordinate_arrays_decode_to_empty_geometries()
    {
        var decoded = Decode(Collection(
            Feature("""{"kind":"point"}""", """{"type":"Point","coordinates":[]}"""),
            Feature("""{"kind":"polygon"}""", """{"type":"Polygon","coordinates":[]}"""),
            Feature("""{"kind":"line"}""", """{"type":"LineString","coordinates":[]}""")));

        Assert.True(decoded.Pages[0][0]["geometry"].GeometryValue.IsEmpty);
        Assert.True(decoded.Pages[0][1]["geometry"].GeometryValue.IsEmpty);
        Assert.True(decoded.Pages[0][2]["geometry"].GeometryValue.IsEmpty);
    }

    [Fact]
    public void Three_element_positions_carry_z()
    {
        var decoded = Decode(Collection(Feature("{}", """{"type":"Point","coordinates":[1,2,3]}""")));

        var point = Assert.IsType<Point>(decoded.Pages[0][0]["geometry"].GeometryValue);
        Assert.Equal(3, point.Z);
    }

    [Fact]
    public void A_numeric_feature_id_becomes_the_feature_identity()
    {
        var decoded = Decode("""{"type":"Feature","id":42,"properties":{},"geometry":null}""");

        Assert.Equal("42", decoded.Pages[0][0].Id.Value);
    }

    [Fact]
    public void A_string_feature_id_becomes_the_feature_identity()
    {
        var decoded = Decode("""{"type":"Feature","id":"abc","properties":{},"geometry":null}""");

        Assert.Equal("abc", decoded.Pages[0][0].Id.Value);
    }

    [Fact]
    public void Features_without_an_id_get_a_synthetic_identity()
    {
        var decoded = Decode(Collection(Feature("{}", "null"), Feature("{}", "null")));

        Assert.Equal("1", decoded.Pages[0][0].Id.Value);
        Assert.Equal("2", decoded.Pages[0][1].Id.Value);
    }

    [Fact]
    public void A_named_identity_field_is_reported_when_it_exists_in_the_schema()
    {
        var decoded = Decode(
            Collection(Feature("""{"oid":7,"name":"A"}""", "null")),
            new DecodeOptions { IdentityField = "oid" });

        Assert.Equal("oid", decoded.IdentityField);
    }

    [Fact]
    public void A_named_identity_field_missing_from_the_schema_is_not_reported()
    {
        var decoded = Decode(Collection(Feature("""{"name":"A"}""", "null")), new DecodeOptions { IdentityField = "oid" });

        Assert.Null(decoded.IdentityField);
    }

    [Fact]
    public void A_custom_geometry_field_name_is_honoured()
    {
        var decoded = Decode(
            Collection(Feature("""{"name":"A"}""", """{"type":"Point","coordinates":[1,2]}""")),
            new DecodeOptions { GeometryField = "shape" });

        Assert.Equal("shape", decoded.Schema[1].Name);
        Assert.Equal(AttributeKind.Geometry, decoded.Pages[0][0]["shape"].Kind);
    }

    [Theory]
    [InlineData("""[1,2,3]""")]
    [InlineData("""{"type":"Nope","coordinates":[1,2]}""")]
    public void A_non_feature_root_document_fails(string document)
    {
        Assert.Throws<IngestFormatException>(() => Decode(document));
    }

    [Fact]
    public void A_feature_collection_without_a_features_array_fails()
    {
        Assert.Throws<IngestFormatException>(() => Decode("""{"type":"FeatureCollection","features":{}}"""));
    }

    [Fact]
    public void A_non_object_feature_fails()
    {
        Assert.Throws<IngestFormatException>(() => Decode("""{"type":"FeatureCollection","features":[5]}"""));
    }

    [Fact]
    public void An_unsupported_geometry_type_fails()
    {
        Assert.Throws<IngestFormatException>(
            () => Decode(Collection(Feature("{}", """{"type":"Circle","coordinates":[1,2]}"""))));
    }

    [Fact]
    public void A_geometry_without_a_type_fails()
    {
        Assert.Throws<IngestFormatException>(
            () => Decode(Collection(Feature("{}", """{"coordinates":[1,2]}"""))));
    }

    [Fact]
    public void A_non_geojson_geometry_value_fails()
    {
        Assert.Throws<IngestFormatException>(
            () => Decode(Collection("""{"type":"Feature","properties":{},"geometry":5}""")));
    }

    [Fact]
    public void A_position_with_one_ordinate_fails()
    {
        Assert.Throws<IngestFormatException>(
            () => Decode(Collection(Feature("{}", """{"type":"Point","coordinates":[1]}"""))));
    }

    [Fact]
    public void A_non_numeric_position_ordinate_fails()
    {
        Assert.Throws<IngestFormatException>(
            () => Decode(Collection(Feature("{}", """{"type":"Point","coordinates":[1,"x"]}"""))));
    }
}
