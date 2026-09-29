using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The SQL Server back end of the one predicate vocabulary (ADR-0074): the
/// grammar is parsed once at the boundary (<c>FeatureFilterText</c>), and
/// this compiler turns the tree into a <c>WHERE</c> fragment with bound
/// parameters — never inlined literals — resolving fields against the
/// discovered schema and combining the bounding-box pre-filter.
/// </summary>
public sealed class SqlServerPredicateSqlTests
{
    private static readonly FeatureSchema GuidSchema = new(
    [
        new FieldDefinition("reference", AttributeKind.Guid, true),
        new FieldDefinition("city", AttributeKind.String, false),
    ]);

    [Fact]
    public void A_guid_literal_binds_as_a_guid_not_as_text()
    {
        // The column's kind is what the driver binds, not the literal's own, so
        // a uuid comparison never becomes a string-to-uniqueidentifier guess.
        var parameters = new List<object?>();
        var reference = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var sql = Build($"reference = '{reference:D}'", GuidSchema, parameters);

        Assert.Equal("[reference] = @p0", sql);
        Assert.Equal(new object?[] { reference }, parameters);
    }

    [Fact]
    public void A_guid_literal_that_is_not_a_guid_matches_nothing()
    {
        // The reference evaluator answers "no match" for a guid it cannot
        // parse, and a store that refused the plan would disagree with
        // MemoryStore on one plan (ADR-0074 §4).
        var parameters = new List<object?>();

        Assert.Equal("(1 = 0)", Build("reference = 'not-a-guid'", GuidSchema, parameters));
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_guid_column_compared_with_a_number_matches_nothing()
    {
        var parameters = new List<object?>();

        Assert.Equal("(1 = 0)", Build("reference > 5", GuidSchema, parameters));
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_guid_column_does_not_take_a_like_pattern()
    {
        var parameters = new List<object?>();

        Assert.Equal("(1 = 0)", Build("reference LIKE 'a%'", GuidSchema, parameters));
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_membership_test_drops_the_values_that_could_never_match()
    {
        // An unmatchable value is not an error and does not poison the list; it
        // simply is not a match, so the rest still answer.
        var known = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var mixed = new List<object?>();

        Assert.Equal("[reference] IN (@p0)", Build($"reference IN ('{known:D}', 'not-a-guid')", GuidSchema, mixed));
        Assert.Equal(new object?[] { known }, mixed);

        var none = new List<object?>();
        Assert.Equal("(1 = 0)", Build("reference IN ('not-a-guid')", GuidSchema, none));
        Assert.Empty(none);
    }

    [Fact]
    public void A_negated_membership_test_keeps_the_values_that_could_match()
    {
        // Removing a never-matching value from a NOT IN does not change the
        // answer — unless it empties the list, which inverts it.
        var known = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var mixed = new List<object?>();

        Assert.Equal("[reference] NOT IN (@p0)", Build($"reference NOT IN ('not-a-guid', '{known:D}')", GuidSchema, mixed));
        Assert.Equal(new object?[] { known }, mixed);

        var none = new List<object?>();
        Assert.Equal("(1 = 1)", Build("reference NOT IN ('not-a-guid')", GuidSchema, none));
        Assert.Empty(none);
    }

    [Fact]
    public void A_text_column_compared_with_a_number_matches_nothing()
    {
        // SQL Server has no `text > int` comparison either, so a number against
        // a text column is unanswerable and matches nothing.
        var parameters = new List<object?>();

        Assert.Equal("(1 = 0)", Build("city > 5", parameters));
        Assert.Equal("(1 = 0)", Build("city = 5", parameters));
        Assert.Empty(parameters);
    }

    private static readonly FeatureSchema TimeSchema = new(
    [
        new FieldDefinition("seen", AttributeKind.DateTimeOffset, true),
        new FieldDefinition("city", AttributeKind.String, false),
    ]);

    [Fact]
    public void A_number_against_a_date_time_column_binds_as_an_instant()
    {
        // A date-time is an instant, so a number is the same point on the same
        // axis and the comparison is answerable — but only if it is bound as a
        // datetimeoffset. `datetimeoffset = bigint` is an operand type clash,
        // and a store that answered "no rows" instead would disagree with
        // MemoryStore, which reads the same literal as the same instant.
        var moment = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);
        var parameters = new List<object?>();

        Assert.Equal("[seen] = @p0", Build("seen = 1700000000000", TimeSchema, parameters));
        Assert.Equal(new object?[] { moment }, parameters);

        parameters.Clear();
        Build("seen > 1699999999999.5", TimeSchema, parameters);
        Assert.Equal(new object?[] { moment.AddMilliseconds(-0.5) }, parameters);

        parameters.Clear();
        Build("seen = TIMESTAMP '2023-11-14 22:13:20'", TimeSchema, parameters);
        Assert.Equal(new object?[] { moment }, parameters);
    }

    [Fact]
    public void A_text_literal_against_a_date_time_column_still_matches_nothing()
    {
        // The instant re-encoding is lossless, so it is not a licence to guess:
        // a string is not an instant and the comparison remains unanswerable.
        var parameters = new List<object?>();

        Assert.Equal("(1 = 0)", Build("seen = 'yesterday'", TimeSchema, parameters));
        Assert.Empty(parameters);
    }

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

        Assert.Equal("[city] COLLATE Latin1_General_100_BIN2" + " = @p0", sql);
        Assert.Equal(["Berlin"], parameters);
    }

    [Theory]
    [InlineData("population < 3", "[population] < @p0")]
    [InlineData("population <= 3", "[population] <= @p0")]
    [InlineData("population > 3", "[population] > @p0")]
    [InlineData("population >= 3", "[population] >= @p0")]
    [InlineData("population != 3", "[population] <> @p0")]
    [InlineData("population <> 3", "[population] <> @p0")]
    [InlineData("city LIKE 'P%'", "[city] COLLATE Latin1_General_100_BIN2 LIKE @p0")]
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

        Assert.Equal("[population] = @p0 OR ([population] = @p1 AND [city] COLLATE Latin1_General_100_BIN2 = @p2)", sql);
    }

