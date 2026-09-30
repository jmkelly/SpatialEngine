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
    /// T-SQL has no ordered-set aggregate, so a percentile is a window function
    /// over a whole result set and not an aggregate over a group: the reduction
    /// that asks for one is finished here (ADR-0133 §5).
    /// </summary>
    [Theory]
    [InlineData(AggregateStatistic.PercentileContinuous, "p90")]
    [InlineData(AggregateStatistic.PercentileDiscrete, "p50")]
    public void A_percentile_is_reduced_here(AggregateStatistic statistic, string name)
    {
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(statistic, "population", name, 0.9)],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            new List<object?>()));
    }

    /// <summary>
    /// The rectangle of a geometry column is four reduced coordinates and a
    /// polygon built out of them; there is no aggregate expression that answers
    /// it, so a reduction that asks for one is finished here (ADR-0133 §5).
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

    /// <summary>T-SQL has no <c>MIN</c>/<c>MAX</c> over a <c>bit</c>.</summary>
    [Fact]
    public void A_boolean_extreme_is_reduced_here()
    {
        Assert.Null(SqlServerPlanQueries.Aggregate(
            Dataset,
            where: null,
            ["city"],
            [new AggregateSpec(AggregateStatistic.Minimum, "flag", "lo")],
            [new OrderTerm("city")],
            Schema,
            byteOrderText: false,
            new List<object?>()));
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
