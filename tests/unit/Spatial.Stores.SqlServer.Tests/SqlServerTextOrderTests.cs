using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The order a plan is pushed down with (ADR-0112, ADR-0124): the reference's
/// two ordering rules written as T-SQL, because a pushed <c>ORDER BY</c>
/// inherits neither of them.
///
/// <para>
/// <b>Nulls.</b> T-SQL sorts a null as the lowest value there is — first
/// ascending, last descending — and the contract sorts nulls <em>last</em>
/// ascending, first descending, which is exactly the other way round in both
/// directions. Every term therefore carries a leading
/// <c>CASE WHEN … IS NULL THEN … END</c> key that puts the nulls where the
/// contract puts them.
/// </para>
///
/// <para>
/// <b>Collation.</b> A text sort key inherits the column's collation, and the
/// Windows collations SQL Server ships by default (<c>SQL_Latin1_General_CP1_CI_AS</c>)
/// are a case-insensitive locale comparison, where <c>"A"</c> and <c>"a"</c>
/// are one key and punctuation sorts where its letters sort. The contract
/// compares strings <em>ordinally</em>, so a text term is read under
/// <c>Latin1_General_100_BIN2</c> unless the database already compares by
/// code point (ADR-0121, §2).
/// </para>
/// </summary>
public sealed class SqlServerTextOrderTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("city", AttributeKind.String, true),
        new FieldDefinition("population", AttributeKind.Int64, true),
        new FieldDefinition("geom", AttributeKind.Geometry, false)]);

    [Fact]
    public void An_ascending_term_puts_nulls_last_which_t_sql_does_not()
    {
        Assert.Equal(
            ["CASE WHEN [population] IS NULL THEN 1 ELSE 0 END, [population] ASC"],
            SqlServerPlanQueries.Terms([new OrderTerm("population")], Schema, byteOrderText: false));
    }

    [Fact]
    public void A_descending_term_puts_nulls_first_which_t_sql_does_not_either()
    {
        Assert.Equal(
            ["CASE WHEN [population] IS NULL THEN 0 ELSE 1 END, [population] DESC"],
            SqlServerPlanQueries.Terms(
                [new OrderTerm("population", SortDirection.Descending)], Schema, byteOrderText: false));
    }

    [Fact]
    public void A_text_term_is_read_by_code_point_and_not_by_the_database_collation()
    {
        Assert.Equal(
            [
                $"CASE WHEN [city] IS NULL THEN 1 ELSE 0 END, CONVERT(nvarchar(max), [city]) COLLATE {SqlServerTextCollation.ByteOrder} ASC",
            ],
            SqlServerPlanQueries.Terms([new OrderTerm("city")], Schema, byteOrderText: false));
    }

    [Fact]
    public void A_database_that_already_comparies_by_code_point_carries_no_collation_term()
    {
        Assert.Equal(
            ["CASE WHEN [city] IS NULL THEN 1 ELSE 0 END, CONVERT(nvarchar(max), [city]) ASC"],
            SqlServerPlanQueries.Terms([new OrderTerm("city")], Schema, byteOrderText: true));
    }

    [Fact]
    public void A_numeric_term_takes_no_collation_because_collate_is_a_string_operator()
    {
        var order = SqlServerPlanQueries.Terms([new OrderTerm("population")], Schema, byteOrderText: false);

        Assert.NotNull(order);
        Assert.DoesNotContain("COLLATE", order[0], StringComparison.Ordinal);
        Assert.DoesNotContain("CONVERT", order[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_non_nullable_key_takes_the_null_key_too_because_t_sql_does_not_know_it_is_non_null()
    {
        // `id` is declared NOT NULL and the CASE is harmless there, but the
        // schema the store discovered is the schema a *reader* hands it: a
        // plan's key is not the store's to declare non-null.
        Assert.Equal(
            ["CASE WHEN [id] IS NULL THEN 1 ELSE 0 END, [id] ASC"],
            SqlServerPlanQueries.Terms([new OrderTerm("id")], Schema, byteOrderText: false));
    }

    [Fact]
    public void The_identity_tie_break_is_the_feature_id_string_under_the_code_point_collation()
    {
        // Not the identity *column*: the reference breaks a tie on the id
        // string, where "10" sorts before "9", so an id column ordered by value
        // would put 9 first and cut a page the reference never handed out. The
        // collation is unconditional — the reference compares ids ordinally
        // whatever the database does (ADR-0121).
        Assert.Equal(
            [
                "CASE WHEN [city] IS NULL THEN 0 ELSE 1 END, CONVERT(nvarchar(max), [city]) DESC",
                "CONVERT(nvarchar(max), [id]) COLLATE Latin1_General_100_BIN2 ASC",
            ],
            SqlServerPlanQueries.Order(
                [new OrderTerm("city", SortDirection.Descending)], ["id"], Schema, byteOrderText: true));
    }

    [Fact]
    public void A_composite_identity_is_joined_the_way_a_feature_id_is_joined()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("a", AttributeKind.Int64),
            new FieldDefinition("b", AttributeKind.String),
            new FieldDefinition("value", AttributeKind.Double),
        ]);

        Assert.Equal(
            [
                "CASE WHEN [value] IS NULL THEN 1 ELSE 0 END, [value] ASC",
                "CONCAT(CONVERT(nvarchar(max), [a]), CONVERT(nvarchar(max), [b])) COLLATE Latin1_General_100_BIN2 ASC",
            ],
            SqlServerPlanQueries.Order([new OrderTerm("value")], ["a", "b"], schema, byteOrderText: false));
    }

    [Fact]
    public void A_guid_identity_is_rendered_the_way_dotnet_renders_it()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("key", AttributeKind.Guid),
            new FieldDefinition("value", AttributeKind.Int64),
        ]);

        Assert.Equal(
            [
                "CASE WHEN [value] IS NULL THEN 1 ELSE 0 END, [value] ASC",
                "LOWER(CONVERT(nvarchar(36), [key])) COLLATE Latin1_General_100_BIN2 ASC",
            ],
            SqlServerPlanQueries.Order([new OrderTerm("value")], ["key"], schema, byteOrderText: false));
    }

    [Theory]
    [InlineData(AttributeKind.Double)]
    [InlineData(AttributeKind.Boolean)]
    [InlineData(AttributeKind.DateTimeOffset)]
    [InlineData(AttributeKind.Geometry)]
    public void An_identity_this_dialect_renders_differently_to_dotnet_leaves_the_plan_to_the_reference(
        AttributeKind kind)
    {
        // T-SQL renders these values as other text than `value?.ToString()`
        // (six significant digits, `1`/`0`, the server's date format, a
        // geometry literal), so a tie-break written over them would order
        // tied rows differently from the reference. The plan is finished
        // in-process instead — the same rule as a table with no identity.
        var schema = new FeatureSchema(
        [
            new FieldDefinition("key", kind),
            new FieldDefinition("value", AttributeKind.Int64),
        ]);

        Assert.Null(SqlServerPlanQueries.Order([new OrderTerm("value")], ["key"], schema, byteOrderText: false));
    }

    [Fact]
    public void A_plan_with_no_order_has_no_order_to_push()
    {
        Assert.Null(SqlServerPlanQueries.Order([], ["id"], Schema, byteOrderText: false));
    }

    [Fact]
    public void A_table_with_no_identity_column_cannot_make_any_order_total()
    {
        Assert.Null(SqlServerPlanQueries.Order([new OrderTerm("city")], [], Schema, byteOrderText: false));
    }

    [Fact]
    public void A_sort_key_the_dataset_does_not_have_is_no_order_at_all()
    {
        Assert.Null(SqlServerPlanQueries.Terms([new OrderTerm("nope")], Schema, byteOrderText: false));
    }

    [Theory]
    [InlineData("Latin1_General_100_BIN2", true)]
    [InlineData("latin1_general_100_bin2", true)]
    [InlineData("Latin1_General_100_BIN", false)]
    [InlineData("SQL_Latin1_General_CP1_CI_AS", false)]
    [InlineData("Latin1_General_100_CI_AS_SC_UTF8", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_a_code_point_collation_is_the_order_the_contract_compares_in(string? collation, bool byteOrder)
    {
        Assert.Equal(byteOrder, SqlServerTextCollation.IsByteOrder(collation));
    }
}