    [Fact]
    public void Values_are_bound_in_order_and_never_inlined()
    {
        var parameters = new List<object?>();

        var sql = Build("city = 'a''b' AND population = 12", parameters);

        Assert.Equal("[city] COLLATE Latin1_General_100_BIN2" + " = @p0 AND [population] = @p1", sql);
        Assert.Equal(["a'b", 12L], parameters);
        Assert.DoesNotContain("'a''b'", sql);
    }

    [Fact]
    public void A_whole_number_wider_than_a_double_keeps_every_digit()
    {
        // 2^53 + 1 is not representable as a double: the literal is carried
        // verbatim so the bound value keeps every digit.
        var parameters = new List<object?>();

        Assert.Equal("[population] = @p0", Build("population = 9007199254740993", parameters));
        Assert.Equal(9007199254740993L, parameters[0]);
    }

    [Fact]
    public void A_date_time_literal_binds_as_an_instant()
    {
        var parameters = new List<object?>();

        Build("population > TIMESTAMP '2024-01-01 00:00:00'", parameters);

        var bound = Assert.IsType<DateTimeOffset>(Assert.Single(parameters));
        Assert.Equal(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            bound.ToUniversalTime());
    }

    [Fact]
    public void Membership_binds_every_value()
    {
        var parameters = new List<object?>();

        Assert.Equal("[city] COLLATE Latin1_General_100_BIN2" + " IN (@p0, @p1)", Build("city IN ('a', 'b')", parameters));
        Assert.Equal(["a", "b"], parameters);
        Assert.Equal("[city] COLLATE Latin1_General_100_BIN2" + " NOT IN (@p0)", Build("city NOT IN ('a')", new List<object?>()));
    }

