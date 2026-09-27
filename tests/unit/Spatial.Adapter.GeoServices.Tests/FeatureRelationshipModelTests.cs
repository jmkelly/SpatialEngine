using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// Pins the relationship projection and traversal vocabulary (ADR-0077): the
/// engine cardinality mapped onto the Esri relationship types, the advertised
/// metadata shape, the key terms the traversal filters the related layer
/// with, and the <c>relationshipId</c> resolution. Everything here is pure, so
/// it is pinned without a store; the served shapes are pinned end-to-end in
/// the host suite.
/// </summary>
public sealed class FeatureRelationshipModelTests
{
    private static readonly IReadOnlyDictionary<int, string> RelatedNames = new Dictionary<int, string> { [1] = "children" };

    [Theory]
    [InlineData(LayerRelationshipCardinality.OneToOne, "esriRelationshipTypeOneToOne")]
    [InlineData(LayerRelationshipCardinality.OneToMany, "esriRelationshipTypeOneToMany")]
    [InlineData(LayerRelationshipCardinality.ManyToMany, "esriRelationshipTypeManyToMany")]
    public void Describe_maps_the_engine_cardinality_onto_the_esri_type(LayerRelationshipCardinality cardinality, string expected)
    {
        var relationship = new LayerRelationship("children", 1, "id", "parent_id", cardinality);

        Assert.Equal(expected, Assert.Single(EsriRelationshipModel.Describe([relationship], RelatedNames)).Type);
    }

    [Fact]
    public void Describe_advertises_the_declared_name_as_the_relationship_id()
    {
        var relationship = new LayerRelationship("children", 1, "id", "parent_id");

        var described = Assert.Single(EsriRelationshipModel.Describe([relationship], RelatedNames));

        Assert.Equal("children", described.Id);
        Assert.Equal("children", described.Name);
        Assert.Equal(1, described.RelatedLayerId);
    }

    [Fact]
    public void Describe_titles_a_relationship_with_its_declared_title_field()
    {
        var relationship = new LayerRelationship("children", 1, "id", "parent_id", TitleField: "label");

        Assert.Equal("label", Assert.Single(EsriRelationshipModel.Describe([relationship], RelatedNames)).Title);
    }

    [Fact]
    public void Describe_titles_a_relationship_with_the_related_layer_name_when_none_is_declared()
    {
        var relationship = new LayerRelationship("children", 1, "id", "parent_id");

        Assert.Equal("children", Assert.Single(EsriRelationshipModel.Describe([relationship], RelatedNames)).Title);
    }

    [Fact]
    public void Describe_omits_the_relationships_key_for_a_layer_that_declares_none()
    {
        var dataset = new DatasetDescription(
            "memory.places", "memory", "places", "geometry", 4326, "Point", 0, [],
            new FeatureSchema([new FieldDefinition("geometry", AttributeKind.Geometry)]));

        var layer = EsriLayerModel.Describe(0, dataset, editable: false, relationships: EsriRelationshipModel.Describe([], RelatedNames));

        Assert.Null(layer.Relationships);
    }

    [Fact]
    public void Describe_advertises_the_relationships_of_a_layer_that_declares_them()
    {
        var dataset = new DatasetDescription(
            "memory.places", "memory", "places", "geometry", 4326, "Point", 0, [],
            new FeatureSchema([new FieldDefinition("geometry", AttributeKind.Geometry)]));
        var declared = new[] { new LayerRelationship("children", 1, "id", "parent_id") };

        var layer = EsriLayerModel.Describe(0, dataset, editable: false, relationships: EsriRelationshipModel.Describe(declared, RelatedNames));

        Assert.Equal("children", Assert.Single(layer.Relationships!).Name);
    }

    // ---- key terms ----

    [Fact]
    public void Equality_renders_an_integer_key_as_a_where_term()
    {
        Assert.Equal("parent_id = 42", FeatureRelationshipKeys.Equality("parent_id", AttributeValue.FromInt64(42)));
    }

    [Fact]
    public void Equality_renders_a_text_key_with_its_quotes_doubled()
    {
        Assert.Equal("parent_id = 'O''Brien'", FeatureRelationshipKeys.Equality("parent_id", AttributeValue.FromString("O'Brien")));
    }

    [Fact]
    public void Equality_renders_a_guid_key_in_its_canonical_form()
    {
        var id = Guid.Parse("11112222-3333-4444-5555-666677778888");

        Assert.Equal("parent_id = '11112222-3333-4444-5555-666677778888'", FeatureRelationshipKeys.Equality("parent_id", AttributeValue.FromGuid(id)));
    }

