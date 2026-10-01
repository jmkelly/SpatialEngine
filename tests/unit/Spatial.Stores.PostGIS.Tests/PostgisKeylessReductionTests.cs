using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// What a <em>reduction</em> may push on a dataset that declares no identity
/// column, and what a <em>read</em> may not (ADR-0184 §1 and §2).
///
/// <para>
/// The two halves of one dataset shape, and the reason they differ is what the
/// rule turns on: ADR-0097 §1 declines a pushed <c>WHERE</c> on a dataset whose
/// features are named by the ordinal of the read, because returning only the
/// matching rows renumbers every feature after the first match. A count, a
/// distinct set and a grouped reduction return <em>values</em> — no feature,
/// and so no number to renumber — and the whole table read a keyless layer
/// cost for them was pure waste (the query baseline: the same
/// <c>SELECT COUNT(*)</c> that cost a 31 MB whole read allocates 0.01 MB
/// through the database).
///
/// <para>
/// The read is unchanged and pinned here beside it: a page on a keyless
/// dataset is still a whole read, because the features it returns are named by
/// the ordinal of the read and only a whole read keeps those ordinals stable
/// across pages. That is the price of a created dataset being keyless
/// (ADR-0149 §3), and it is paid on purpose — so the decline is stated here
/// rather than discovered per call.
/// </para>
/// </summary>
public sealed class PostgisKeylessReductionTests
{
    private const string Qualified = "public.places";

    private static PostgisDatasetName Dataset
    {
        get
        {
            Assert.True(PostgisDatasetName.TryParse(Qualified, out var name, out var reason), reason);
            return name;
        }
    }

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64, false),
        new FieldDefinition("city", AttributeKind.String, false),
        new FieldDefinition("population", AttributeKind.Int64, true),
        new FieldDefinition("geom", AttributeKind.Geometry, false)]);

    /// <summary>
    /// The same schema described the way <c>CreateAsync</c> leaves a table whose
    /// defining batch has no integer identity field: no primary key, so no
    /// feature identity (ADR-0147 keeps it out of everything the contract
    /// reads).
    /// </summary>
    private static DatasetDescription Keyless => new(
        "public.places",
        "public",
        "places",
        "geom",
        4326,
        "POINT",
        3,
        [],
        Schema);

    private static readonly FeatureQuery Restricted = new(
        Where: new Predicate.Compare(
            new FieldRef("population"),
            ComparisonOperator.GreaterOrEqual,
            Literal.FromInteger("200")));

    [Fact]
    public void A_reduction_pushes_the_restriction_of_a_dataset_with_no_identity_column()
    {
        var parameters = new List<object?>();

        var where = PostgisPlanQueries.Reduction(Dataset, Keyless, Restricted, PostgisTextOrder.Locale, parameters);

        // The one the count carries: a number, and no column of rows to renumber.
        Assert.Equal("\"population\" >= @p0", where);
        Assert.Equal([200L], parameters);
        Assert.Equal(
            "SELECT COUNT(*) FROM \"public\".\"places\" WHERE \"population\" >= @p0",
            PostgisPlanQueries.Count(Dataset, where));
    }

    [Fact]
    public void A_reduction_pushes_the_box_and_the_clause_together()
    {
        var plan = Restricted with { BoundingBox = new BoundingBox(0, 0, 10, 10) };
        var parameters = new List<object?>();

        var where = PostgisPlanQueries.Reduction(Dataset, Keyless, plan, PostgisTextOrder.Locale, parameters);

        Assert.Equal(
            "(\"geom\" && ST_MakeEnvelope(@p0, @p1, @p2, @p3, 4326)) AND (\"population\" >= @p4)",
            where);
        Assert.Equal([0d, 0d, 10d, 10d, 200L], parameters);
    }

    [Fact]
    public void A_reduction_declines_an_identity_restriction_the_dataset_cannot_name()
    {
        // The one restriction a keyless dataset cannot carry: `Ids` name features
        // by the identity columns, and there are none. A count that pushed what
        // it could of this would be a count of the wrong row set, so the whole
        // reduction keeps its restriction in the caller.
        var plan = Restricted with { Ids = [new FeatureId("1")] };
        var parameters = new List<object?>();

        Assert.Null(PostgisPlanQueries.Reduction(Dataset, Keyless, plan, PostgisTextOrder.Locale, parameters));
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_reduction_of_an_empty_identity_list_is_the_empty_set()
    {
        // No ids name no features, and nothing has to be named to say so: the
        // reduction is `FALSE`, and the count of it is zero.
        var parameters = new List<object?>();

        var where = PostgisPlanQueries.Reduction(
            Dataset, Keyless, new FeatureQuery(Ids: []), PostgisTextOrder.Locale, parameters);

        Assert.Equal("FALSE", where);
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_read_of_the_same_dataset_still_declines_the_restriction_and_the_page()
    {
        var parameters = new List<object?>();
        var ordered = Restricted with { Order = [new OrderTerm("population", SortDirection.Descending)] };

        Assert.Null(PostgisPlanQueries.Predicate(Dataset, Keyless, ordered, PostgisTextOrder.Locale, parameters));
        Assert.Null(PostgisPlanQueries.Order(ordered.Order, Keyless.IdColumns, Keyless.Schema, PostgisTextOrder.Locale));
        Assert.False(PostgisPlanReader.Pushed(where: null, ordered, order: null));
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_reduction_on_an_identity_carrying_dataset_pushes_exactly_what_the_read_pushes()
    {
        // The two are one compiler over one plan; the keyless bail is the whole
        // of the difference, so a keyed dataset cannot tell them apart.
        var description = Keyless with { IdColumns = ["id"] };

        var read = PostgisPlanQueries.Predicate(Dataset, description, Restricted, PostgisTextOrder.Locale, new List<object?>());
        var reduction = PostgisPlanQueries.Reduction(Dataset, description, Restricted, PostgisTextOrder.Locale, new List<object?>());

        Assert.Equal("\"population\" >= @p0", read);
        Assert.Equal(read, reduction);
    }
}