    [Fact]
    public void Comparing_with_null_is_never_true()
    {
        var parameters = new List<object?>();

        Assert.Equal("(1 = 0)", Build("city = NULL", parameters));
        Assert.Equal("(1 = 1)", Build("city != NULL", new List<object?>()));
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_constant_is_the_plan_own_truth_value()
    {
        // The constant is the plan's own truth value (the Esri `1=1` idiom,
        // ADR-0074 §7), not a client value, so it is not bound.
        Assert.Equal("(1 = 1)", SqlServerPredicateSql.Where(Predicate.All, Schema, []));
        Assert.Equal("(1 = 0)", SqlServerPredicateSql.Where(Predicate.None, Schema, []));
    }

    [Fact]
    public void A_membership_test_holding_null_is_unanswerable_in_sql()
    {
        var sql = SqlServerPredicateSql.Where(
            new Predicate.IsIn(new FieldRef("city"), [Literal.FromText("a"), Literal.Null], false),
            Schema,
            []);

        Assert.Equal("(1 = 0)", sql);
    }

    [Fact]
    public void A_mixed_case_discovered_field_is_resolved_and_bracketed()
    {
        // Real ingests carry names like LABELRANK (ADR-0041 §3): the filter
        // resolves them against the discovered schema and quotes them.
        var schema = new FeatureSchema([new FieldDefinition("LABELRANK", AttributeKind.Double, true)]);
        var parameters = new List<object?>();

        Assert.Equal("[LABELRANK] > @p0", SqlServerPredicateSql.Where(Where("LABELRANK > 1.5"), schema, parameters));
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

    [Fact]
    public void A_membership_test_on_a_geometry_column_is_rejected_too()
    {
        Assert.Contains("bounding box", BuildFails("geom IN (1, 2)").Message);
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

        var sql = SqlServerPredicateSql.BoundingBox(new BoundingBox(1, 2, 3, 4), Description(), parameters);

        Assert.Equal("[geom].STIntersects(geometry::STGeomFromWKB(@p0, 4326)) = 1", sql);
        var envelope = Assert.IsType<byte[]>(Assert.Single(parameters));
        Assert.Equal<byte[]>([0x01, 0x03, 0x00, 0x00, 0x00], envelope[..5]);
    }

    [Fact]
    public void A_bbox_combined_with_a_filter_never_reuses_a_parameter_name()
    {
        var parameters = new List<object?>();
        SqlServerPredicateSql.BoundingBox(new BoundingBox(1, 2, 3, 4), Description(), parameters);

        Assert.Equal("[city] COLLATE Latin1_General_100_BIN2" + " = @p1", SqlServerPredicateSql.Where(Where("city = 'x'"), Schema, parameters));
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void A_plan_with_no_predicate_selects_everything()
    {
        Assert.Null(SqlServerPredicateSql.Build(Description(), null, null, []));
    }

    private static Predicate Where(string filter)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return predicate!;
    }

    private static string Build(string filter, List<object?> parameters) =>
        SqlServerPredicateSql.Where(Where(filter), Schema, parameters);

    private static string Build(string filter, FeatureSchema schema, List<object?> parameters) =>
        SqlServerPredicateSql.Where(Where(filter), schema, parameters);

    /// <summary>
    /// The whole boundary path a client filter takes: the text is parsed once
    /// (<see cref="FeatureFilter"/>) and the tree is compiled, so a malformed
    /// text and an unknown column are both typed <c>invalid.arguments</c>.
    /// </summary>
    private static SpatialException BuildFails(string filter) =>
        Assert.Throws<SpatialException>(() =>
            SqlServerPredicateSql.Build(Description(), null, FeatureFilter.Parse(filter), []));

    private static DatasetDescription Description() =>
        new("dbo.places", "dbo", "places", "geom", 4326, "Point", 0, ["id"], Schema);
}
