using System.Buffers.Binary;
using System.Text;
using Spatial.Core.Features;
using Spatial.Core.Features.Codec;
using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// FeatureBatchCodec: exact round trips for every attribute kind, determinism,
/// schema projection (append-only prefix decoding) and malformed-input
/// rejection with actionable byte offsets.
/// </summary>
public class FeatureBatchCodecTests
{
    public static TheoryData<string, FeatureBatch> RoundTripCases => BuildRoundTripCases();

    [Theory]
    [MemberData(nameof(RoundTripCases))]
    public void Batch_round_trips_exactly(string name, FeatureBatch batch)
    {
        var bytes = FeatureBatchCodec.Encode(batch);
        var decoded = FeatureBatchCodec.Decode(bytes);

        Assert.True(batch.Equals(decoded), $"Round trip failed for '{name}': {batch} vs {decoded}");
        Assert.Equal(batch.GetHashCode(), decoded.GetHashCode());
        Assert.Equal(bytes, FeatureBatchCodec.Encode(decoded));
    }

    [Fact]
    public void Encoding_is_deterministic()
    {
        var batch = AllKindsBatch();
        Assert.Equal(FeatureBatchCodec.Encode(batch), FeatureBatchCodec.Encode(batch));

        // Decode → encode must be byte-identical (no data is lost or added).
        var decoded = FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batch));
        Assert.Equal(FeatureBatchCodec.Encode(batch), FeatureBatchCodec.Encode(decoded));
    }

    [Fact]
    public void Large_batch_round_trips()
    {
        var schema = new FeatureSchema([new FieldDefinition("id", AttributeKind.Int64)]);
        var features = Enumerable.Range(0, 250).Select(i => new Feature(
            new FeatureId($"f{i}"),
            schema,
            [AttributeValue.FromInt64(i * 1_000_003L)])).ToArray();
        var batch = new FeatureBatch(schema, features);

        Assert.Equal(batch, FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batch)));
    }

    [Fact]
    public void Empty_batch_and_zero_field_schema_round_trip()
    {
        var schema = new FeatureSchema([]);
        var batch = new FeatureBatch(schema, [new Feature(new FeatureId("a"), schema, []), new Feature(new FeatureId("b"), schema, [])]);

        var decoded = FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batch));
        Assert.Equal(batch, decoded);
        Assert.Equal(2, decoded.Count);
        Assert.Equal(0, decoded.Schema.Count);
    }

    [Fact]
    public void Null_and_value_attributes_round_trip_together()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("a", AttributeKind.String, nullable: true),
            new FieldDefinition("b", AttributeKind.Int64),
        ]);
        var batch = new FeatureBatch(schema,
        [
            new Feature(new FeatureId("1"), schema, [AttributeValue.Null, AttributeValue.FromInt64(1)]),
            new Feature(new FeatureId("2"), schema, [AttributeValue.FromString("x"), AttributeValue.FromInt64(2)]),
            new Feature(new FeatureId("3"), schema, [AttributeValue.Null, AttributeValue.FromInt64(3)]),
        ]);

        var decoded = FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batch));
        Assert.Equal(batch, decoded);
        Assert.True(decoded[0]["a"].IsNull);
    }

    [Fact]
    public void Projection_decodes_a_prefix_schema()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("count", AttributeKind.Int64),
            new FieldDefinition("geom", AttributeKind.Geometry),
        ]);
        var batch = new FeatureBatch(schema,
        [
            new Feature(new FeatureId("f1"), schema,
                [AttributeValue.FromString("alpha"), AttributeValue.FromInt64(7), AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))]),
            new Feature(new FeatureId("f2"), schema,
                [AttributeValue.FromString("beta"), AttributeValue.FromInt64(8), AttributeValue.FromGeometry(GeometryFactory.CreateEmptyPoint())]),
        ]);
        var bytes = FeatureBatchCodec.Encode(batch);
        var target = new FeatureSchema([new FieldDefinition("name", AttributeKind.String)]);

        var projected = FeatureBatchCodec.Decode(bytes, target);

        Assert.Equal(target, projected.Schema);
        Assert.Equal(2, projected.Count);
        Assert.Equal("alpha", projected[0][0].StringValue);
        Assert.Equal("beta", projected[1]["name"].StringValue);
        Assert.Single(projected[0].Attributes);

        // The projected batch is itself a valid batch under the target schema.
        Assert.Equal(projected, FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(projected), target));
    }

    [Fact]
    public void Projection_with_identical_schema_equals_plain_decode()
    {
        var batch = AllKindsBatch();
        var bytes = FeatureBatchCodec.Encode(batch);

        Assert.Equal(FeatureBatchCodec.Decode(bytes), FeatureBatchCodec.Decode(bytes, batch.Schema));
    }

    [Fact]
    public void Projection_preserves_nulls_for_nullable_prefix_fields()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("a", AttributeKind.String, nullable: true),
            new FieldDefinition("b", AttributeKind.Int64),
        ]);
        var batch = new FeatureBatch(schema, [new Feature(new FeatureId("f"), schema, [AttributeValue.Null, AttributeValue.FromInt64(1)])]);
        var target = new FeatureSchema([new FieldDefinition("a", AttributeKind.String, nullable: true)]);

        var projected = FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batch), target);
        Assert.True(projected[0][0].IsNull);
    }

    [Fact]
    public void Projection_rejects_incompatible_targets()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("name", AttributeKind.String),
            new FieldDefinition("count", AttributeKind.Int64, nullable: true),
        ]);
        var batch = new FeatureBatch(schema, [new Feature(new FeatureId("f"), schema, [AttributeValue.FromString("a"), AttributeValue.Null])]);
        var bytes = FeatureBatchCodec.Encode(batch);

        var cases = new (string Fragment, FeatureSchema Target)[]
        {
            // Order matters: a reordered prefix does not match.
            ("reader expects 'count'", new FeatureSchema([new FieldDefinition("count", AttributeKind.Int64, nullable: true), new FieldDefinition("name", AttributeKind.String)])),
            // Kind changes are incompatible.
            ("reader kind", new FeatureSchema([new FieldDefinition("name", AttributeKind.Int64)])),
            // A null-producing writer cannot be read by a non-nullable reader.
            ("writer may produce null", new FeatureSchema([new FieldDefinition("name", AttributeKind.String), new FieldDefinition("count", AttributeKind.Int64)])),
        };

        foreach (var (fragment, target) in cases)
        {
            var exception = Assert.Throws<FeatureBatchFormatException>(() => FeatureBatchCodec.Decode(bytes, target));
            Assert.Contains("Cannot decode feature batch for the target schema", exception.Message);
            Assert.Contains(fragment, exception.Message);

            Assert.False(FeatureBatchCodec.TryDecode(bytes, target, out _, out var error));
            Assert.Contains(fragment, error);
        }
    }

    [Fact]
    public void Projection_rejects_wider_targets()
    {
        var schema = new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64)]);
        var batch = new FeatureBatch(schema, [new Feature(new FeatureId("f"), schema, [AttributeValue.FromInt64(1)])]);
        var wider = new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64), new FieldDefinition("b", AttributeKind.String)]);

        var exception = Assert.Throws<FeatureBatchFormatException>(() => FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batch), wider));
        Assert.Contains("appended", exception.Message);
    }

    [Fact]
    public void Projection_rejects_foreign_first_field_names()
    {
        var schema = new FeatureSchema([new FieldDefinition("a", AttributeKind.Geometry), new FieldDefinition("b", AttributeKind.String)]);
        var batch = new FeatureBatch(schema, [new Feature(new FeatureId("f"), schema, [AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0)), AttributeValue.FromString("x")])]);
        var target = new FeatureSchema([new FieldDefinition("nope", AttributeKind.Geometry)]);

        var exception = Assert.Throws<FeatureBatchFormatException>(() => FeatureBatchCodec.Decode(FeatureBatchCodec.Encode(batch), target));
        Assert.Contains("reader expects 'nope'", exception.Message);
    }

    // ----- malformed input -----

    [Fact]
    public void Decode_rejects_bad_magic_and_version()
    {
        AssertFailed([0x00, 0x00, 0x00, 0x00, 0x00, 0x00], "expected magic");
        AssertFailed([0x53, 0x46, 0x42, 0x41, 0x54, 0x02], "unsupported feature batch format version 2");
        AssertFailed(Header()[..5], "header is truncated");
        AssertFailed([], "header is truncated");
    }

    [Fact]
    public void Decode_rejects_invalid_schema_field_counts()
    {
        AssertFailed(Payload(Int32(-1)), "invalid schema field count");
        AssertFailed(Payload(Int32(1_000_000)), "only");
    }

    [Fact]
    public void Decode_rejects_invalid_kind_bytes()
    {
        var name = String("f");
        AssertFailed(Payload(Int32(1), name, [0x00], [0x00], [0x00]), "invalid kind byte 0");
        AssertFailed(Payload(Int32(1), name, [0x08], [0x00], [0x00]), "invalid kind byte 8");
        AssertFailed(Payload(Int32(1), name, [0xFF], [0x00], [0x00]), "invalid kind byte 255");
    }

    [Fact]
    public void Decode_rejects_invalid_flag_bytes()
    {
        var field = FieldChunks("f", AttributeKind.Int64);
        AssertFailed(Payload(Int32(1), field[0], field[1], [0x02], [0x00]), "invalid nullable byte");
        AssertFailed(Payload(Int32(1), field[0], field[1], [0x00], [0x02]), "invalid description presence byte");
    }

    [Fact]
    public void Decode_rejects_blank_and_duplicate_field_names()
    {
        AssertFailed(Payload(Int32(1), String("   "), Kind(AttributeKind.Int64), [0x00], [0x00]), "name must be a non-empty string");
        AssertFailed(Payload(Int32(1), String(""), Kind(AttributeKind.Int64), [0x00], [0x00]), "name must be a non-empty string");
        AssertFailed(Payload(Int32(2), String("a"), Kind(AttributeKind.Int64), [0x00], [0x00], String("a"), Kind(AttributeKind.String), [0x00], [0x00]), "duplicate field name 'a'");
    }

    [Fact]
    public void Decode_rejects_invalid_utf8_field_name()
    {
        AssertFailed(Payload(Int32(1), Concat(Int32(1), [0xFF]), Kind(AttributeKind.Int64), [0x00], [0x00]), "not valid UTF-8");
    }

    [Fact]
    public void Decode_rejects_truncated_schema()
    {
        // Name length 10 but only a handful of bytes follow.
        AssertFailed(Payload(Int32(1), Int32(10), [0x41, 0x42, 0x43, 0x44]), "field 0: name is truncated");
    }

    [Fact]
    public void Decode_rejects_invalid_feature_counts()
    {
        var intField = new FeatureSchema([new FieldDefinition("f", AttributeKind.Int64)]);
        var head = FeatureBatchCodec.Encode(new FeatureBatch(intField, []));

        AssertFailed(Concat(head[..^4], Int32(-1)), "invalid feature count");
        AssertFailed(Concat(head[..^4], Int32(1_000_000)), "only");
    }

    [Fact]
    public void Decode_rejects_blank_and_invalid_feature_ids()
    {
        AssertFailed(Payload(Int32(1), String("f"), Kind(AttributeKind.Int64), [0x00], [0x00], Int32(1), String("  "), [0x00], Int64(1)), "id must be a non-empty string");
        AssertFailed(Payload(Int32(1), String("f"), Kind(AttributeKind.Int64), [0x00], [0x00], Int32(1), Concat(Int32(1), [0xFF]), [0x00], Int64(1)), "not valid UTF-8");
    }

    [Fact]
    public void Decode_rejects_null_marker_misuse()
    {
        var nonNullable = new FeatureSchema([new FieldDefinition("n", AttributeKind.Int64)]);
        var batch = new FeatureBatch(nonNullable, [new Feature(new FeatureId("f"), nonNullable, [AttributeValue.FromInt64(1)])]);
        var bytes = FeatureBatchCodec.Encode(batch);

        AssertFailed(Concat(bytes[..^9], [0x02], Int64(1)), "invalid null marker");
        AssertFailed(Concat(bytes[..^9], [0x01], Int64(1)), "null marker on a non-nullable field");
    }

    [Fact]
    public void Decode_rejects_invalid_boolean_payload()
    {
        var field = FieldChunks("b", AttributeKind.Boolean);
        AssertFailed(Payload(Int32(1), field[0], field[1], [0x00], [0x00], Int32(1), String("f"), [0x00], [0x02]), "invalid boolean byte 2");
        AssertFailed(Payload(Int32(1), field[0], field[1], [0x00], [0x00], Int32(1), String("f"), [0x00]), "expected a boolean byte");
    }

    [Fact]
    public void Decode_rejects_truncated_payloads()
    {
        AssertFailed(FeaturePayload(AttributeKind.Int64, [0x00, 0x01]), "int64 payload is truncated");
        AssertFailed(FeaturePayload(AttributeKind.Double, [0x00, 0x01]), "double payload is truncated");
        AssertFailed(FeaturePayload(AttributeKind.DateTimeOffset, [0x00, 0x01]), "truncated or out of range");
        AssertFailed(FeaturePayload(AttributeKind.Guid, [0x00, 0x01]), "guid payload is truncated");
        AssertFailed(FeaturePayload(AttributeKind.String, Concat(Int32(5), [0x41])), "string payload is truncated");
    }

    [Fact]
    public void Decode_rejects_invalid_string_utf8_payload()
    {
        AssertFailed(FeaturePayload(AttributeKind.String, Concat(Int32(1), [0xFF])), "not valid UTF-8");
    }

    [Fact]
    public void Decode_rejects_invalid_geometry_payloads()
    {
        AssertFailed(FeaturePayload(AttributeKind.Geometry, Int32(100)), "invalid geometry payload length");
        AssertFailed(FeaturePayload(AttributeKind.Geometry, Int32(6), [0x53, 0x47, 0x45, 0x4F, 0x4D, 0x01]), "Invalid canonical geometry");
    }

    [Fact]
    public void Decode_rejects_out_of_range_date_time_payloads()
    {
        var valid = DateTimeOffset.UnixEpoch;
        AssertFailed(FeaturePayload(AttributeKind.DateTimeOffset, Int64(-1), Int16(0)), "truncated or out of range");
        AssertFailed(FeaturePayload(AttributeKind.DateTimeOffset, Int64(valid.UtcTicks), Int16(999)), "truncated or out of range");
        AssertFailed(FeaturePayload(AttributeKind.DateTimeOffset, Int64(valid.UtcTicks), Int16(-999)), "truncated or out of range");
        AssertFailed(FeaturePayload(AttributeKind.DateTimeOffset, Int64(DateTime.MaxValue.Ticks + 1), Int16(0)), "truncated or out of range");
    }

    [Fact]
    public void Decode_rejects_trailing_bytes()
    {
        var withTrailing = Concat(FeatureBatchCodec.Encode(AllKindsBatch()), [0x00]);
        AssertFailed(withTrailing, "trailing bytes");

        Assert.False(FeatureBatchCodec.TryDecode(withTrailing, out _, out var error));
        Assert.Contains("byte offset", error);
    }

    [Fact]
    public void Decode_error_messages_carry_byte_offsets()
    {
        var exception = Assert.Throws<FeatureBatchFormatException>(() => FeatureBatchCodec.Decode(Payload(Int32(1), String("f"), [0x63], [0x00])));
        Assert.Contains("byte offset", exception.Message);
        Assert.Contains("invalid kind byte", exception.Message);
    }

    [Fact]
    public void TryDecode_reports_errors_without_throwing()
    {
        Assert.False(FeatureBatchCodec.TryDecode([0x00], out _, out var error));
        Assert.Contains("byte offset 0", error);
    }

    // ----- fixtures -----

    private static TheoryData<string, FeatureBatch> BuildRoundTripCases()
    {
        var data = new TheoryData<string, FeatureBatch>();

        var crs = CoordinateReference.Epsg(4326);
        var geometries = new (string Name, IGeometry Geometry)[]
        {
            ("point xy", GeometryFactory.CreatePoint(1.5, -2.25)),
            ("point xyz crs", GeometryFactory.CreatePoint(1, 2, 3, crs)),
            ("empty xyz point", GeometryFactory.CreateEmptyPoint(layout: CoordinateLayout.Xyz)),
            ("line with nan z", GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(5, 5, Z: double.NaN)], CoordinateLayout.Xyz, crs)),
            ("polygon with hole", GeometryFactory.CreatePolygon(
                GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)]),
                [GeometryFactory.CreateLineString([new Coordinate(4, 4), new Coordinate(6, 4), new Coordinate(6, 6), new Coordinate(4, 6), new Coordinate(4, 4)])],
                crs)),
            ("geometry collection", GeometryFactory.CreateGeometryCollection(GeometryFactory.CreatePoint(9, 9), GeometryFactory.CreateEmptyPoint())),
        };

        data.Add("all kinds, multiple features", AllKindsBatch());

        foreach (var (name, geometry) in geometries)
        {
            data.Add($"geometry {name}", new FeatureBatch(AllKindsSchema(), [
                new Feature(new FeatureId("g"), AllKindsSchema(), SampleAllKinds(geometry)),
            ]));
        }

        data.Add("empty batch", new FeatureBatch(AllKindsSchema(), []));
        data.Add("zero-field schema", new FeatureBatch(new FeatureSchema([]), [new Feature(new FeatureId("only"), new FeatureSchema([]), [])]));

        return data;
    }

    private static FeatureSchema AllKindsSchema() => new(
    [
        new FieldDefinition("name", AttributeKind.String, description: "display name"),
        new FieldDefinition("active", AttributeKind.Boolean),
        new FieldDefinition("count", AttributeKind.Int64, nullable: true),
        new FieldDefinition("ratio", AttributeKind.Double),
        new FieldDefinition("geom", AttributeKind.Geometry),
        new FieldDefinition("when", AttributeKind.DateTimeOffset),
        new FieldDefinition("tag", AttributeKind.Guid),
    ]);

    private static AttributeValue[] SampleAllKinds(IGeometry geometry) =>
    [
        AttributeValue.FromString("alpha"),
        AttributeValue.FromBoolean(true),
        AttributeValue.Null,
        AttributeValue.FromDouble(double.NaN),
        AttributeValue.FromGeometry(geometry),
        AttributeValue.FromDateTimeOffset(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.FromHours(2))),
        AttributeValue.FromGuid(new Guid("12345678-1234-1234-1234-123456789abc")),
    ];

    private static FeatureBatch AllKindsBatch() => new(AllKindsSchema(),
    [
        new Feature(new FeatureId("f1"), AllKindsSchema(), SampleAllKinds(GeometryFactory.CreateGeometryCollection(GeometryFactory.CreatePoint(9, 9), GeometryFactory.CreateEmptyPoint()))),
        new Feature(new FeatureId("f2"), AllKindsSchema(),
        [
            AttributeValue.FromString("ßêta 🚀"),
            AttributeValue.FromBoolean(false),
            AttributeValue.Null,
            AttributeValue.FromDouble(-0.0),
            AttributeValue.FromGeometry(GeometryFactory.CreatePolygon(
                GeometryFactory.CreateLineString([new Coordinate(0, 0), new Coordinate(10, 0), new Coordinate(10, 10), new Coordinate(0, 10), new Coordinate(0, 0)]),
                [GeometryFactory.CreateLineString([new Coordinate(4, 4), new Coordinate(6, 4), new Coordinate(6, 6), new Coordinate(4, 6), new Coordinate(4, 4)])],
                CoordinateReference.Epsg(3857))),
            AttributeValue.FromDateTimeOffset(new DateTimeOffset(2020, 6, 15, 14, 30, 0, TimeSpan.FromHours(-5.5))),
            AttributeValue.FromGuid(Guid.Empty),
        ]),
    ]);

    private static byte[] FeaturePayload(AttributeKind kind, params byte[][] payloadChunks)
    {
        var schemaChunks = FieldChunks("f", kind);
        return Payload(Int32(1), schemaChunks[0], schemaChunks[1], [0x00], [0x00], Int32(1), String("f"), [0x00], Concat(payloadChunks));
    }

    private static byte[][] FieldChunks(string name, AttributeKind kind) => [String(name), Kind(kind)];

    private static byte[] Payload(params byte[][] chunks) => Concat(Header(), Concat(chunks));

    private static byte[] Header()
    {
        var bytes = new byte[FeatureBatchCodec.HeaderLength];
        FeatureBatchCodec.Magic.CopyTo(bytes);
        bytes[^1] = FeatureBatchCodec.FormatVersion;
        return bytes;
    }

    private static byte[] Concat(params byte[][] chunks) =>
        chunks.SelectMany(chunk => chunk).ToArray();

    private static byte[] Kind(AttributeKind kind) => [(byte)kind];

    private static byte[] String(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return Concat(Int32(bytes.Length), bytes);
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int64(long value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(bytes, value);
        return bytes;
    }

    private static void AssertFailed(byte[] bytes, string messageFragment)
    {
        Assert.False(FeatureBatchCodec.TryDecode(bytes, out _, out var error), $"Expected decode to fail for: {Convert.ToHexString(bytes)}");
        Assert.Contains(messageFragment, error);

        var exception = Assert.Throws<FeatureBatchFormatException>(() => FeatureBatchCodec.Decode(bytes));
        Assert.Contains(messageFragment, exception.Message);
    }
}
