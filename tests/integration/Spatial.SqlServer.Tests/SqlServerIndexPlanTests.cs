using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The index plan a created SQL Server dataset is given (ADR-0092), tested as
/// pure planning: the statements are the store's own and are built without a
/// server, so the two limits the server puts on them — a spatial index needs a
/// clustered primary key, and a <c>nvarchar(max)</c> column cannot be an index
/// key — are asserted here rather than discovered as a failed
/// <c>CREATE INDEX</c> against a container.
/// </summary>
public sealed class SqlServerIndexPlanTests
{
    private static FeatureSchema Schema(params (string Name, AttributeKind Kind)[] fields) =>
        new(fields.Select(field => new FieldDefinition(field.Name, field.Kind, nullable: false)));

    private static SqlServerDatasetName Dataset() => Parse("dbo.cities");

    private static SqlServerDatasetName Parse(string dataset)
    {
        Assert.True(SqlServerDatasetName.TryParse(dataset, out var name, out var reason), reason);
        return name;
    }

    [Fact]
    public void A_keyed_schema_gets_a_spatial_index_and_a_btree_on_every_indexable_attribute()
    {
        var statements = SqlServerIndexPlan.CreateIndexes(
            Dataset(),
            Schema(("population", AttributeKind.Int64), ("ratio", AttributeKind.Double), ("geom", AttributeKind.Geometry)),
            ["id"],
            clusteredPrimaryKey: true);

        Assert.Equal(
            [
                "CREATE SPATIAL INDEX [ix_cities_geom] ON [dbo].[cities] ([geom]) USING GEOMETRY_AUTO_GRID "
                    + "WITH (BOUNDING_BOX = (-180, -90, 180, 90), CELLS_PER_OBJECT = 16)",
                "CREATE INDEX [ix_cities_population] ON [dbo].[cities] ([population])",
                "CREATE INDEX [ix_cities_ratio] ON [dbo].[cities] ([ratio])",
            ],
            statements);
    }

    [Fact]
    public void A_text_column_is_not_offered_a_btree()
    {
        var statements = SqlServerIndexPlan.CreateIndexes(
            Dataset(), Schema(("name", AttributeKind.String), ("geom", AttributeKind.Geometry)), ["id"], clusteredPrimaryKey: true);

        Assert.DoesNotContain(statements, statement => statement.Contains("[name]", StringComparison.Ordinal));
    }

    [Fact]
    public void A_table_with_no_clustered_primary_key_is_not_gridded()
    {
        var statements = SqlServerIndexPlan.CreateIndexes(
            Dataset(), Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry)));

        Assert.DoesNotContain(statements, statement => statement.Contains("SPATIAL INDEX", StringComparison.Ordinal));
        Assert.Single(statements);
    }

    [Fact]
    public void The_first_geometry_field_in_column_order_is_the_one_gridded()
    {
        var schema = Schema(("bounds", AttributeKind.Geometry), ("centroid", AttributeKind.Geometry));

        var statement = Assert.Single(
            SqlServerIndexPlan.CreateIndexes(Dataset(), schema, ["id"], clusteredPrimaryKey: true),
            candidate => candidate.Contains("SPATIAL INDEX", StringComparison.Ordinal));

        Assert.Contains("([bounds])", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_column_the_primary_key_already_indexes_is_not_indexed_again()
    {
        var statements = SqlServerIndexPlan.CreateIndexes(
            Dataset(), Schema(("code", AttributeKind.Int64), ("geom", AttributeKind.Geometry)), ["code"], clusteredPrimaryKey: true);

        Assert.DoesNotContain(statements, statement => statement.Contains("ix_cities_code", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_identifier_is_bracket_quoted_rather_than_interpolated()
    {
        var schema = Schema(("from", AttributeKind.Int64), ("geom", AttributeKind.Geometry));

        var statement = Assert.Single(
            SqlServerIndexPlan.CreateIndexes(Dataset(), schema, ["id"], clusteredPrimaryKey: true),
            candidate => candidate.Contains("from", StringComparison.Ordinal));

        Assert.Contains("([from])", statement, StringComparison.Ordinal);
    }

    [Fact]
    public void An_index_name_that_would_exceed_the_identifier_limit_is_shortened_deterministically()
    {
        var column = new string('c', 120);

        var name = SqlServerIndexName.For(new string('t', 120), column);

        Assert.Equal(128, name.Length);
        Assert.Equal(name, SqlServerIndexName.For(new string('t', 120), column));
    }

    [Fact]
    public void Two_long_names_that_share_a_prefix_stay_distinct()
    {
        var prefix = new string('t', 120);

        Assert.NotEqual(
            SqlServerIndexName.For(prefix, new string('c', 120) + "a"),
            SqlServerIndexName.For(prefix, new string('c', 120) + "b"));
    }
}
