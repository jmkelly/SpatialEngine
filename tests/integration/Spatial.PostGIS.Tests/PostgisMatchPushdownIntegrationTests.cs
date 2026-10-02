using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The feature-match envelope against a live PostGIS (SpatialEngine-u2x.11,
/// ADR-0110): the plan the GeoServices match loop compiles — the identities,
/// the attribute clause (the Esri <c>where</c> grammar and the <c>time</c>
/// extent) and the query geometry's envelope — is a restricted
/// <c>SELECT</c>, and it answers the same features the whole-dataset
/// read-and-match answers.
///
/// <para>
/// Two assertions, one per half of the claim. The call count says the table
/// was not read to be filtered afterwards; the answer comparison says the rows
/// that came back are the rows the scan-and-match path would have kept. The
/// reference side is the shared reference executor over the same table, with
/// the identity restriction applied to the identity column — a scan names its
/// features by ordinal (ADR-0097), so the reference cannot read the
/// restriction off the feature identity.
/// </para>
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisMatchPushdownIntegrationTests : IClassFixture<PostgisDatabaseFixture>
{
    private readonly PostgisDatabaseFixture _fixture;

    public PostgisMatchPushdownIntegrationTests(PostgisDatabaseFixture fixture) => _fixture = fixture;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("city", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("geom", AttributeKind.Geometry, nullable: true),
    ]);

    private static readonly long Earlier = 1_000_000_000_000;
    private static readonly long Later = 1_700_000_000_000;

    /// <summary>The <c>time</c> extent as the adapter compiles it: a date is inside the bounds or absent.</summary>
    private static Predicate.Some TimeExtent() => new Predicate.Some(
    [
        new Predicate.IsNull(new FieldRef("observed"), Negated: false),
        new Predicate.Every(
        [
            new Predicate.Compare(new FieldRef("observed"), ComparisonOperator.GreaterOrEqual, Literal.FromMilliseconds(Earlier)),
            new Predicate.Compare(new FieldRef("observed"), ComparisonOperator.LessOrEqual, Literal.FromMilliseconds(Later)),
        ]),
    ]);

    [SkippableFact]
    public async Task The_envelope_read_restricts_the_table_and_agrees_with_the_whole_dataset_read()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.envelope_{Guid.NewGuid().ToString("N")[..8]}";
        var features = new[]
        {
            Row(1, "alpha", 100, Earlier, Point(1, 1)),
            Row(2, "beta", 200, Later, Point(2, 2)),
            Row(3, "gamma", null, null, Point(30, 30)),
            Row(4, "delta", 400, null, Polygon(20, 20, 40, 40)),
        };
        await WithIdentityTable(context, dataset);
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, features));

        var plan = new FeatureQuery(
            Ids: [new FeatureId("2"), new FeatureId("4")],
            Where: TimeExtent(),
            BoundingBox: new BoundingBox(0, 0, 10, 10));

        var store = new CountingStore(context.Store);
        var pushed = await store.QueryAsync(dataset, plan);
        var served = pushed.Features.Select(feature => feature["id"].Int64Value).ToArray();

        // The table was not read to be filtered afterwards: one restricted
        // read, no scan.
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);

        // And the rows that came back are the ones the scan-and-match path
        // keeps: inside the box, dated inside the extent or undated, and
        // carrying one of the requested identities.
        var scanned = await context.Store.ScanAsync(dataset);
        var whole = scanned.SelectMany(batch => batch.Features).ToArray();
        var reference = FeaturePlanExecutor
            .Select(scanned[0].Schema, whole, plan with { Ids = null }, CancellationToken.None)
            .Where(feature => feature["id"].Int64Value is 2L or 4L)
            .Select(feature => feature["id"].Int64Value)
            .ToArray();

        Assert.Equal([2L], reference);
        Assert.Equal(reference, served);
    }

    /// <summary>
    /// A table with a primary key, which is what makes the dataset's features
    /// named by their identity column rather than by the ordinal of a read
    /// (ADR-0037, ADR-0097) — the precondition for an identity restriction to
    /// be expressible at all.
    /// </summary>
    private static Task WithIdentityTable(PostgisTestContext context, string dataset) =>
        context.ExecuteAsync(
            $"CREATE TABLE {dataset} ("
            + "\"id\" bigint PRIMARY KEY, \"city\" text, \"population\" bigint, "
            + "\"observed\" timestamptz, \"geom\" geometry(Geometry, 4326))");

    [SkippableFact]
    public async Task A_where_clause_and_a_box_reach_sql_as_one_restricted_select()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.envelope2_{Guid.NewGuid().ToString("N")[..8]}";
        var features = new[]
        {
            Row(1, "alpha", 100, Earlier, Point(1, 1)),
            Row(2, "beta", 200, Later, Point(2, 2)),
        };
        await WithIdentityTable(context, dataset);
        await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, features));

        // The plan a served query with an envelope compiles to, read through
        // the store: a spatial predicate and an attribute predicate over the
        // discovered geometry column, answered with the count the database
        // computed rather than by counting rows the engine fetched.
        var store = new CountingStore(context.Store);
        var page = await store.QueryAsync(
            dataset,
            new FeatureQuery(
                Where: new Predicate.Compare(new FieldRef("population"), ComparisonOperator.GreaterOrEqual, Literal.FromInteger("200")),
                BoundingBox: new BoundingBox(0, 0, 10, 10)));

        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);
        Assert.Equal([2L], page.Features.Select(feature => feature["id"].Int64Value));
        Assert.Equal(1, page.TotalCount);
    }

    private static Feature Row(long id, string city, long? population, long? observed, IGeometry geometry) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(city),
            population is { } count ? AttributeValue.FromInt64(count) : AttributeValue.Null,
            observed is { } millis
                ? AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(millis))
                : AttributeValue.Null,
            AttributeValue.FromGeometry(geometry),
        ]);

    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y, CoordinateReference.Epsg(4326));

    private static Polygon Polygon(double minX, double minY, double maxX, double maxY) => GeometryFactory.CreatePolygon(
    [
        new Coordinate(minX, minY),
        new Coordinate(maxX, minY),
        new Coordinate(maxX, maxY),
        new Coordinate(minX, maxY),
        new Coordinate(minX, minY),
    ]);

    /// <summary>The store under a call count, so "did it read the table?" is an assertion and not a claim.</summary>
    private sealed class CountingStore(IFeatureStore inner) : IFeatureStore
    {
        public int Scans { get; private set; }

        public int Queries { get; private set; }

        public async Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            Scans++;
            return await inner.ScanAsync(dataset, cancellationToken);
        }

        public async Task<FeatureQueryPage> QueryAsync(
            string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            Queries++;
            return await inner.QueryAsync(dataset, query, cancellationToken);
        }

        public Task<int> WriteAsync(
            string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(dataset, batch, transaction, cancellationToken);
    }
}
