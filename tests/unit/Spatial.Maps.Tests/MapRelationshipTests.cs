using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Maps;

namespace Spatial.Maps.Tests;

/// <summary>
/// Pins the relationship declaration rules (ADR-0077): the structural half in
/// <see cref="MapValidator"/> (name, target, columns, cardinality/join
/// pairing) and the live half in <see cref="MapRelationshipSchemas"/> (the
/// named datasets and columns exist, and both sides carry the same key kind).
/// </summary>
public sealed class MapRelationshipTests
{
    private static Map TwoLayers(IReadOnlyList<LayerRelationship> relationships, string name = "related") => new(
        name,
        "memory",
        [
            new MapLayer("memory.parents", 0, Relationships: relationships),
            new MapLayer("memory.children", 1),
        ],
        [MapServiceKind.FeatureServer]);

    private static DatasetDescription Description(string id, params FieldDefinition[] fields) => new(
        id, "memory", id[(id.IndexOf('.') + 1)..], "geometry", 4326, "Point", 1, [], new FeatureSchema([.. fields, new FieldDefinition("geometry", AttributeKind.Geometry)]));

    private static Task ValidateAsync(Map map, params (string Dataset, FieldDefinition[] Fields)[] schemas) =>
        MapRelationshipSchemas.ValidateAsync(
            map,
            (store, dataset, cancellationToken) => Task.FromResult(
                schemas.FirstOrDefault(schema => schema.Dataset == dataset) is { Dataset: not null } found
                    ? Description(dataset, found.Fields)
                    : throw SpatialException.Missing($"Dataset '{dataset}' does not exist.")));

    // ---- structural ----

    [Fact]
    public void Normalize_keeps_a_well_formed_declaration()
    {
        var map = TwoLayers([new LayerRelationship("children", 1, "id", "parent_id")]);

        var normalised = MapValidator.Normalize(map, nextLayerId: 0);

        var relationship = Assert.Single(normalised.Layers[0].Relationships!);
        Assert.Equal("children", relationship.Name);
        Assert.Equal(LayerRelationshipCardinality.OneToMany, relationship.Cardinality);
    }

