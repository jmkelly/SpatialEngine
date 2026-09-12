using Spatial.Core.Features;
using Spatial.Core.Geometry;
using Spatial.Provider.PostGIS.Core;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// The attribute filter language of <c>spatial.feature.query@1</c>: parsing
/// (including every error shape), the parameterised SQL output with bound
/// values (never inlined literals), column resolution against the schema,
/// and the bbox predicate.
/// </summary>
public sealed class PostgisFilterTests
{
    private static readonly FeatureSchema Schema = FeatureTests.Schema(
        ("population", AttributeKind.Int64, true),
        ("city", AttributeKind.String, false),
        ("score", AttributeKind.Double, true),
        ("active", AttributeKind.Boolean, false),
        ("geom", AttributeKind.Geometry, false));

    // ---- Parsing ----

    [Fact]
    public void Comparison_with_integer_parameterises()
    {
        var sql = Build("population >= 1000");

        Assert.Equal("\"population\" >= @p0", sql.Sql);
        Assert.Single(sql.Parameters);
        Assert.Equal(1000L, sql.Parameters[0]);
    }

    [Fact]
    public void String_literal_with_escaped_quote_and_like_parameterise()
    {
        var escaped = Build("city = 'O''Brien'");
        Assert.Equal("\"city\" = @p0", escaped.Sql);
        Assert.Equal("O'Brien", escaped.Parameters[0]);

        var like = Build("city LIKE 'Be%'");
        Assert.Equal("\"city\" LIKE @p0", like.Sql);
        Assert.Equal("Be%", like.Parameters[0]);
    }

    [Fact]
    public void Boolean_decimal_and_negative_numbers_parameterise_typed()
    {
        Assert.Equal(true, Build("active = true").Parameters[0]);
        Assert.Equal(2.5, Build("score > 2.5").Parameters[0]);
        Assert.Equal(-3L, Build("population < -3").Parameters[0]);
        Assert.Equal(1.5e3, Build("score <= 1.5e3").Parameters[0]);
        Assert.Equal(100000.0, Build("population > 1E5").Parameters[0]);
        Assert.Equal(100000.0, Build("population > 1e+5").Parameters[0]);
        Assert.Equal(0.00001, Build("population > 1e-5").Parameters[0]);
        Assert.Equal(10000000000.0, Build("population > 1e10").Parameters[0]);
    }

    [Fact]
    public void And_or_and_parentheses_build_in_precedence_order()
    {
        var sql = Build("city = 'a' OR (population > 1 AND city != 'b')");

        Assert.Equal("\"city\" = @p0 OR (\"population\" > @p1 AND \"city\" != @p2)", sql.Sql);
        Assert.Equal(3, sql.Parameters.Count);

        // The other grouping direction matters too: (a OR b) AND c must not
        // become a OR b AND c (different precedence).
        var grouped = Build("city = 'a' OR city = 'b' AND population > 1");
        Assert.Equal("\"city\" = @p0 OR (\"city\" = @p1 AND \"population\" > @p2)", grouped.Sql);
    }

    [Fact]
    public void Is_null_and_is_not_null_build_without_parameters()
    {
        Assert.Equal("\"city\" IS NULL", Build("city IS NULL").Sql);
        Assert.Equal("\"city\" IS NOT NULL", Build("city IS NOT NULL").Sql);
        Assert.Empty(Build("city IS NOT NULL").Parameters);
    }

    [Theory]
    [InlineData("population >")]
    [InlineData("= 5")]
    [InlineData("population")]
    [InlineData("population <>'")]
    [InlineData("population > 1 AND")]
    [InlineData("population > 1 OR")]
    [InlineData("population > 1e+")]
    [InlineData("(population > 1")]
    [InlineData("population > 1)")]
    [InlineData("population > 1e")]
    [InlineData("city IS")]
    [InlineData("city IS NOT")]
    public void Parse_errors_are_reported(string filter)
    {
        var result = PostgisFilterParser.TryParse(filter, out _, out var error);

        Assert.False(result);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void A_malformed_exponent_reports_an_invalid_number()
    {
        // 1e999 overflows to infinity, so the lexer classifies it InvalidNumber.
        var result = PostgisFilterParser.TryParse("population > 1e999", out _, out var error);

        Assert.False(result);
        Assert.Contains("not a valid filter number", error);
    }

    [Theory]
    [InlineData("population 1")]
    [InlineData("population > 1 city = 'x'")]
    public void Trailing_content_is_rejected(string filter)
    {
        Assert.False(PostgisFilterParser.TryParse(filter, out _, out _));
    }

    [Fact]
    public void Unterminated_string_is_reportable()
    {
        Assert.False(PostgisFilterParser.TryParse("city = 'open", out _, out var error));
        Assert.Contains("position", error);
    }

    [Fact]
    public void Keywords_are_case_insensitive()
    {
        var expected = Build("population > 1 AND city = 'x'");

        var mixed = Build("population > 1 and city = 'x'");

        Assert.Equal(expected.Sql, mixed.Sql);
    }

    // ---- Schema resolution ----

    [Fact]
    public void Unknown_column_is_invalid_with_the_available_fields()
    {
        var error = BuildError("mystery = 1");

        Assert.NotNull(error);
        Assert.Contains("'mystery'", error);
        Assert.Contains("'population'", error);
        Assert.Contains("'city'", error);
    }

    [Fact]
    public void Geometry_column_filter_is_rejected_with_the_bbox_hint()
    {
        var error = BuildError("geom = 3");

        Assert.NotNull(error);
        Assert.Contains("bounding box", error);
        Assert.Contains("'geom'", error);
    }

    // ---- Bounding box ----

    [Fact]
    public void Bounding_box_builds_a_parameterised_envelope_predicate()
    {
        var parameters = new List<object?>();

        var sql = PostgisFilterSql.BoundingBox(new BoundingBox(13.0, 52.0, 14.0, 53.0), "geom", 4326, parameters);

        Assert.Equal("\"geom\" && ST_MakeEnvelope(@p0, @p1, @p2, @p3, 4326)", sql);
        Assert.Equal(new object?[] { 13.0, 52.0, 14.0, 53.0 }, parameters);
    }

    [Fact]
    public void Built_sql_never_contains_a_literal_value()
    {
        var sql = Build("city = 'atlantis' AND population > 7").Sql;

        Assert.DoesNotContain("atlantis", sql);
        Assert.DoesNotContain("7", sql);
        Assert.DoesNotContain(";", sql);

        // Injection text becomes a parse error before it can reach SQL.
        Assert.False(PostgisFilterParser.TryParse("city = 'x'; DROP TABLE places", out _, out _));
    }

    private static BuiltSql Build(string filter)
    {
        Assert.True(PostgisFilterParser.TryParse(filter, out var expression, out var parseError), parseError);
        var parameters = new List<object?>();
        Assert.True(PostgisFilterSql.TryBuild(expression, Schema, parameters, out var sql, out var error), error);
        return new BuiltSql(sql, parameters);
    }

    private static string? BuildError(string filter)
    {
        if (!PostgisFilterParser.TryParse(filter, out var expression, out _))
        {
            return "parse error";
        }

        var parameters = new List<object?>();
        PostgisFilterSql.TryBuild(expression, Schema, parameters, out _, out var error);
        return error;
    }

    private sealed record BuiltSql(string Sql, List<object?> Parameters);
}
