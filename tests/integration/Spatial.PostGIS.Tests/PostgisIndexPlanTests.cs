using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The index plan a created PostGIS dataset is given (ADR-0092), tested as
/// pure planning: the statements are the store's own, and they are built
/// without a database, so a shape that would be rejected by the server — a
/// name over the identifier limit, a column the table does not have, a
/// primary key indexed twice — is caught here rather than by a container.
/// </summary>
public sealed class PostgisIndexPlanTests
{
    private static FeatureSchema Schema(params (string Name, AttributeKind Kind)[] fields) =>
        new(fields.Select(field => new FieldDefinition(field.Name, field.Kind, nullable: false)));

    private static PostgisDatasetName Dataset() => Parse("public.cities");

    private static PostgisDatasetName Parse(string dataset)
    {
        Assert.True(PostgisDatasetName.TryParse(dataset, out var name, out var reason), reason);
        return name;
    }

    [Fact]
    public void A_schema_gets_a_gist_index_on_its_geometry_and_a_btree_on_every_attribute()
    {
        var statements = PostgisIndexPlan.CreateIndexes(
            Dataset(), Schema(("name", AttributeKind.String), ("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry)));

        Assert.Equal(
            [
                "CREATE INDEX \"ix_cities_geom\" ON \"public\".\"cities\" USING GIST (\"geom\")",
                "CREATE INDEX \"ix_cities_name\" ON \"public\".\"cities\" (\"name\")",
                "CREATE INDEX \"ix_cities_population\" ON \"public\".\"cities\" (\"population\")",
            ],
            statements);
    }

    [Fact]
    public void The_first_geometry_field_in_column_order_is_the_one_indexed()
    {
        var schema = Schema(("bounds", AttributeKind.Geometry), ("centroid", AttributeKind.Geometry));

        var statement = Assert.Single(PostgisIndexPlan.CreateIndexes(Dataset(), schema));

        Assert.Contains("USING GIST (\"bounds\")", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_schema_without_a_geometry_gets_no_spatial_index()
    {
        var statements = PostgisIndexPlan.CreateIndexes(Dataset(), Schema(("name", AttributeKind.String)));

        Assert.DoesNotContain(statements, statement => statement.Contains("GIST", StringComparison.Ordinal));
        Assert.Single(statements);
    }

    [Fact]
    public void A_column_the_primary_key_already_indexes_is_not_indexed_again()
    {
        var schema = Schema(("code", AttributeKind.Int64), ("geom", AttributeKind.Geometry));

        var statements = PostgisIndexPlan.CreateIndexes(Dataset(), schema, ["code"]);

        Assert.DoesNotContain(statements, statement => statement.Contains("\"code\"", StringComparison.Ordinal));
        Assert.Single(statements);
    }

    [Fact]
    public void Every_identifier_is_quoted_rather_than_interpolated()
    {
        var schema = Schema(("from", AttributeKind.Int64), ("geom", AttributeKind.Geometry));

        var statement = Assert.Single(
            PostgisIndexPlan.CreateIndexes(Dataset(), schema),
            candidate => candidate.Contains("from", StringComparison.Ordinal));

        Assert.Contains("(\"from\")", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_name_that_would_exceed_the_identifier_limit_is_shortened_deterministically()
    {
        var column = new string('c', 60);

        var name = PostgisIndexName.For(new string('t', 60), column);

        Assert.True(System.Text.Encoding.UTF8.GetByteCount(name) <= 63, $"{name} is {System.Text.Encoding.UTF8.GetByteCount(name)} bytes");
        Assert.Equal(name, PostgisIndexName.For(new string('t', 60), column));
    }

    [Fact]
    public void Two_long_names_that_share_a_prefix_stay_distinct()
    {
        var prefix = new string('t', 60);

        Assert.NotEqual(
            PostgisIndexName.For(prefix, new string('c', 60) + "a"),
            PostgisIndexName.For(prefix, new string('c', 60) + "b"));
    }

    [Fact]
    public void A_multibyte_name_is_cut_on_a_character_boundary()
    {
        // A table name of multibyte characters puts the 54-byte cut in the
        // middle of the name, where a byte-wise cut would split a character and
        // emit an identifier the server cannot store.
        var name = PostgisIndexName.For(new string('é', 40), new string('c', 30));

        Assert.Equal(62, System.Text.Encoding.UTF8.GetByteCount(name));
        Assert.Equal("ix_" + new string('é', 25), name[..28]);
        Assert.DoesNotContain('�', name);
    }
}
