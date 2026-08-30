using Spatial.Core.Features;

namespace Spatial.Core.Tests;

/// <summary>
/// FieldDefinition and FeatureSchema: construction validation, the decodable
/// compatibility matrix (names, kinds, nullability, append-only prefix rule)
/// and content equality.
/// </summary>
public class FeatureSchemaTests
{
    [Fact]
    public void Field_definition_holds_name_kind_nullability_and_description()
    {
        var field = new FieldDefinition("name", AttributeKind.String, nullable: true, description: "the name");
        Assert.Equal("name", field.Name);
        Assert.Equal(AttributeKind.String, field.Kind);
        Assert.True(field.Nullable);
        Assert.Equal("the name", field.Description);
        Assert.False(new FieldDefinition("name", AttributeKind.String).Nullable);
        Assert.Null(new FieldDefinition("name", AttributeKind.String).Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Field_definition_rejects_blank_names(string name) =>
        Assert.Throws<ArgumentException>(() => new FieldDefinition(name, AttributeKind.String));

    [Fact]
    public void Field_definition_rejects_null_name() =>
        Assert.Throws<ArgumentNullException>(() => new FieldDefinition(null!, AttributeKind.String));

    [Fact]
    public void Field_definition_rejects_null_kind() =>
        Assert.Throws<ArgumentException>(() => new FieldDefinition("f", AttributeKind.Null));

    [Theory]
    [InlineData((AttributeKind)8)]
    [InlineData((AttributeKind)255)]
    public void Field_definition_rejects_unknown_kinds(AttributeKind kind) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new FieldDefinition("f", kind));

    [Fact]
    public void Field_definition_rejects_blank_description() =>
        Assert.Throws<ArgumentException>(() => new FieldDefinition("f", AttributeKind.String, description: " "));

    [Fact]
    public void Field_equality_includes_description()
    {
        var a = new FieldDefinition("f", AttributeKind.Double, nullable: true, description: "d");
        var b = new FieldDefinition("f", AttributeKind.Double, nullable: true, description: "d");
        var without = new FieldDefinition("f", AttributeKind.Double, nullable: true);
        Assert.Equal(a, b);
        Assert.NotEqual(a, without);
        Assert.NotEqual(a, new FieldDefinition("f", AttributeKind.Double));
    }

    [Fact]
    public void Field_is_decodable_from_matching_field()
    {
        var reader = new FieldDefinition("f", AttributeKind.Int64, nullable: true);
        Assert.True(reader.IsDecodableFrom(new FieldDefinition("f", AttributeKind.Int64, nullable: true)));
        // Reader that never sees nulls can read from a writer that may produce them.
        Assert.True(reader.IsDecodableFrom(new FieldDefinition("f", AttributeKind.Int64)));
    }

    [Theory]
    [InlineData("g", AttributeKind.Int64, false)] // name differs
    [InlineData("f", AttributeKind.Double, false)] // kind differs
    public void Field_is_not_decodable_on_mismatch(string name, AttributeKind kind, bool nullable)
    {
        var reader = new FieldDefinition("f", AttributeKind.Int64, nullable: true);
        Assert.False(reader.IsDecodableFrom(new FieldDefinition(name, kind, nullable)));
        // Narrowing nullability cannot decode a nullable writer.
        Assert.False(new FieldDefinition("f", AttributeKind.Int64).IsDecodableFrom(new FieldDefinition("f", AttributeKind.Int64, nullable: true)));
    }

    [Fact]
    public void Schema_rejects_duplicate_names()
    {
        var exception = Assert.Throws<ArgumentException>(() => new FeatureSchema(
        [
            new FieldDefinition("a", AttributeKind.Int64),
            new FieldDefinition("b", AttributeKind.String),
            new FieldDefinition("a", AttributeKind.Double),
        ]));
        Assert.Contains("'a'", exception.Message);
    }

    [Fact]
    public void Schema_names_are_case_sensitive()
    {
        var schema = new FeatureSchema([new FieldDefinition("A", AttributeKind.Int64)]);
        Assert.Equal(0, schema.IndexOf("A"));
        Assert.Equal(-1, schema.IndexOf("a"));
    }

    [Fact]
    public void Schema_orders_fields_and_looks_up_by_name()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.String),
            new FieldDefinition("geom", AttributeKind.Geometry),
        ]);

        Assert.Equal(2, schema.Count);
        Assert.Equal("id", schema[0].Name);
        Assert.Equal("geom", schema[1].Name);
        Assert.Equal(0, schema.IndexOf("id"));
        Assert.Equal(1, schema.IndexOf("geom"));
        Assert.Equal(-1, schema.IndexOf("missing"));
        Assert.Throws<ArgumentNullException>(() => schema.IndexOf(null!));
    }

    [Fact]
    public void Schema_equality_is_content_based_and_includes_descriptions()
    {
        var a = new FeatureSchema(
        [
            new FieldDefinition("f", AttributeKind.String, description: "d"),
            new FieldDefinition("g", AttributeKind.Double, nullable: true),
        ]);
        var b = new FeatureSchema(
        [
            new FieldDefinition("f", AttributeKind.String, description: "d"),
            new FieldDefinition("g", AttributeKind.Double, nullable: true),
        ]);
        var noDescription = new FeatureSchema(
        [
            new FieldDefinition("f", AttributeKind.String),
            new FieldDefinition("g", AttributeKind.Double, nullable: true),
        ]);
        var reordered = new FeatureSchema(
        [
            new FieldDefinition("g", AttributeKind.Double, nullable: true),
            new FieldDefinition("f", AttributeKind.String, description: "d"),
        ]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, noDescription);
        Assert.NotEqual(a, reordered);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Schema_is_decodable_matrix()
    {
        var reader = new FeatureSchema(SchemaFields("a", "b"));
        var writer = new FeatureSchema(SchemaFields("a", "b"));
        var appended = new FeatureSchema(SchemaFields("a", "b", "c"));

        // Exact and prefix compatibility.
        Assert.True(reader.IsDecodableFrom(writer));
        Assert.True(reader.IsDecodableFrom(appended));
        Assert.False(appended.IsDecodableFrom(reader)); // reader is narrower than writer's declared prefix

        // Order matters.
        var reordered = new FeatureSchema(SchemaFields("b", "a"));
        Assert.False(reader.IsDecodableFrom(reordered));
        Assert.False(reordered.IsDecodableFrom(writer));

        // Kind changes are incompatible.
        var kindChange = new FeatureSchema([new FieldDefinition("a", AttributeKind.String), new FieldDefinition("b", AttributeKind.Int64)]);
        Assert.False(reader.IsDecodableFrom(kindChange));
    }

    [Fact]
    public void Schema_nullability_matrix()
    {
        var nullableWriter = new FeatureSchema(
        [
            new FieldDefinition("a", AttributeKind.Int64, nullable: true),
            new FieldDefinition("b", AttributeKind.String),
        ]);
        var strictReader = new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64), new FieldDefinition("b", AttributeKind.String)]);

        Assert.False(strictReader.IsDecodableFrom(nullableWriter)); // writer may produce nulls the reader cannot hold
        Assert.True(new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64, nullable: true)]).IsDecodableFrom(nullableWriter));
        Assert.True(new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64, nullable: true)]).IsDecodableFrom(strictReader));
    }

    [Fact]
    public void Try_is_decodable_reports_actionable_reasons()
    {
        var reader = new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64)]);
        Assert.True(reader.TryIsDecodableFrom(new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64), new FieldDefinition("b", AttributeKind.String)]), out _));

        Assert.False(reader.TryIsDecodableFrom(new FeatureSchema([new FieldDefinition("x", AttributeKind.Int64)]), out var reason));
        Assert.Contains("field 0", reason);

        Assert.False(reader.TryIsDecodableFrom(new FeatureSchema([new FieldDefinition("a", AttributeKind.Double)]), out reason));
        Assert.Contains("kind", reason);

        Assert.False(reader.TryIsDecodableFrom(new FeatureSchema([new FieldDefinition("a", AttributeKind.Int64, nullable: true)]), out reason));
        Assert.Contains("null", reason);

        Assert.False(reader.TryIsDecodableFrom(new FeatureSchema([]), out reason));
        Assert.Contains("appended", reason);
    }

    [Fact]
    public void Empty_schema_is_decodable_from_any_schema()
    {
        var empty = new FeatureSchema([]);
        Assert.True(empty.IsDecodableFrom(new FeatureSchema(SchemaFields("a", "b", "c"))));
        Assert.True(empty.IsDecodableFrom(empty));
        Assert.False(new FeatureSchema(SchemaFields("a")).IsDecodableFrom(empty));
    }

    private static FieldDefinition[] SchemaFields(params string[] names) =>
        names.Select((name, i) => new FieldDefinition(name, i % 2 == 0 ? AttributeKind.Int64 : AttributeKind.String)).ToArray();
}
