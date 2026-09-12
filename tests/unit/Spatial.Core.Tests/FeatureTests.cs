using Spatial.Core.Features;
using Spatial.Core.Geometry;

namespace Spatial.Core.Tests;

/// <summary>
/// Feature and FeatureBatch construction: attribute count/kind/nullability
/// validation, defensive copies, exact batch-schema membership and content
/// equality.
/// </summary>
public class FeatureTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("name", AttributeKind.String),
        new FieldDefinition("count", AttributeKind.Int64, nullable: true),
        new FieldDefinition("geom", AttributeKind.Geometry),
    ]);

    private static readonly FeatureSchema OtherSchema = new([new FieldDefinition("name", AttributeKind.String)]);

    [Fact]
    public void Feature_builds_and_exposes_attributes()
    {
        var geometry = GeometryFactory.CreatePoint(1, 2);
        var feature = new Feature(
            new FeatureId("f1"),
            Schema,
            [AttributeValue.FromString("alpha"), AttributeValue.Null, AttributeValue.FromGeometry(geometry)]);

        Assert.Equal(new FeatureId("f1"), feature.Id);
        Assert.Same(Schema, feature.Schema);
        Assert.Equal(3, feature.Attributes.Count);
        Assert.Equal(AttributeValue.FromString("alpha"), feature[0]);
        Assert.Equal("alpha", feature["name"].StringValue);
        Assert.True(feature["count"].IsNull);
        Assert.Same(geometry, feature["geom"].GeometryValue);
    }

    [Fact]
    public void Feature_rejects_attribute_count_mismatch()
    {
        var exception = Assert.Throws<ArgumentException>(() => new Feature(
            new FeatureId("f1"),
            Schema,
            [AttributeValue.FromString("alpha")]));
        Assert.Contains("1 attributes", exception.Message);
        Assert.Contains("3 fields", exception.Message);
    }

    [Fact]
    public void Feature_rejects_kind_mismatch()
    {
        var exception = Assert.Throws<ArgumentException>(() => new Feature(
            new FeatureId("f1"),
            Schema,
            [AttributeValue.FromInt64(1), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0))]));
        Assert.Contains("'name'", exception.Message);
        Assert.Contains("Int64", exception.Message);
    }

    [Fact]
    public void Feature_rejects_null_in_non_nullable_field()
    {
        var exception = Assert.Throws<ArgumentException>(() => new Feature(
            new FeatureId("f1"),
            Schema,
            [AttributeValue.Null, AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0))]));
        Assert.Contains("non-nullable", exception.Message);
    }

    [Fact]
    public void Feature_accepts_null_in_nullable_field()
    {
        var feature = new Feature(
            new FeatureId("f1"),
            Schema,
            [AttributeValue.FromString("alpha"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0))]);
        Assert.True(feature[1].IsNull);
    }

    [Fact]
    public void Feature_copies_the_attribute_list()
    {
        var attributes = new List<AttributeValue> { AttributeValue.FromString("alpha"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0)) };
        var feature = new Feature(new FeatureId("f1"), Schema, attributes);
        attributes[0] = AttributeValue.FromString("mutated");

        Assert.Equal("alpha", feature[0].StringValue);
    }

    [Fact]
    public void Feature_unknown_name_throws_with_field_list()
    {
        var feature = SampleFeature();
        var exception = Assert.Throws<ArgumentException>(() => feature["missing"]);
        Assert.Contains("'missing'", exception.Message);
        Assert.Contains("'name'", exception.Message);
    }

    [Fact]
    public void Feature_equality_is_content_based()
    {
        var a = SampleFeature();
        var b = new Feature(
            new FeatureId("f1"),
            Schema,
            [AttributeValue.FromString("alpha"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.True(a.Equals((object)b));
        Assert.False(a.Equals((object)"not a feature"));
        Assert.NotEqual(a, new Feature(
            new FeatureId("f2"),
            Schema,
            [AttributeValue.FromString("alpha"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))]));
        Assert.NotEqual(a, new Feature(
            new FeatureId("f1"),
            Schema,
            [AttributeValue.FromString("beta"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))]));
        Assert.NotEqual(a, new Feature(
            new FeatureId("f1"),
            OtherSchema,
            [AttributeValue.FromString("alpha")]));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Feature_id_rejects_blank_values()
    {
        Assert.Throws<ArgumentException>(() => new FeatureId(""));
        Assert.Throws<ArgumentException>(() => new FeatureId("   "));
        Assert.Throws<ArgumentNullException>(() => new FeatureId(null!));
    }

    [Fact]
    public void Feature_id_is_a_structural_value()
    {
        Assert.Equal(new FeatureId("abc"), new FeatureId("abc"));
        Assert.Equal(new FeatureId("abc").GetHashCode(), new FeatureId("abc").GetHashCode());
        Assert.NotEqual(new FeatureId("abc"), new FeatureId("ABC"));
        Assert.Equal("abc", new FeatureId("abc").ToString());
    }

    [Fact]
    public void Batch_requires_the_exact_schema()
    {
        var matching = new Feature(new FeatureId("f1"), Schema, [AttributeValue.FromString("a"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(0, 0))]);
        var batch = new FeatureBatch(Schema, [matching]);
        Assert.Equal(1, batch.Count);
        Assert.Same(matching, batch[0]);

        // An equal-but-distinct schema instance is accepted (content equality).
        var equivalent = new FeatureBatch(new FeatureSchema(Schema.Fields), [matching]);
        Assert.Equal(1, equivalent.Count);

        var foreign = new Feature(new FeatureId("f2"), OtherSchema, [AttributeValue.FromString("b")]);
        var exception = Assert.Throws<ArgumentException>(() => new FeatureBatch(Schema, [matching, foreign]));
        Assert.Contains("does not match the batch schema", exception.Message);
    }

    [Fact]
    public void Batch_rejects_null_features() =>
        Assert.Throws<ArgumentException>(() => new FeatureBatch(Schema, [null!]));

    [Fact]
    public void Batch_copies_the_feature_list()
    {
        var features = new List<Feature> { SampleFeature() };
        var batch = new FeatureBatch(Schema, features);
        features.Clear();
        Assert.Equal(1, batch.Count);
    }

    [Fact]
    public void Batch_equality_is_content_based()
    {
        var a = new FeatureBatch(Schema, [SampleFeature(), SampleFeature()]);
        var b = new FeatureBatch(new FeatureSchema(Schema.Fields), [SampleFeature(), SampleFeature()]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());

        var one = new FeatureBatch(Schema, [SampleFeature()]);
        var differentId = new FeatureBatch(Schema, [new Feature(
            new FeatureId("other"),
            Schema,
            [AttributeValue.FromString("alpha"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))]), SampleFeature()]);
        var differentSchema = new FeatureBatch(OtherSchema, [new Feature(new FeatureId("f1"), OtherSchema, [AttributeValue.FromString("alpha")])]);
        Assert.NotEqual(a, one);
        Assert.NotEqual(a, differentId);
        Assert.NotEqual(a, differentSchema);
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Empty_batch_is_valid() =>
        Assert.Equal(0, new FeatureBatch(Schema, []).Count);

    private static Feature SampleFeature() => new(
        new FeatureId("f1"),
        Schema,
        [AttributeValue.FromString("alpha"), AttributeValue.Null, AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 2))]);
}
