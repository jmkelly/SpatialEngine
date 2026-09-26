using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Core;
using FilterBoundingBox = Spatial.Stores.SqlServer.Core.BoundingBox;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The attribute filter language of <c>spatial.feature.query@1</c> as the SQL
/// Server provider implements it: parsing (including every error shape), the
/// parameterised T-SQL with bound values (never inlined literals), column
/// resolution against the schema, and the bounding-box predicate.
/// </summary>
public sealed class SqlServerFilterTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("population", AttributeKind.Int64, true),
        new FieldDefinition("city", AttributeKind.String, false),
        new FieldDefinition("score", AttributeKind.Double, true),
        new FieldDefinition("active", AttributeKind.Boolean, false),
        new FieldDefinition("geom", AttributeKind.Geometry, false),
    ]);

    [Fact]
    public void A_comparison_binds_its_value_as_a_parameter()
    {
        var parameters = new List<object?>();

        var sql = Build("city = 'Berlin'", parameters);

        Assert.Equal("[city] = @p0", sql);
        Assert.Equal(["Berlin"], parameters);
    }

    [Theory]
    [InlineData("population < 3", "[population] < @p0")]
    [InlineData("population <= 3", "[population] <= @p0")]
    [InlineData("population > 3", "[population] > @p0")]
    [InlineData("population >= 3", "[population] >= @p0")]
    [InlineData("population != 3", "[population] <> @p0")]
    [InlineData("population <> 3", "[population] <> @p0")]
    [InlineData("city LIKE 'P%'", "[city] LIKE @p0")]
    [InlineData("active = TRUE", "[active] = @p0")]
    [InlineData("score > -1.5", "[score] > @p0")]
    [InlineData("city IS NULL", "[city] IS NULL")]
    [InlineData("city IS NOT NULL", "[city] IS NOT NULL")]
    public void Every_operator_renders_its_own_shape(string filter, string expected) =>
        Assert.Equal(expected, Build(filter, new List<object?>()));

    [Fact]
    public void Conjunctions_are_grouped_so_precedence_survives()
    {
        var sql = Build("population = 1 OR population = 2 AND city = 'x'", new List<object?>());

        Assert.Equal("[population] = @p0 OR ([population] = @p1 AND [city] = @p2)", sql);
    }

    [Fact]
    public void Values_are_bound_in_order_and_never_inlined()
    {
        var parameters = new List<object?>();

        var sql = Build("city = 'a''b' AND population = 12", parameters);

        Assert.Equal("[city] = @p0 AND [population] = @p1", sql);
        Assert.Equal(["a'b", 12L], parameters);
        Assert.DoesNotContain("'a''b'", sql);
    }

    [Fact]
    public void A_mixed_case_discovered_field_is_resolved_and_bracketed()
    {
        // Real ingests carry names like LABELRANK (ADR-0041 §3): the filter
        // resolves them against the discovered schema and quotes them.
        var schema = new FeatureSchema([new FieldDefinition("LABELRANK", AttributeKind.Double, true)]);
        var parameters = new List<object?>();

        Assert.True(SqlServerFilterSql.TryBuild(
            Parsed("LABELRANK > 1.5"), schema, parameters, out var sql, out var error), error);

        Assert.Equal("[LABELRANK] > @p0", sql);
        Assert.Equal([1.5d], parameters);
    }

    [Fact]
    public void An_unknown_column_names_the_available_fields()
    {
        var failure = BuildFails("mystery = 1");

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("'mystery'", failure.Message);
        Assert.Contains("'city'", failure.Message);
    }

    [Fact]
    public void A_geometry_column_points_at_the_bounding_box()
    {
        var failure = BuildFails("geom = 1");

        Assert.Contains("bounding box", failure.Message);
    }

    [Theory]
    [InlineData("city =")]
    [InlineData("city = 'unterminated")]
    [InlineData("city = 'a' AND")]
    [InlineData("(city = 'a'")]
    [InlineData("city ~ 1")]
    [InlineData("city = 1e")]
    [InlineData("city IS 1")]
    [InlineData("city = 'a' city = 'b'")]
    [InlineData("= 1")]
    [InlineData("city $ 1")]
    public void A_malformed_filter_is_invalid_arguments(string filter)
    {
        var failure = BuildFails(filter);

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.NotEmpty(failure.Message);
    }

    [Fact]
    public void The_bounding_box_predicate_binds_its_envelope()
    {
        var parameters = new List<object?>();

        var sql = SqlServerFilterSql.BoundingBox(new FilterBoundingBox(1, 2, 3, 4), "geom", 4326, parameters);

        Assert.Equal("[geom].STIntersects(geometry::STGeomFromWKB(@p0, 4326)) = 1", sql);
        var envelope = Assert.IsType<byte[]>(Assert.Single(parameters));
        Assert.Equal<byte[]>([0x01, 0x03, 0x00, 0x00, 0x00], envelope[..5]);
    }

    [Fact]
    public void A_bbox_combined_with_a_filter_never_reuses_a_parameter_name()
    {
        var parameters = new List<object?>();
        SqlServerFilterSql.BoundingBox(new FilterBoundingBox(1, 2, 3, 4), "geom", 4326, parameters);
        var filter = Build("city = 'x'", parameters);

        Assert.Equal("[city] = @p1", filter);
        Assert.Equal(2, parameters.Count);
    }

    private static string Build(string filter, List<object?> parameters)
    {
        Assert.True(
            SqlServerFilterSql.TryBuild(Parsed(filter), Schema, parameters, out var sql, out var error),
            error);
        return sql;
    }

    private static SpatialException BuildFails(string filter) =>
        Assert.Throws<SpatialException>(() =>
            SqlServerPredicate.Build(Description(), null, filter, new List<object?>()));

    private static FilterExpression Parsed(string filter)
    {
        Assert.True(SqlServerFilterParser.TryParse(filter, out var expression, out var error), error);
        return expression;
    }

    private static Spatial.Contracts.Providers.DatasetDescription Description() =>
        new(
            "dbo.places",
            "dbo",
            "places",
            "geom",
            4326,
            "Point",
            0,
            ["id"],
            Schema);
}