    [Fact]
    public void Equality_renders_a_boolean_key_as_a_keyword()
    {
        Assert.Equal("active = TRUE", FeatureRelationshipKeys.Equality("active", AttributeValue.FromBoolean(true)));
    }

    [Fact]
    public void Equality_is_nothing_for_a_null_key()
    {
        Assert.Null(FeatureRelationshipKeys.Equality("parent_id", AttributeValue.Null));
    }

    [Fact]
    public void Equality_rejects_a_key_kind_with_no_literal()
    {
        Assert.ThrowsAny<Exception>(() => FeatureRelationshipKeys.Equality(
            "seen", AttributeValue.FromDateTimeOffset(DateTimeOffset.UnixEpoch)));
    }

    [Fact]
    public void Conjoin_joins_a_composite_key_with_and()
    {
        var term = FeatureRelationshipKeys.Conjoin(
            FeatureRelationshipKeys.Equality("a", AttributeValue.FromInt64(1)),
            null,
            FeatureRelationshipKeys.Equality("b", AttributeValue.FromInt64(2)));

        Assert.Equal("a = 1 AND b = 2", term);
    }

    [Fact]
    public void Disjoin_joins_many_to_many_keys_with_or()
    {
        var term = FeatureRelationshipKeys.Disjoin(
        [
            FeatureRelationshipKeys.Equality("child_id", AttributeValue.FromInt64(1)),
            FeatureRelationshipKeys.Equality("child_id", AttributeValue.FromInt64(2)),
        ]);

        Assert.Equal("(child_id = 1) OR (child_id = 2)", term);
    }

    [Fact]
    public void Disjoin_of_no_keys_is_the_constant_false_term()
    {
        Assert.Equal("1 = 0", FeatureRelationshipKeys.Disjoin([]));
    }

    [Fact]
    public void Parse_builds_a_clause_that_matches_the_key_it_rendered()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("parent_id", AttributeKind.Int64),
            new FieldDefinition("name", AttributeKind.String, nullable: true),
        ]);
        var feature = new Feature(
            new FeatureId("1"), schema, [AttributeValue.FromInt64(42), AttributeValue.FromString("child")]);

        var clause = FeatureRelationshipKeys.Parse(FeatureRelationshipKeys.Equality("parent_id", feature[0])!);

        Assert.True(EsriPredicateEvaluator.Matches(clause.Predicate, feature));
        Assert.False(EsriPredicateEvaluator.Matches(clause.Predicate, new Feature(new FeatureId("2"), schema, [AttributeValue.FromInt64(7), AttributeValue.FromString("other")])));
    }

    [Fact]
    public void Parse_rejects_a_term_the_grammar_cannot_express()
    {
        Assert.ThrowsAny<Exception>(() => FeatureRelationshipKeys.Parse("parent_id = 1 AND"));
    }

    // ---- relationshipId resolution ----

    private static readonly IReadOnlyList<LayerRelationship> Declared =
    [
        new("children", 1, "id", "parent_id"),
        new("tags", 2, "id", "tag_id", LayerRelationshipCardinality.ManyToMany,
            Join: new LayerRelationshipJoin("memory.parent_tags", "parent_id", "tag_id")),
    ];

    [Fact]
    public void Select_resolves_a_declaration_by_its_name()
    {
        Assert.Equal("tags", FeatureRelationshipTargets.Select(Declared, 0, "tags").Name);
    }

    [Fact]
    public void Select_resolves_a_declaration_by_its_position()
    {
        Assert.Equal("tags", FeatureRelationshipTargets.Select(Declared, 0, "2").Name);
    }

    [Fact]
    public void Select_takes_the_only_declaration_when_no_id_is_given()
    {
        Assert.Equal("children", FeatureRelationshipTargets.Select([Declared[0]], 0, null).Name);
    }

    [Fact]
    public void Select_requires_an_id_when_a_layer_declares_several()
    {
        var failure = Assert.ThrowsAny<Exception>(() => FeatureRelationshipTargets.Select(Declared, 0, null));

        Assert.Contains("children", failure.Message, StringComparison.Ordinal);
        Assert.Contains("tags", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_reports_a_layer_that_declares_nothing()
    {
        var failure = Assert.ThrowsAny<Exception>(() => FeatureRelationshipTargets.Select([], 3, "children"));

        Assert.Contains("Layer 3 declares no relationship.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_reports_an_id_that_names_no_declaration()
    {
        var failure = Assert.ThrowsAny<Exception>(() => FeatureRelationshipTargets.Select(Declared, 0, "nope"));

        Assert.Contains("'relationshipId' parameter is required", failure.Message, StringComparison.Ordinal);
    }
}
