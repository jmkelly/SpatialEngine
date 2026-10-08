using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Configuration;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The feature face's plan handling without a database (ADR-0033): which
/// plans compare text — the one question that decides whether the store reads
/// the database's collation — and the two answers the face gives before any
/// SQL is issued. A plan over a text column or a text identity says which
/// order it wants; a plan over numbers and a bounding box never pays the
/// catalog read. A null plan is refused, and an identity lookup on a dataset
/// that declares no identity answers with nothing rather than failing.
/// </summary>
public sealed class PostgisFeaturesQueryTests
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

    private static DatasetDescription Describe(IReadOnlyList<string> identities) =>
        new(Qualified, "public", "places", "geom", 4326, "POINT", 3, identities, Schema);

    private static PostgisFeatures FeaturesWith(DatasetDescription description)
    {
        var storage = new PostgisStorage(
            PostgisConnectionConfiguration.FromConnectionString("Host=localhost;Database=spatial"));
        storage.Descriptions.Set(Dataset, new PostgisDatasetFacts(Dataset, description, new Dictionary<string, string>()));
        return new PostgisFeatures(storage, storage.Catalogue);
    }

    [Fact]
    public async Task A_null_plan_is_refused_before_anything_is_described()
    {
        var features = FeaturesWith(Describe(["id"]));

        await Assert.ThrowsAsync<ArgumentNullException>(() => features.QueryAsync(Dataset, null!, CancellationToken.None));
    }

    [Fact]
    public async Task An_identity_lookup_on_a_dataset_with_no_identity_answers_with_nothing()
    {
        // A dataset that declares no identity column has no durable key to
        // address a row by, so the lookup answers with the empty page rather
        // than issuing SQL it cannot state (ADR-0140). The description is held
        // in the store's cache and the connection string names no server, so a
        // test that reached the database would fail rather than pass.
        var features = FeaturesWith(Describe([]));
        var query = new FeatureQuery(Ids: [new FeatureId("1")]);

        var batches = await features.QueryAsync(Dataset, query, CancellationToken.None);

        var batch = Assert.Single(batches);
        Assert.Empty(batch.Features);
        Assert.Same(Schema, batch.Schema);
    }

    [Theory]
    [InlineData("city = 'Oslo'", true)]
    [InlineData("population > 100", false)]
    public void A_text_predicate_is_what_makes_a_plan_compare_text(string filter, bool expected)
    {
        Assert.True(FeatureFilterText.TryParse(filter, out var predicate, out var error), error);

        Assert.Equal(expected, PostgisFeatures.ComparesText(Describe(["id"]), new FeatureQuery(Where: predicate)));
    }

    [Fact]
    public void An_identity_restriction_over_a_text_identity_compares_text()
    {
        var query = new FeatureQuery(Ids: [new FeatureId("Oslo")]);

        Assert.True(PostgisFeatures.ComparesText(Describe(["city"]), query));
        Assert.False(PostgisFeatures.ComparesText(Describe(["id"]), query));
    }

    [Fact]
    public void An_unrestricted_plan_compares_no_text()
    {
        Assert.False(PostgisFeatures.ComparesText(Describe(["city"]), FeatureQuery.All));
    }
}