    [Fact]
    public void Normalize_rejects_a_relationship_naming_a_layer_the_map_does_not_publish()
    {
        var map = TwoLayers([new LayerRelationship("children", 7, "id", "parent_id")]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("targets layer 7", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rejects_a_duplicate_relationship_name_on_one_layer()
    {
        var map = TwoLayers(
        [
            new LayerRelationship("children", 1, "id", "parent_id"),
            new LayerRelationship("children", 1, "id", "other"),
        ]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("more than once", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rejects_a_column_name_that_is_not_an_identifier()
    {
        var map = TwoLayers([new LayerRelationship("children", 1, "id", "parent_id) OR (1=1")]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("expected an identifier", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rejects_many_to_many_without_a_join_dataset()
    {
        var map = TwoLayers([new LayerRelationship("tags", 1, "id", "parent_id", LayerRelationshipCardinality.ManyToMany)]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("needs a join dataset", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_rejects_a_one_to_many_that_names_a_join_dataset()
    {
        var map = TwoLayers(
        [
            new LayerRelationship(
                "tags", 1, "id", "parent_id",
                Join: new LayerRelationshipJoin("memory.parent_tags", "parent_id", "child_id")),
        ]);

        var failure = Assert.Throws<SpatialException>(() => MapValidator.Normalize(map, nextLayerId: 0));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("cannot name the join dataset", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_accepts_many_to_many_with_a_join_dataset()
    {
        var map = TwoLayers(
        [
            new LayerRelationship(
                "tags", 1, "id", "parent_id", LayerRelationshipCardinality.ManyToMany,
                Join: new LayerRelationshipJoin("memory.parent_tags", "parent_id", "child_id")),
        ]);

        var normalised = MapValidator.Normalize(map, nextLayerId: 0);

        Assert.Equal("memory.parent_tags", Assert.Single(normalised.Layers[0].Relationships!).Join!.Dataset);
    }

    // ---- live schemas ----

    [Fact]
    public async Task Validate_accepts_a_declaration_over_existing_columns()
    {
        var map = TwoLayers([new LayerRelationship("children", 1, "id", "parent_id")]);

        await ValidateAsync(
            map,
            ("memory.parents", [new FieldDefinition("id", AttributeKind.Int64)]),
            ("memory.children", [new FieldDefinition("id", AttributeKind.Int64), new FieldDefinition("parent_id", AttributeKind.Int64)]));
    }

    [Fact]
    public async Task Validate_rejects_a_related_column_the_dataset_does_not_have()
    {
        var map = TwoLayers([new LayerRelationship("children", 1, "id", "parent_id")]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => ValidateAsync(
            map,
            ("memory.parents", [new FieldDefinition("id", AttributeKind.Int64)]),
            ("memory.children", [new FieldDefinition("id", AttributeKind.Int64)])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("'parent_id'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_rejects_a_key_column_the_related_dataset_does_not_have()
    {
        var map = TwoLayers([new LayerRelationship("children", 1, "missing", "parent_id")]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => ValidateAsync(
            map,
            ("memory.parents", [new FieldDefinition("id", AttributeKind.Int64)]),
            ("memory.children", [new FieldDefinition("parent_id", AttributeKind.Int64)])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("'missing'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_rejects_sides_whose_key_kinds_do_not_match()
    {
        var map = TwoLayers([new LayerRelationship("children", 1, "id", "parent_id")]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => ValidateAsync(
            map,
            ("memory.parents", [new FieldDefinition("id", AttributeKind.String)]),
            ("memory.children", [new FieldDefinition("parent_id", AttributeKind.Int64)])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("must carry the same kind", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_rejects_a_geometry_key_column()
    {
        var map = TwoLayers([new LayerRelationship("children", 1, "geometry", "parent_id")]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => ValidateAsync(
            map,
            ("memory.parents", []),
            ("memory.children", [new FieldDefinition("parent_id", AttributeKind.Int64)])));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("cannot be a relationship key", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_reports_a_join_dataset_that_does_not_exist()
    {
        var map = TwoLayers(
        [
            new LayerRelationship(
                "tags", 1, "id", "parent_id", LayerRelationshipCardinality.ManyToMany,
                Join: new LayerRelationshipJoin("memory.parent_tags", "parent_id", "child_id")),
        ]);

        var failure = await Assert.ThrowsAsync<SpatialException>(() => ValidateAsync(
            map,
            ("memory.parents", [new FieldDefinition("id", AttributeKind.Int64)]),
            ("memory.children", [new FieldDefinition("parent_id", AttributeKind.Int64)])));

        Assert.Equal(SpatialException.NotFound, failure.Code);
    }

    [Fact]
    public async Task Validate_accepts_a_many_to_many_join_whose_key_kinds_line_up()
    {
        var map = TwoLayers(
        [
            new LayerRelationship(
                "tags", 1, "id", "parent_id", LayerRelationshipCardinality.ManyToMany,
                Join: new LayerRelationshipJoin("memory.parent_tags", "parent_id", "child_id")),
        ]);

        await ValidateAsync(
            map,
            ("memory.parents", [new FieldDefinition("id", AttributeKind.Int64)]),
            ("memory.children", [new FieldDefinition("parent_id", AttributeKind.Int64)]),
            ("memory.parent_tags", [new FieldDefinition("parent_id", AttributeKind.Int64), new FieldDefinition("child_id", AttributeKind.Int64)]));
    }

    [Fact]
    public async Task Validate_does_not_ask_the_catalogue_for_a_map_without_relationships()
    {
        var asked = false;

        await MapRelationshipSchemas.ValidateAsync(
            TwoLayers([]),
            (store, dataset, cancellationToken) =>
            {
                asked = true;
                return Task.FromResult(Description(dataset));
            });

        Assert.False(asked);
    }

    [Fact]
    public async Task Validate_observes_cancellation_before_the_first_lookup()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MapRelationshipSchemas.ValidateAsync(
            TwoLayers([new LayerRelationship("children", 1, "id", "parent_id")]),
            (store, dataset, token) => Task.FromResult(Description(dataset)),
            cancellation.Token));
    }
}
