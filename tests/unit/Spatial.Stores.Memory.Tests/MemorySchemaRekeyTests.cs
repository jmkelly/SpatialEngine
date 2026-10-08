using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The re-keying rule (ADR-0038): a source-identity ingest keys the stored
/// feature by its identity column's value rather than the decoded identity.
/// </summary>
public sealed class MemorySchemaRekeyTests
{
    private static Feature FeatureWith(FeatureSchema schema, string id, params AttributeValue[] values) =>
        new(new FeatureId(id), schema, values);

    [Fact]
    public void Rekey_returns_the_feature_unchanged_when_the_key_already_matches()
    {
        var schema = new FeatureSchema([new FieldDefinition("id", AttributeKind.Int64)]);
        var stored = FeatureWith(schema, "13", AttributeValue.FromInt64(13));

        Assert.Same(stored, MemorySchema.Rekey(stored, 0));
    }

    [Fact]
    public void Rekey_keys_an_int64_identity_column_by_its_value()
    {
        var schema = new FeatureSchema([new FieldDefinition("id", AttributeKind.Int64)]);
        var stored = FeatureWith(schema, "decode-7", AttributeValue.FromInt64(7));

        var rekeyed = MemorySchema.Rekey(stored, 0);

        Assert.Equal("7", rekeyed.Id.Value);
        Assert.Same(schema, rekeyed.Schema);
    }

    [Fact]
    public void Rekey_keys_a_string_identity_column_by_its_value()
    {
        var schema = new FeatureSchema([new FieldDefinition("code", AttributeKind.String)]);
        var stored = FeatureWith(schema, "decode-x", AttributeValue.FromString("ABC-1"));

        Assert.Equal("ABC-1", MemorySchema.Rekey(stored, 0).Id.Value);
    }

    [Fact]
    public void Rekey_keys_a_guid_identity_column_in_round_trip_form()
    {
        var guid = Guid.NewGuid();
        var schema = new FeatureSchema([new FieldDefinition("uid", AttributeKind.Guid)]);
        var stored = FeatureWith(schema, "decode-g", AttributeValue.FromGuid(guid));

        Assert.Equal(guid.ToString("D"), MemorySchema.Rekey(stored, 0).Id.Value);
    }

    [Fact]
    public void Rekey_rejects_an_identity_index_outside_the_schema()
    {
        var schema = new FeatureSchema([new FieldDefinition("id", AttributeKind.Int64)]);
        var stored = FeatureWith(schema, "13", AttributeValue.FromInt64(13));

        Assert.Throws<ArgumentOutOfRangeException>(() => MemorySchema.Rekey(stored, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MemorySchema.Rekey(stored, 1));
    }

    [Fact]
    public void Rekey_rejects_a_null_identity_value()
    {
        var schema = new FeatureSchema([new FieldDefinition("id", AttributeKind.Int64, nullable: true)]);
        var stored = FeatureWith(schema, "decode-n", AttributeValue.Null);

        var exception = Assert.Throws<SpatialException>(() => MemorySchema.Rekey(stored, 0));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public void Rekey_rejects_an_identity_column_whose_kind_cannot_key_a_feature()
    {
        var schema = new FeatureSchema([new FieldDefinition("score", AttributeKind.Double)]);
        var stored = FeatureWith(schema, "decode-d", AttributeValue.FromDouble(1.5));

        var exception = Assert.Throws<SpatialException>(() => MemorySchema.Rekey(stored, 0));
        Assert.Equal(SpatialException.InvalidArguments, exception.Code);
        Assert.Contains("score", exception.Message, StringComparison.Ordinal);
    }
}
