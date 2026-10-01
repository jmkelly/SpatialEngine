using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The reductions the SQL Server store compiles to T-SQL rather than reading a
/// whole layer and folding it in managed code (ADR-0133): the count, the
/// distinct set and the grouped aggregate. The statement shapes are pinned
/// here, because what the dialect cannot say in SQL is what the store must
/// refuse to push rather than answer differently — and because the two rules
/// T-SQL states the other way round (nulls lowest, a case-insensitive default
/// collation) have to be written out in every statement or the reduction is
/// not the reference's reduction.
///
/// <para>
/// The answer-equality half is measured against a real database by the shared
/// conformance suite in <c>Spatial.SqlServer.Tests</c>.
/// </para>
/// </summary>
public sealed class SqlServerReductionPushdownTests
{
    private const string Qualified = "dbo.places";

    private static SqlServerDatasetName Dataset
    {
        get
        {
            Assert.True(SqlServerDatasetName.TryParse(Qualified, out var name, out var reason), reason);
            return name;
        }
    }

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("city", AttributeKind.String, true),
        new FieldDefinition("population", AttributeKind.Int64, true),
        new FieldDefinition("ratio", AttributeKind.Double, true),
        new FieldDefinition("flag", AttributeKind.Boolean, true),
        new FieldDefinition("geom", AttributeKind.Geometry, true)]);

    /// <summary>
    /// A text group key as T-SQL states it: the column read under the
    /// code-point collation, which is both the expression the <c>GROUP BY</c>
    /// groups by and the one the group order is written over, since a
    /// grouped statement's <c>ORDER BY</c> may name nothing else
    /// (ADR-0124 §2-§3).
    /// </summary>
    private const string CityKey = "CONVERT(nvarchar(max), [city]) COLLATE Latin1_General_100_BIN2";

    private const string CityAscending = $"CASE WHEN {CityKey} IS NULL THEN 1 ELSE 0 END, {CityKey} ASC";

    private const string CityDescending = $"CASE WHEN {CityKey} IS NULL THEN 0 ELSE 1 END, {CityKey} DESC";

    /// <summary>
    /// The same order written over the <em>derived</em> key of a windowed
    /// statement: the derived column is the key expression itself, so it
    /// already carries the code-point collation the partition was taken under,
    /// and the group, the partition and the order are one value.
    /// </summary>
    private const string CityDerivedAscending = "CASE WHEN [city] IS NULL THEN 1 ELSE 0 END, [city] ASC";

    [Fact]
    public void A_count_is_the_database_counting_the_restriction_it_pushed()
    {
        Assert.Equal(
            "SELECT COUNT(*) FROM [dbo].[places] WHERE [population] > @p0",
            SqlServerPlanQueries.Count(Dataset, "[population] > @p0"));
    }

    [Fact]
    public void A_grouped_reduction_is_a_group_by_over_the_key_and_not_a_row_set()
    {
        var parameters = new List<object?>();
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
             new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            parameters);

        Assert.NotNull(sql);
        Assert.Equal(
            $"SELECT {CityKey} AS [city], COUNT(*),"
            + " SUM(CONVERT(bigint, [population])) FROM [dbo].[places]"
            + $" GROUP BY {CityKey} ORDER BY {CityAscending}",
            sql);
        Assert.Empty(parameters);
    }

    /// <summary>
    /// ADR-0128 §8: the page clause follows the order, in that order, or T-SQL
    /// refuses the statement — <c>OFFSET</c>/<c>FETCH NEXT</c> is legal only
    /// over an <c>ORDER BY</c>. The pushdown carries the plan's group order and
    /// its cut together, so the two are pinned as one statement.
    /// </summary>
    [Fact]
    public void The_group_order_is_written_before_the_page_it_cuts()
    {
        // The restriction bound its own parameter first, so the page's two are
        // numbered after it.
        var parameters = new List<object?> { 1000L };
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: "[population] > @p0",
            ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            [new OrderTerm("city", SortDirection.Descending)],
            Schema,
            byteOrderText: false,
            parameters,
            having: null,
            paging: new SqlServerPlanQueries.Paging(5, 10));

        Assert.NotNull(sql);
        Assert.Equal(
            $"SELECT {CityKey} AS [city], COUNT(*)"
            + " FROM [dbo].[places] WHERE [population] > @p0"
            + $" GROUP BY {CityKey}"
            + $" ORDER BY {CityDescending} OFFSET @p1 ROWS FETCH NEXT @p2 ROWS ONLY",
            sql);
        Assert.Equal([1000L, 10, 5], parameters);
    }

    [Fact]
    public void A_grouped_reduction_with_no_plan_order_is_reduced_here()
    {
        // `GROUP BY` returns rows in no defined order and the contract's order
        // for a reduction is the plan's, which is the store's scan order here.
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            order: [],
            Schema,
            byteOrderText: false,
            new List<object?>()));
    }

    [Fact]
    public void A_group_order_over_a_value_a_group_row_does_not_carry_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            [new OrderTerm("population")],
            Schema,
            byteOrderText: false,
            new List<object?>()));
    }

    /// <summary>
    /// A grouped key a <c>GROUP BY</c> could carry and the reference could not
    /// group the same way is declined rather than pushed: a geometry groups by
    /// its stored bytes there and by its value here (ADR-0133 §4).
    /// </summary>
    [Fact]
    public void A_geometry_group_key_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["geom"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            [new OrderTerm("geom")],
            Schema,
            byteOrderText: false,
            new List<object?>()));
    }

    /// <summary>
    /// The one statistic T-SQL has no <em>aggregate</em> for is a percentile: it
    /// is a window function, so it cannot be written over the statement the
    /// <c>GROUP BY</c> is about to build (a window's ordering column has to be
    /// a grouped one, and the ranked field is not grouped — error 8120). It is
    /// pushed anyway, by ranking the rows in a derived table <c>PARTITION BY</c>
    /// the group key and reducing the one value each partition produced: a
    /// partition is a group, so the value a partition ranked is the value that
    /// group reports. The rest of the reduction is the grouped statement as
    /// before, over the derived table's columns (ADR-0137 §2).
    ///
    /// <para>
    /// The derived table carries the group key and the columns the other
    /// statistics reduce, so the <c>GROUP BY</c> and the outer order are written
    /// over the derived key and the partition is written over the base one —
    /// they are the same value, the key expression with its code-point
    /// collation, and a partition that folded <c>"Alpha"</c> onto <c>"alpha"</c>
    /// would be a group the reference does not have (ADR-0121).
    /// </para>
    /// </summary>
    [Fact]
    public void A_percentile_is_a_window_function_over_the_partition_a_group_is()
    {
        var parameters = new List<object?> { 1000L };
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: "[population] > @p0",
            ["city"],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows"),
             new AggregateSpec(AggregateStatistic.PercentileContinuous, "population", "p90", 0.9)],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            parameters,
            having: null,
            paging: new SqlServerPlanQueries.Paging(5, 10));

        Assert.NotNull(sql);
        Assert.Equal(
            "SELECT [city], COUNT(*), MAX([d].[w0])"
            + $" FROM (SELECT {CityKey} AS [city],"
            + " PERCENTILE_CONT(@p1) WITHIN GROUP (ORDER BY [population] ASC)"
            + $" OVER (PARTITION BY {CityKey}) AS [w0]"
            + " FROM [dbo].[places] WHERE [population] > @p0) AS d"
            + " GROUP BY [city]"
            + $" ORDER BY {CityDerivedAscending}"
            + " OFFSET @p2 ROWS FETCH NEXT @p3 ROWS ONLY",
            sql);

        // The fraction is a bound value, not text, and it is numbered after the
        // restriction the plan had already bound and before the page's two.
        Assert.Equal([1000L, 0.9, 10, 5], parameters);
    }

    /// <summary>
    /// An ungrouped reduction is one group, so its percentile is ranked over
    /// the whole selection — <c>OVER ()</c>, with no partition — and the grouped
    /// statement over it has no <c>GROUP BY</c> at all. The discrete form is the
    /// dataset value at the rank <c>ceil(f × n)</c>, which is what
    /// <c>PERCENTILE_DISC</c> answers, and a descending rank is a descending
    /// <c>WITHIN GROUP</c> — the same shape the PostGIS statement writes.
    /// </summary>
    [Fact]
    public void An_ungrouped_percentile_is_ranked_over_the_whole_selection()
    {
        var parameters = new List<object?>();
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.Average, "population", "mean"),
             new AggregateSpec(AggregateStatistic.PercentileDiscrete, "population", "p50", 0.5, PercentileDescending: true)],
            order: [],
            Schema,
            byteOrderText: false,
            parameters);

        Assert.NotNull(sql);
        Assert.Equal(
            "SELECT SUM(CONVERT(float, [d].[population])) / COUNT([d].[population]), MAX([d].[w0])"
            + " FROM (SELECT [population],"
            + " PERCENTILE_DISC(@p0) WITHIN GROUP (ORDER BY [population] DESC) OVER () AS [w0]"
            + " FROM [dbo].[places]) AS d",
            sql);
        Assert.Equal([0.5], parameters);
    }

    /// <summary>
    /// The group row is read in the order the request asked its statistics in,
    /// not in the order the statement happens to group the two kinds of
    /// statistic into — so a percentile asked for <em>before</em> an aggregate
    /// is reported where the request put it, and the pushed value is the value
    /// at that position. It is the one shape here a wrong answer hides: the row
    /// is read positionally, so a percentile reported under a neighbour's name
    /// is a plausible-looking number in the wrong column.
    /// </summary>
    [Fact]
    public void A_percentile_is_reported_where_the_request_asked_for_it()
    {
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["population"],
            [new AggregateSpec(AggregateStatistic.PercentileContinuous, "population", "p90", 0.9),
             new AggregateSpec(AggregateStatistic.Sum, "population", "total")],
            [new OrderTerm("population")],
            Schema,
            byteOrderText: false,
            new List<object?>());

        Assert.NotNull(sql);
        Assert.Contains(
            "SELECT [population], MAX([d].[w0]), SUM(CONVERT(bigint, [d].[population])) FROM",
            sql);
    }

    /// <summary>
    /// A percentile ranks one column against another, and T-SQL refuses the two
    /// unless they are numbers (error 402), so a percentile over a text field
    /// is a cost and not a statement: the reduction is finished here with the
    /// reference (ADR-0137 §3).
    /// </summary>
    [Fact]
    public void A_percentile_of_a_field_T_SQL_cannot_rank_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.PercentileContinuous, "city", "p90", 0.9)],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            new List<object?>()));
    }

    /// <summary>
    /// The envelope is the one statistic this dialect <em>does</em> have an
    /// aggregate for and this store does not push. ADR-0133 §3 declined it
    /// because “a rectangle is four reduced coordinates and a polygon rather
    /// than one aggregate expression”, which is the wrong reason — the
    /// expression exists, and this test is what the measured reason looks like
    /// in the code: <c>geometry::EnvelopeAggregate</c> and
    /// <c>UnionAggregate(…).STEnvelope()</c> answer a rectangle grown by the
    /// server's 1e-8 tolerance where the reference reports a degenerate one,
    /// and both raise on a group holding an invalid geometry (ADR-0157). The
    /// values that say so are asserted against a live server in
    /// <c>SqlServerStatisticsPushdownTests</c>.
    /// </summary>
    [Fact]
    public void An_envelope_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Envelope, "geom", "box")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            new List<object?>()));
    }

    /// <summary>
    /// The two statistics whose T-SQL spelling is not the field's own type: a
    /// <c>SUM</c> over an integer column is a <c>bigint</c> here because a
    /// hand-authored <c>int</c> column would otherwise overflow in the server
    /// and report an answer the reference keeps, and a mean is a division rather
    /// than an <c>AVG</c> because <c>AVG</c> over an integer column is
    /// integer division — a truncated mean, not the reference's
    /// (ADR-0133 §3).
    /// </summary>
    [Fact]
    public void An_integer_sum_is_a_bigint_and_a_mean_is_a_division()
    {
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Sum, "population", "total"),
             new AggregateSpec(AggregateStatistic.Average, "population", "mean"),
             new AggregateSpec(AggregateStatistic.Average, "ratio", "rate"),
             new AggregateSpec(AggregateStatistic.Variance, "ratio", "var"),
             new AggregateSpec(AggregateStatistic.StdDev, "ratio", "sd")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            new List<object?>());

        Assert.NotNull(sql);
        Assert.Contains("SUM(CONVERT(bigint, [population]))", sql);
        Assert.Contains("SUM(CONVERT(float, [population])) / COUNT([population])", sql);
        Assert.Contains("SUM([ratio]) / COUNT([ratio])", sql);
        Assert.Contains("VAR([ratio])", sql);
        Assert.Contains("STDEV([ratio])", sql);
    }

    /// <summary>
    /// A text extreme is reduced under the same collation every other string
    /// comparison in this store states, so <c>"A"</c> and <c>"a"</c> are two
    /// values here as they are in the reference (ADR-0121, ADR-0124 §3).
    /// </summary>
    [Fact]
    public void A_text_extreme_is_reduced_under_the_code_point_collation()
    {
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["population"],
            [new AggregateSpec(AggregateStatistic.Minimum, "city", "lo"),
             new AggregateSpec(AggregateStatistic.Maximum, "city", "hi")],
            [new OrderTerm("population")],
            Schema,
            byteOrderText: false,
            new List<object?>());

        Assert.NotNull(sql);
        Assert.Contains("MIN(CONVERT(nvarchar(max), [city]) COLLATE Latin1_General_100_BIN2)", sql);
        Assert.Contains("MAX(CONVERT(nvarchar(max), [city]) COLLATE Latin1_General_100_BIN2)", sql);
    }

    /// <summary>
    /// T-SQL has no <c>MIN</c>/<c>MAX</c> over a <c>bit</c> — the type is not
    /// orderable there — but it is two integers, and the contract's order over a
    /// boolean is false before true, which is <c>0</c> before <c>1</c>. So the
    /// extreme is a minimum over the column's own integers and the value is
    /// mapped back to the boolean the reference reports it as (ADR-0137 §4).
    /// </summary>
    [Fact]
    public void A_boolean_extreme_is_a_minimum_over_the_bits_own_integers()
    {
        var sql = SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Minimum, "flag", "lo"),
             new AggregateSpec(AggregateStatistic.Maximum, "flag", "hi")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            new List<object?>());

        Assert.NotNull(sql);
        Assert.Contains("MIN(CONVERT(int, [flag]))", sql);
        Assert.Contains("MAX(CONVERT(int, [flag]))", sql);
    }

    /// <summary>
    /// A page needs an order to skip over, and an ungrouped reduction is one
    /// group that needs none: T-SQL refuses <c>OFFSET</c>/<c>FETCH NEXT</c>
    /// without an <c>ORDER BY</c>, and the only order it would accept there is
    /// one the store would be inventing over a single row (ADR-0124 §6). The
    /// reduction is finished here instead.
    /// </summary>
    [Fact]
    public void A_page_over_an_ungrouped_reduction_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            order: [],
            Schema,
            byteOrderText: false,
            new List<object?>(),
            having: null,
            paging: new SqlServerPlanQueries.Paging(1, 1)));

        // The same reduction unpaged is one aggregate row and is pushed.
        Assert.NotNull(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            groupColumns: [],
            [new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, "rows")],
            order: [],
            Schema,
            byteOrderText: false,
            new List<object?>()));
    }

    [Fact]
    public void A_distinct_set_is_pushed_when_the_plan_order_is_total_over_it()
    {
        var parameters = new List<object?>();
        var sql = SqlServerPlanQueries.Distinct(
            Dataset,
            ["city", "population"],
            [new OrderTerm("city"), new OrderTerm("population", SortDirection.Descending)],
            Schema,
            byteOrderText: false,
            where: null,
            parameters);

        // The `DISTINCT` is the inner statement and the order is the outer one:
        // T-SQL takes an `ORDER BY` over a `DISTINCT` only from the select list
        // (error 145), and the reference's order carries a null-placement key
        // that is not itself one of the requested fields.
        Assert.NotNull(sql);
        Assert.Equal(
            $"SELECT * FROM (SELECT DISTINCT {CityKey} AS [city],"
            + " [population] FROM [dbo].[places]) AS d"
            + " ORDER BY CASE WHEN [d].[city] IS NULL THEN 1 ELSE 0 END,"
            + " CONVERT(nvarchar(max), [d].[city]) COLLATE Latin1_General_100_BIN2 ASC,"
            + " CASE WHEN [d].[population] IS NULL THEN 0 ELSE 1 END, [d].[population] DESC",
            sql);
        Assert.Empty(parameters);
    }

    /// <summary>
    /// The contract's order for a distinct set is the plan's, and a plan that
    /// asks for no order leaves it the order the rows arrived in — which SQL
    /// does not state (ADR-0133 §6).
    /// </summary>
    [Fact]
    public void A_distinct_set_with_no_plan_order_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Distinct(
            Dataset,
            ["city"],
            order: [],
            Schema,
            byteOrderText: false,
            where: null,
            parameters: new List<object?>()));
    }

    /// <summary>
    /// An order that names fewer of the requested fields leaves rows tying on
    /// it in an order the dialect does not state, so the first-seen order is the
    /// reference's and is only knowable here.
    /// </summary>
    [Fact]
    public void A_distinct_set_the_plan_order_does_not_cover_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Distinct(
            Dataset,
            ["city", "population"],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            where: null,
            parameters: new List<object?>()));

        Assert.Null(SqlServerPlanQueries.Distinct(
            Dataset,
            ["city"],
            [new OrderTerm("population")],
            Schema,
            byteOrderText: false,
            where: null,
            parameters: new List<object?>()));
    }

    [Fact]
    public void A_distinct_set_of_a_geometry_field_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Distinct(
            Dataset,
            ["geom"],
            [new OrderTerm("geom")],
            Schema,
            byteOrderText: false,
            where: null,
            parameters: new List<object?>()));
    }

    [Fact]
    public void A_pushed_distinct_set_keeps_the_restriction_it_was_given()
    {
        var parameters = new List<object?> { 1000L };
        var sql = SqlServerPlanQueries.Distinct(
            Dataset,
            ["population"],
            [new OrderTerm("population")],
            Schema,
            byteOrderText: false,
            where: "[population] > @p0",
            parameters);

        Assert.NotNull(sql);
        Assert.Contains("FROM [dbo].[places] WHERE [population] > @p0)", sql);
        Assert.Single(parameters);
    }
}
