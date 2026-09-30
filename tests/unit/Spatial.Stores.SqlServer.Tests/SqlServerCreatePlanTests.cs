using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The plan a created SQL Server dataset is created with (ADR-0147): SQL Server
/// will not grid a table that has no clustered primary key, so the create
/// statement carries the engine's own key — and the contract never sees it, so
/// a created dataset is the keyless dataset it has always been. Pure planning,
/// no server, so both halves are asserted here rather than discovered as a
/// missing spatial index against a container.
/// </summary>
public sealed class SqlServerCreatePlanTests
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
    public void A_created_dataset_carries_the_clustered_key_the_spatial_index_needs()
    {
        var plan = SqlServerCreatePlan.Create(Dataset(), Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry)), true);

        Assert.Equal(
            "CREATE TABLE [dbo].[cities] ([population] bigint, [geom] geometry, "
                + "[id] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [spatial_key_cities] PRIMARY KEY)",
            plan.CreateTableSql());
        Assert.Equal("id", plan.IdentityColumn);
    }

    [Fact]
    public void The_engine_key_is_what_makes_the_dataset_gridable()
    {
        var plan = SqlServerCreatePlan.Create(Dataset(), Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry)), true);

        Assert.Contains(plan.CreateIndexSql(), statement => statement.Contains("CREATE SPATIAL INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void The_engine_key_is_never_indexed_a_second_time()
    {
        var plan = SqlServerCreatePlan.Create(Dataset(), Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry)), true);

        Assert.DoesNotContain(
            plan.CreateIndexSql(),
            statement => statement.Contains("[id]", StringComparison.Ordinal));
    }

    [Fact]
    public void With_index_creation_off_the_created_table_is_exactly_what_it_was()
    {
        var plan = SqlServerCreatePlan.Create(Dataset(), Schema(("population", AttributeKind.Int64), ("geom", AttributeKind.Geometry)), false);

        Assert.Equal("CREATE TABLE [dbo].[cities] ([population] bigint, [geom] geometry)", plan.CreateTableSql());
        Assert.Null(plan.IdentityColumn);
        Assert.Empty(plan.CreateIndexSql());
    }

    [Fact]
    public void A_schema_that_already_names_the_engine_key_gets_the_next_free_name()
    {
        var plan = SqlServerCreatePlan.Create(
            Dataset(), Schema(("id", AttributeKind.Int64), ("ID_1", AttributeKind.String), ("geom", AttributeKind.Geometry)), true);

        Assert.Equal("id_2", plan.IdentityColumn);
        Assert.Contains(
            "[id_2] bigint IDENTITY(1,1) NOT NULL CONSTRAINT [spatial_key_cities] PRIMARY KEY",
            plan.CreateTableSql(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_engine_key_name_does_not_collide_with_a_field_whose_case_differs()
    {
        var plan = SqlServerCreatePlan.Create(Dataset(), Schema(("Id", AttributeKind.Int64), ("geom", AttributeKind.Geometry)), true);

        Assert.Equal("id_1", plan.IdentityColumn);
    }

    [Fact]
    public void The_engine_key_name_is_the_one_the_ingest_path_assigns()
    {
        Assert.Equal(SqlServerIngestPlan.AutoIdentityColumn, SqlServerEngineKey.ColumnName);
    }
}
