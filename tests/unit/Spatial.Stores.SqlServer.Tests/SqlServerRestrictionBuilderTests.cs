using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The restriction a plan pushes into the <c>WHERE</c> (ADR-0038): the
/// identity restriction as one OR-group per requested tuple with one bound
/// parameter per value, combined with the attribute predicate. An empty id set
/// selects nothing, and a plan with no restriction selects everything.
/// </summary>
public sealed class SqlServerRestrictionBuilderTests
{
    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("code", AttributeKind.String, true),
        new FieldDefinition("population", AttributeKind.Int64, true),
    ]);

    [Fact]
    public void No_identity_restriction_without_requested_ids()
    {
        Assert.Null(SqlServerRestrictionBuilder.Identity(Description(), null, []));
    }

    [Fact]
    public void No_identity_restriction_without_an_identity_column()
    {
        var parameters = new List<object?>();
        Assert.Null(SqlServerRestrictionBuilder.Identity(Description([]), [new FeatureId("1")], parameters));
        Assert.Empty(parameters);
    }

    [Fact]
    public void An_empty_id_set_selects_nothing()
    {
        var parameters = new List<object?>();
        Assert.Equal("(1 = 0)", SqlServerRestrictionBuilder.Identity(Description(), [], parameters));
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_single_id_is_a_single_conjunction_binding_each_value_in_order()
    {
        var parameters = new List<object?>();
        var sql = SqlServerRestrictionBuilder.Identity(
            Description(), [new FeatureId("7")], parameters);

        Assert.Equal("[id] = @p0", sql);
        Assert.Equal([7L], parameters);
    }

    [Fact]
    public void Several_ids_are_one_or_group_per_tuple_numbered_after_what_is_bound()
    {
        var parameters = new List<object?> { 1000L };
        var sql = SqlServerRestrictionBuilder.Identity(
            Description(), [new FeatureId("1"), new FeatureId("2")], parameters);

        Assert.Equal("([id] = @p1 OR [id] = @p2)", sql);
        Assert.Equal([1000L, 1L, 2L], parameters);
    }

    [Fact]
    public void A_composite_id_binds_one_parameter_per_column_in_id_column_order()
    {
        var parameters = new List<object?>();
        var sql = SqlServerRestrictionBuilder.Identity(
            Description(["id", "code"]), [new FeatureId("7|x")], parameters);

        Assert.Equal("[id] = @p0 AND [code] = @p1", sql);
        Assert.Equal([7L, "x"], parameters);
    }

    [Fact]
    public void A_plan_with_no_restriction_selects_everything()
    {
        Assert.Null(SqlServerRestrictionBuilder.Predicate(Description(), FeatureQuery.All, []));
    }

    [Fact]
    public void An_identity_plan_pushes_the_identity_restriction()
    {
        var parameters = new List<object?>();
        var sql = SqlServerRestrictionBuilder.Predicate(
            Description(), new FeatureQuery(Ids: [new FeatureId("7")]), parameters);

        Assert.Equal("[id] = @p0", sql);
        Assert.Equal([7L], parameters);
    }

    [Fact]
    public void Identity_and_attribute_restrictions_combine_as_a_conjunction()
    {
        Assert.True(
            FeatureFilterText.TryParse("population > 1000", out var where, out var error), error);
        var parameters = new List<object?>();
        var sql = SqlServerRestrictionBuilder.Predicate(
            Description(), new FeatureQuery([new FeatureId("7")], where), parameters);

        Assert.Equal("([id] = @p0) AND ([population] > @p1)", sql);
        Assert.Equal([7L, 1000L], parameters);
    }

    private static DatasetDescription Description(IReadOnlyList<string>? idColumns = null) =>
        new("dbo.places", "dbo", "places", "geom", 4326, "Point", 0, idColumns ?? ["id"], Schema);
}
