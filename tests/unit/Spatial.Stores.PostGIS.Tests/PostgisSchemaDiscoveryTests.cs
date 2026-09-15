using Spatial.Core.Features;
using Spatial.PluginSdk.Providers;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// Schema discovery (pure): catalogue rows to a dataset description — field
/// kinds in column order, geometry columns, feature-identity columns, row
/// estimates — plus the failures (no columns, unsupported type, no geometry
/// column) and the catalogue summary mapping.
/// </summary>
public sealed class PostgisSchemaDiscoveryTests
{
    private static PostgisSchemaDiscovery.ColumnRow Column(string name, string udt, bool nullable = false, int ordinal = 0) =>
        new(name, udt, nullable, ordinal);

    [Fact]
    public void Builds_a_description_in_column_order()
    {
        var description = Build(
            [Column("id", "int8", ordinal: 1), Column("name", "text", ordinal: 2), Column("geom", "geometry", ordinal: 3)],
            ["geom"]);

        Assert.Equal("public.places", description.Id);
        Assert.Equal("geom", description.GeometryColumn);
        Assert.Equal(4326, description.Srid);
        Assert.Equal("POINT", description.GeometryType);
        Assert.Equal(10, description.EstimatedRowCount);
        Assert.Equal(["id"], description.IdColumns);
        Assert.Equal(
            [$"id|{AttributeKind.Int64}", $"name|{AttributeKind.String}", $"geom|{AttributeKind.Geometry}"],
            description.Schema.Fields.Select(field => $"{field.Name}|{field.Kind}").ToArray());
    }

    [Fact]
    public void Nullable_columns_are_carried()
    {
        var description = Build([Column("id", "int8", nullable: true), Column("geom", "geometry")], ["geom"]);

        Assert.True(description.Schema[0].Nullable);
    }

    [Fact]
    public void Unsupported_column_types_fail_with_the_column_named()
    {
        var ok = TryBuild([Column("data", "jsonb"), Column("geom", "geometry")], ["geom"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("'data'", error);
        Assert.Contains("jsonb", error);
    }

    [Fact]
    public void A_table_without_columns_is_rejected()
    {
        var ok = TryBuild([], ["geom"], out _, out var error);

        Assert.False(ok);
        Assert.Contains("no columns", error);
    }

    [Fact]
    public void A_table_without_a_geometry_column_is_not_a_spatial_dataset()
    {
        var ok = TryBuild([Column("id", "int8")], [], out _, out var error);

        Assert.False(ok);
        Assert.Contains("not a spatial dataset", error);
    }

    [Fact]
    public void The_first_geometry_column_by_order_is_the_primary_one()
    {
        var description = Build(
            [Column("geom", "geometry", ordinal: 2), Column("route", "geometry", ordinal: 5), Column("id", "int8", ordinal: 1)],
            ["geom", "route"]);

        Assert.Equal("geom", description.GeometryColumn);
        Assert.Equal(["id", "geom", "route"], description.Schema.Fields.Select(field => field.Name).ToArray());
    }

    [Fact]
    public void A_geometry_view_column_typed_as_text_is_trusted_as_geometry()
    {
        // geometry_columns names the column while information_schema types it
        // differently (a view): the geometry view wins.
        var description = Build([Column("geom", "text")], ["geom"]);

        Assert.Equal(AttributeKind.Geometry, description.Schema.Fields.Single(field => field.Name == "geom").Kind);
    }

    [Fact]
    public void Summary_from_a_catalogue_row()
    {
        var summary = PostgisSchemaDiscovery.SummaryFromRow(new object?[] { "public", "places", "geom", 4326, "POINT", 12.0 });

        Assert.Equal("public.places", summary.Id);
        Assert.Equal("public", summary.Schema);
        Assert.Equal("geom", summary.GeometryColumn);
        Assert.Equal(4326, summary.Srid);
        Assert.Equal(12, summary.EstimatedRowCount);
    }

    private static DatasetDescription Build(
        IReadOnlyList<PostgisSchemaDiscovery.ColumnRow> columns,
        IReadOnlyList<string> geometryColumns) =>
        TryBuild(columns, geometryColumns, out var description, out _)
            ? description
            : throw new InvalidOperationException("expected the discovery to succeed");

    private static bool TryBuild(
        IReadOnlyList<PostgisSchemaDiscovery.ColumnRow> columns,
        IReadOnlyList<string> geometryColumns,
        out DatasetDescription description,
        out string error)
    {
        var geometries = geometryColumns
            .Select((column, index) => new PostgisSchemaDiscovery.GeometryRow(column, 4326, "POINT"))
            .ToArray();
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));
        return PostgisSchemaDiscovery.TryBuild(
            dataset,
            new PostgisSchemaDiscovery.SchemaFacts(columns, geometries, ["id"], 10),
            out description,
            out error);
    }
}
