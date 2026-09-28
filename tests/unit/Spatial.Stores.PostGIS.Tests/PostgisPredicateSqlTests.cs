using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The PostGIS back end of the one predicate vocabulary (ADR-0074): the
/// grammar is parsed once at the boundary (<c>FeatureFilterText</c>), and
/// this compiler turns the tree into a <c>WHERE</c> fragment with bound
/// parameters — never inlined literals — resolving fields against the
/// discovered schema and combining the bounding-box pre-filter.
/// </summary>
public sealed class PostgisPredicateSqlTests
{
    private static readonly FeatureSchema Schema = FeatureTests.Schema(
        ("population", AttributeKind.Int64, true),
        ("city", AttributeKind.String, false),
        ("score", AttributeKind.Double, true),
        ("active", AttributeKind.Boolean, false),
        ("geom", AttributeKind.Geometry, false));

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
    public void A_whole_number_wider_than_a_double_keeps_every_digit()
    {
        // The literal is carried verbatim, so the bound value is the whole
        // number the client wrote rather than its double rounding: 2^53 + 1
        // is not representable as a double.
        Assert.Equal(9007199254740993L, Build("population = 9007199254740993").Parameters[0]);
    }

    [Fact]
    public void A_date_time_literal_binds_as_an_instant()
    {
        var bound = Assert.IsType<DateTimeOffset>(Build("population > TIMESTAMP '2024-01-01 00:00:00'").Parameters[0]);

        Assert.Equal(
            DateTimeOffset.Parse("2024-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            bound.ToUniversalTime());
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

    [Fact]
    public void Membership_binds_every_value()
    {
        var included = Build("city IN ('a', 'b')");
        Assert.Equal("\"city\" IN (@p0, @p1)", included.Sql);
        Assert.Equal(["a", "b"], included.Parameters);

        var excluded = Build("city NOT IN ('a')");
        Assert.Equal("\"city\" NOT IN (@p0)", excluded.Sql);
    }

    [Fact]
    public void Comparing_with_null_is_never_true()
    {
        Assert.Equal("FALSE", Build("city = NULL").Sql);
        Assert.Equal("TRUE", Build("city != NULL").Sql);
        Assert.Empty(Build("city = NULL").Parameters);
    }

    [Fact]
    public void A_constant_is_the_plan_own_truth_value()
    {
        // The constant is the plan's own truth value (the Esri `1=1` idiom,
        // ADR-0074 §7), not a client value, so it is not bound.
        Assert.Equal("TRUE", PostgisPredicateSql.Where(Predicate.All, Schema, []));
        Assert.Equal("FALSE", PostgisPredicateSql.Where(Predicate.None, Schema, []));
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
    [InlineData("city IN ('a'")]
    [InlineData("city IN 'a'")]
    [InlineData("city IN (1, NULL)")]
    public void Parse_errors_are_reported(string filter)
    {
        Assert.False(Parse(filter, out _));
    }

    [Fact]
    public void A_malformed_exponent_reports_an_invalid_number()
    {
        Assert.False(Parse("population > 1e999", out var error));

        Assert.Contains("not a valid filter number", error);
    }

    [Theory]
    [InlineData("population 1")]
    [InlineData("population > 1 city = 'x'")]
    public void Trailing_content_is_rejected(string filter) =>
        Assert.False(Parse(filter, out _));

    [Fact]
    public void An_unterminated_string_is_reportable()
    {
        Assert.False(Parse("city = 'open", out var error));

        Assert.Contains("position", error);
    }

    [Fact]
    public void Keywords_are_case_insensitive()
    {
        Assert.Equal(Build("population > 1 AND city = 'x'").Sql, Build("population > 1 and city = 'x'").Sql);
    }

    [Fact]
    public void Unknown_column_is_invalid_with_the_available_fields()
    {
        var failure = Assert.Throws<SpatialException>(() => Build("mystery = 1"));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
        Assert.Contains("'mystery'", failure.Message);
        Assert.Contains("'population'", failure.Message);
        Assert.Contains("'city'", failure.Message);
    }

    [Fact]
    public void Geometry_column_filter_is_rejected_with_the_bbox_hint()
    {
        var failure = Assert.Throws<SpatialException>(() => Build("geom = 3"));

        Assert.Contains("bounding box", failure.Message);
        Assert.Contains("'geom'", failure.Message);
    }

    [Fact]
    public void A_membership_test_on_a_geometry_column_is_rejected_too()
    {
        var failure = Assert.Throws<SpatialException>(() => Build("geom IN (1, 2)"));

        Assert.Contains("bounding box", failure.Message);
    }

    [Fact]
    public void A_membership_test_holding_null_is_unanswerable_in_sql()
    {
        // The grammar refuses this, so it can only be a hand-built plan: the
        // compiler still answers the way three-valued logic reads.
        var sql = PostgisPredicateSql.Where(
            new Predicate.IsIn(new FieldRef("city"), [Literal.FromText("a"), Literal.Null], false),
            Schema,
            []);

        Assert.Equal("FALSE", sql);
    }

    [Fact]
    public void A_guid_literal_binds_as_a_guid_not_as_text()
    {
        // Postgres has no `uuid = text` operator, so a string literal bound
        // against a uuid column is a server error at execution, not a filter
        // that matches nothing. The column's kind is what the driver must bind.
        var reference = Guid.Parse("11111111-2222-3333-4444-555555555555");

        Assert.Equal(reference, Build($"reference = '{reference:D}'", GuidSchema).Parameters[0]);
    }

    [Fact]
    public void A_guid_membership_test_binds_every_value_as_a_guid()
    {
        var first = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var second = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

        var sql = Build($"reference IN ('{first:D}', '{second:D}')", GuidSchema);

        Assert.Equal(new object?[] { first, second }, sql.Parameters);
    }

    [Fact]
    public void A_guid_literal_that_is_not_a_guid_matches_nothing()
    {
        // The reference evaluator answers "no match" for a guid it cannot parse,
        // so the pushdown must answer the same rather than bind a zero uuid:
        // a store that refused the plan would disagree with MemoryStore on one
        // plan, and a store that coerced it would answer something else again
        // (ADR-0074 §4: pushdown preserves contract semantics, it does not
        // restate them).
        var sql = Build("reference = 'not-a-guid'", GuidSchema);

        Assert.Equal("FALSE", sql.Sql);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void A_guid_column_compared_with_a_number_matches_nothing()
    {
        // Nothing coerces: `uuid > 5` is not SQL Postgres can answer at all, and
        // the reference answer is "no match" because a number is not a guid.
        var sql = Build("reference > 5", GuidSchema);

        Assert.Equal("FALSE", sql.Sql);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void A_guid_column_does_not_take_a_like_pattern()
    {
        // LIKE is a whole-value text test, so against a uuid column it is
        // unanswerable — and the reference answer is "no match".
        var sql = Build("reference LIKE 'a%'", GuidSchema);

        Assert.Equal("FALSE", sql.Sql);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void A_membership_test_drops_the_values_that_could_never_match()
    {
        // The unparseable value is not an error and does not poison the list: it
        // is simply a value the column can never equal, so the rest still
        // answer. With none left the test matches nothing.
        var known = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var mixed = Build($"reference IN ('{known:D}', 'not-a-guid')", GuidSchema);
        Assert.Equal("\"reference\" IN (@p0)", mixed.Sql);
        Assert.Equal(new object?[] { known }, mixed.Parameters);

        var none = Build("reference IN ('not-a-guid')", GuidSchema);
        Assert.Equal("FALSE", none.Sql);
        Assert.Empty(none.Parameters);
    }

    [Fact]
    public void A_negated_membership_test_keeps_the_values_that_could_match()
    {
        // Removing a never-matching value from a NOT IN does not change the
        // answer — unless it empties the list, which inverts it.
        var known = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var mixed = Build($"reference NOT IN ('not-a-guid', '{known:D}')", GuidSchema);
        Assert.Equal("\"reference\" NOT IN (@p0)", mixed.Sql);
        Assert.Equal(new object?[] { known }, mixed.Parameters);

        var none = Build("reference NOT IN ('not-a-guid')", GuidSchema);
        Assert.Equal("TRUE", none.Sql);
        Assert.Empty(none.Parameters);
    }

    [Fact]
    public void A_text_column_compared_with_a_number_matches_nothing()
    {
        // The same rule for the other direction a text column cannot answer:
        // Postgres has no `text > double precision` operator either.
        Assert.Equal("FALSE", Build("city > 5").Sql);
        Assert.Equal("FALSE", Build("city = 5").Sql);
    }

    private static readonly FeatureSchema GuidSchema = FeatureTests.Schema(
        ("reference", AttributeKind.Guid, true),
        ("city", AttributeKind.String, false));

    private static readonly FeatureSchema TimeSchema = FeatureTests.Schema(
        ("seen", AttributeKind.DateTimeOffset, true),
        ("city", AttributeKind.String, false));

    [Fact]
    public void A_number_against_a_date_time_column_binds_as_an_instant()
    {
        // A date-time is an instant, so a number is the same point on the same
        // axis and the comparison is answerable — but only if it is bound as a
        // timestamp. `timestamptz = bigint` is not an operator Postgres has, so
        // binding the number as a number is an execution failure, and a store
        // that answered "no rows" instead would disagree with MemoryStore, which
        // reads the same literal as the same instant.
        var moment = DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000);

        var whole = Build("seen = 1700000000000", TimeSchema);
        Assert.Equal("\"seen\" = @p0", whole.Sql);
        Assert.Equal(moment, whole.Parameters[0]);

        var fraction = Build("seen > 1699999999999.5", TimeSchema);
        Assert.Equal(moment.AddMilliseconds(-0.5), fraction.Parameters[0]);

        var stamp = Build("seen = TIMESTAMP '2023-11-14 22:13:20'", TimeSchema);
        Assert.Equal(moment, stamp.Parameters[0]);
    }

    [Fact]
    public void A_date_time_literal_on_a_date_time_column_stays_an_instant()
    {
        var bound = Assert.IsType<DateTimeOffset>(
            Build("seen > TIMESTAMP '2024-01-01 00:00:00'", TimeSchema).Parameters[0]);

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_704_067_200_000), bound);
    }

    [Fact]
    public void A_text_literal_against_a_date_time_column_still_matches_nothing()
    {
        // The instant re-encoding is lossless, so it is not a licence to guess:
        // a string is not an instant and the comparison remains unanswerable.
        var sql = Build("seen = 'yesterday'", TimeSchema);

        Assert.Equal("FALSE", sql.Sql);
        Assert.Empty(sql.Parameters);
    }

    [Fact]
    public void Bounding_box_builds_a_parameterised_envelope_predicate()
    {
        var parameters = new List<object?>();

        var sql = PostgisPredicateSql.BoundingBox(new BoundingBox(13.0, 52.0, 14.0, 53.0), Description(), parameters);

        Assert.Equal("\"geom\" && ST_MakeEnvelope(@p0, @p1, @p2, @p3, 4326)", sql);
        Assert.Equal(new object?[] { 13.0, 52.0, 14.0, 53.0 }, parameters);
    }

    [Fact]
    public void A_bbox_combined_with_a_filter_never_reuses_a_parameter_name()
    {
        var parameters = new List<object?>();

        var sql = PostgisPredicateSql.Build(Description(), new BoundingBox(13.0, 52.0, 14.0, 53.0), Where("city = 'x'"), parameters);

        Assert.Equal("(\"geom\" && ST_MakeEnvelope(@p0, @p1, @p2, @p3, 4326)) AND (\"city\" = @p4)", sql);
        Assert.Equal(5, parameters.Count);
    }

    [Fact]
    public void A_plan_with_no_predicate_selects_everything()
    {
        Assert.Null(PostgisPredicateSql.Build(Description(), null, null, []));
    }

    [Fact]
    public void Built_sql_never_contains_a_literal_value()
    {
        var sql = Build("city = 'atlantis' AND population > 7").Sql;

        Assert.DoesNotContain("atlantis", sql);
        Assert.DoesNotContain("7", sql);
        Assert.DoesNotContain(";", sql);

        // Injection text becomes a parse error before it can reach SQL.
        Assert.False(Parse("city = 'x'; DROP TABLE places", out _));
    }

    private static bool Parse(string filter, out string error) =>
        FeatureFilterText.TryParse(filter, out _, out error);

    private static Predicate Where(string filter)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);
        return predicate!;
    }

    private static BuiltSql Build(string filter) => Build(filter, Schema);

    private static BuiltSql Build(string filter, FeatureSchema schema)
    {
        var parameters = new List<object?>();
        return new BuiltSql(PostgisPredicateSql.Where(Where(filter), schema, parameters), parameters);
    }

    private static DatasetDescription Description() =>
        new("public.places", "public", "places", "geom", 4326, "Point", 0, ["id"], Schema);

    private sealed record BuiltSql(string Sql, List<object?> Parameters);
}
