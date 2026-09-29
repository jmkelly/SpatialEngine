using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The SQL the served feature-match envelope becomes (SpatialEngine-u2x.11,
/// ADR-0110): the identities, the attribute clause (the Esri <c>where</c>
/// grammar and the <c>time</c> extent) and the query geometry's envelope all
/// ride in the <c>WHERE</c> of one <c>SELECT</c>, so a feature query is an
/// index-usable read rather than a table read the engine filters afterwards.
///
/// <para>
/// This is the shape half of the claim, and it is deliberately a text
/// assertion: the conformance suite proves the same plan answers correctly,
/// while this proves it is the query that was issued at all. A dialect that
/// quietly selected the whole table and filtered in memory would pass every
/// answer comparison and fail here.
/// </para>
/// </summary>
public sealed class PostgisMatchEnvelopeTests
{
    private static readonly FeatureSchema Schema = FeatureTests.Schema(
        ("id", AttributeKind.Int64, false),
        ("city", AttributeKind.String, false),
        ("population", AttributeKind.Int64, true),
        ("observed", AttributeKind.DateTimeOffset, true),
        ("geom", AttributeKind.Geometry, true));

    private static readonly DatasetDescription Description = FeatureTests.PlacesDescription(Schema);

    private static PostgisDatasetName Dataset
    {
        get
        {
            Assert.True(PostgisDatasetName.TryParse("public.places", out var name, out var reason), reason);
            return name;
        }
    }

    [Fact]
    public void The_envelope_is_one_select_with_a_spatial_and_a_filter_predicate()
    {
        var parameters = new List<object?>();
        var plan = new FeatureQuery(
            Ids: [new FeatureId("1"), new FeatureId("2")],
            Where: new Predicate.Every(
            [
                new Predicate.Compare(new FieldRef("population"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("200")),
                new Predicate.Some(
                [
                    new Predicate.IsNull(new FieldRef("observed"), Negated: false),
                    new Predicate.Every(
                    [
                        new Predicate.Compare(new FieldRef("observed"), ComparisonOperator.GreaterOrEqual, Literal.FromMilliseconds(1_000_000_000_000)),
                        new Predicate.Compare(new FieldRef("observed"), ComparisonOperator.LessOrEqual, Literal.FromMilliseconds(1_700_000_000_000)),
                    ]),
                ]),
            ]),
            BoundingBox: new BoundingBox(0, 0, 10, 10));
        var where = PostgisPlanQueries.Predicate(Dataset, Description, plan, byteOrderText: false, parameters);

        Assert.Equal(
            "((\"id\" = @p0 OR \"id\" = @p1)) AND ((\"geom\" && ST_MakeEnvelope(@p2, @p3, @p4, @p5, 4326)) "
            + "AND (\"population\" >= @p6 AND (\"observed\" IS NULL OR (\"observed\" >= @p7 AND \"observed\" <= @p8))))",
            where);
        // Every literal is a bound value, and the two instants bind as the
        // instants they are (ADR-0097 §3), not as the numbers they came from.
        Assert.Equal(
            [
                1L,
                2L,
                0d,
                0d,
                10d,
                10d,
                200L,
                DateTimeOffset.FromUnixTimeMilliseconds(1_000_000_000_000),
                DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000),
            ],
            parameters);
    }

    [Fact]
    public void The_read_is_a_single_select_that_restricts_rather_than_a_table_read()
    {
        var parameters = new List<object?>();
        var plan = new FeatureQuery(
            Ids: [new FeatureId("7")],
            Where: new Predicate.Compare(new FieldRef("population"), ComparisonOperator.Equals, Literal.FromInteger("200")),
            BoundingBox: new BoundingBox(1, 2, 3, 4));
        var where = PostgisPlanQueries.Predicate(Dataset, Description, plan, byteOrderText: false, parameters);

        var sql = PostgisPlanQueries.Read(
            Dataset,
            ["\"id\"", "\"city\"", "\"population\"", "\"observed\"", "ST_AsEWKB(\"geom\")"],
            where,
            order: null,
            new PostgisPlanQueries.Paging(null, 0),
            parameters);

        Assert.StartsWith("SELECT \"id\", \"city\", \"population\", \"observed\", ST_AsEWKB(\"geom\") FROM \"public\".\"places\" WHERE ", sql);
        Assert.DoesNotContain(" LIMIT", sql);
        Assert.Equal(6, parameters.Count);
    }
}
