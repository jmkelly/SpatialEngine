using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Operations.NetTopologySuite;
using Spatial.Querying;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The feature-match envelope compiled onto the store's query surface
/// (SpatialEngine-u2x.11): <c>objectIds</c>, <c>where</c>, <c>time</c> and the
/// query geometry's envelope go to the store as a <see cref="FeatureQuery"/>,
/// so a feature query no longer reads the whole table to filter it in the
/// adapter.
///
/// <para>
/// The claim is not "the pushed read is cheaper" but "the pushed read answers
/// the same question", and that is asserted from both sides over one
/// conformance fixture: for every served query the pushed result is a
/// <em>subset</em> of the whole-dataset match, it agrees with it on every
/// matched feature, and the response the writer builds from the two is
/// byte-identical. The in-memory matcher stays the verification path — it is
/// what the pushed rows are re-tested against, and a store that returns more
/// than it was asked for is caught by it rather than answered from.
/// </para>
///
/// <para>
/// The refusal half is pinned too: a layer whose <c>OBJECTID</c> is the scan
/// ordinal keeps the scan (ADR-0097), a <c>uniqueIds</c> request and an
/// unsupported <c>spatialRel</c> are still refused by name rather than
/// answered from a narrower row set, and nothing here falls back to a scan
/// after a pushdown has been attempted.
/// </para>
/// </summary>
public sealed class FeatureMatchPushdownTests
{
    private static QueryServices Services => new(Operations, Relations, new NtsGeometryMeasures(), Transforms, Transforms);

    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly long Earlier = 1_000_000_000_000;
    private static readonly long Later = 1_700_000_000_000;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>The same layer without an identity column: its <c>OBJECTID</c> is the scan ordinal.</summary>
    private static DatasetDescription OrdinalLayer() =>
        new("demo.sensors", "demo", "sensors", "geometry", 4326, "Point", Rows.Count, [], Schema);

    private static DatasetDescription Layer() =>
        new("demo.sensors", "demo", "sensors", "geometry", 4326, "Point", Rows.Count, ["id"], Schema);

    private static Feature Row(long id, string name, long? population, long? observed, IGeometry? geometry) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(name),
            population is { } count ? AttributeValue.FromInt64(count) : AttributeValue.Null,
            observed is { } millis ? AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(millis)) : AttributeValue.Null,
            geometry is null ? AttributeValue.Null : AttributeValue.FromGeometry(geometry),
        ]);

    private static readonly IReadOnlyList<Feature> Rows =
    [
        Row(1, "alpha", 100, Earlier, Point(1, 1)),
        Row(2, "beta", 200, Later, Point(2, 2)),
        Row(3, "gamma", null, null, Point(30, 30)),
        Row(4, "delta", 400, null, Polygon(20, 20, 40, 40)),
        Row(5, "epsilon", 500, Earlier, null),
    ];

    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y, Crs4326);

    private static Polygon Polygon(double minX, double minY, double maxX, double maxY) => GeometryFactory.CreatePolygon(
    [
        new Coordinate(minX, minY),
        new Coordinate(maxX, minY),
        new Coordinate(maxX, maxY),
        new Coordinate(minX, maxY),
        new Coordinate(minX, minY),
    ]);

    /// <summary>
    /// The conformance fixture: one served query per combination of the
    /// envelope's members, each of which the match path is the answer for
    /// (a <c>time</c>, an <c>objectIds</c> or a topology relation is what
    /// keeps the paged store path out of the way, ADR-0098 §7).
    /// </summary>
    public static IEnumerable<object[]> Envelope() =>
        Cases.Select(fixture => new object[] { fixture.Name, fixture.Values });

    private static readonly (string Name, (string Key, string Value)[] Values)[] Cases =
    [
        ("objectIds alone", [("objectIds", "2,4")]),
        ("objectIds that match nothing", [("objectIds", "99")]),
        ("objectIds and a where clause", [("objectIds", "1,2,3,4,5"), ("where", "population >= 200")]),
        ("objectIds and the envelope box", [("objectIds", "1,2,3,4,5"), ("geometry", "0,0,10,10")]),
        ("objectIds, where and the box", [("objectIds", "1,2,3,4,5"), ("where", "population > 100"), ("geometry", "0,0,35,35")]),
        ("objectIds and envelope intersects", [("objectIds", "1,2,3,4,5"), ("geometry", "0,0,10,10"), ("spatialRel", "esriSpatialRelEnvelopeIntersects")]),
        ("objectIds and contains", [("objectIds", "1,2,3,4,5"), ("geometry", "0,0,45,45"), ("spatialRel", "esriSpatialRelContains")]),
        ("objectIds and intersects", [("objectIds", "1,2,3,4,5"), ("geometry", "0,0,45,45"), ("spatialRel", "esriSpatialRelIntersects")]),
        ("objectIds and a box that matches nothing", [("objectIds", "1,2,3,4,5"), ("geometry", "80,80,90,90")]),
        ("a time extent over dated features", [("objectIds", "1,2,3,4,5"), ("time", "1000000000000,1700000000000")]),
        ("a time extent before every date", [("objectIds", "1,2,3,4,5"), ("time", "1,2")]),
        ("a time instant", [("objectIds", "1,2,3,4,5"), ("time", "1700000000000")]),
        ("time, where and the box", [("objectIds", "1,2,3,4,5"), ("time", "1000000000000,1700000000000"), ("where", "population >= 200"), ("geometry", "0,0,45,45")]),
        ("a where clause on the synthetic OBJECTID", [("objectIds", "1,2,3,4,5"), ("where", "OBJECTID = 3")]),
        ("a negated where clause", [("objectIds", "1,2,3,4,5"), ("where", "population <> 200")]),
    ];

    /// <summary>
    /// The subset-and-agree property, over the fixture: the pushed rows are a
    /// subset of the whole-dataset match, they agree on every matched feature,
    /// and the two write byte-identical responses.
    /// </summary>
    [Theory]
    [MemberData(nameof(Envelope))]
    public async Task The_pushed_match_is_a_subset_of_the_scanned_match_and_agrees_with_it(
        string fixture, (string Key, string Value)[] values)
    {
        var query = await ParseAsync(values);
        var store = new RecordingStore(Rows);
        var spec = Spec(Layer(), store, query);
        _ = fixture;

        var pushed = await FeatureSpatialMatcher.MatchAsync(spec, CancellationToken.None);

        // The rows the store returned are a subset of the dataset, not the
        // dataset: the scan is never read, and the plan carries the envelope.
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);
        Assert.NotNull(store.LastPlan);

        var scanned = await FeatureSpatialMatcher.ScanAndMatchAsync(Spec(Layer(), store, query), CancellationToken.None);

        var agreed = pushed.Select(match => match.ObjectId).ToHashSet();
        Assert.Equal(agreed, scanned.Select(match => match.ObjectId).ToHashSet());
        Assert.Equal(scanned, pushed);
        Assert.Equal(await BodyAsync(Layer(), scanned, query), await BodyAsync(Layer(), pushed, query));
    }

    /// <summary>
    /// The store is asked a plan and not a scan, and the plan names every
    /// part of the envelope: the identities, the attribute clause (the Esri
    /// <c>where</c> and the compiled <c>time</c>), and the box.
    /// </summary>
    [Fact]
    public async Task The_envelope_becomes_the_identity_the_predicate_and_the_box()
    {
        var query = await ParseAsync(
            ("objectIds", "1,2,3,4,5"),
            ("where", "population > 100"),
            ("time", $"{Earlier},{Later}"),
            ("geometry", "0,0,45,45"),
            ("spatialRel", "esriSpatialRelIntersects"));
        var store = new RecordingStore(Rows);

        await FeatureSpatialMatcher.MatchAsync(Spec(Layer(), store, query), CancellationToken.None);

        var plan = Assert.IsType<FeatureQuery>(store.LastPlan);
        Assert.Equal(["1", "2", "3", "4", "5"], plan.Ids!.Select(id => id.Value));
        var box = Assert.IsType<Spatial.Contracts.BoundingBox>(plan.BoundingBox);
        Assert.Equal(0, box.MinX);
        Assert.Equal(45, box.MaxY);

        // The predicate is the conjunction of the where clause and the time
        // extent, both over columns the dataset really has.
        var where = Assert.IsType<Predicate.Every>(plan.Where);
        Assert.Equal(2, where.Terms.Count);
        Assert.Contains("population", plan.Where.Fields().Select(field => field.Name));
        Assert.Contains("observed", plan.Where.Fields().Select(field => field.Name));
    }

    /// <summary>
    /// A layer whose <c>OBJECTID</c> is the scan ordinal is not pushable at
    /// all: a store that returned only the matching rows would renumber that
    /// key, so the same feature would come back with an object id that depends
    /// on the query (ADR-0097).
    /// </summary>
    [Fact]
    public async Task An_ordinal_object_id_keeps_the_whole_dataset_match()
    {
        var query = await ParseAsync(("objectIds", "2"), ("where", "population > 100"), ("geometry", "0,0,45,45"));
        var store = new RecordingStore(Rows);
        var spec = Spec(OrdinalLayer(), store, query);

        var matches = await FeatureSpatialMatcher.MatchAsync(spec, CancellationToken.None);

        Assert.Equal(1, store.Scans);
        Assert.Equal(0, store.Queries);
        Assert.Equal([2L], matches.Select(match => match.ObjectId));
    }

    /// <summary>
    /// <c>uniqueIds</c> names a string-or-guid identity field, which a layer
    /// with a durable integer <c>OBJECTID</c> does not have — so the request is
    /// refused by name. Pushing the rest of the envelope must not turn that
    /// refusal into an empty answer by returning no feature to refuse on.
    /// </summary>
    [Fact]
    public async Task A_unique_id_request_is_still_refused_by_name_rather_than_answered_from_a_narrower_read()
    {
        var query = await ParseAsync(("objectIds", "99"), ("uniqueIds", "alpha"));
        var store = new RecordingStore(Rows);

        var failure = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeatureSpatialMatcher.MatchAsync(Spec(Layer(), store, query), CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains("uniqueIds", failure.Message);
        Assert.Equal(0, store.Queries);
    }

    /// <summary>
    /// An unsupported <c>spatialRel</c> is refused by name, on a read that
    /// matches nothing: the refusal must not depend on a feature coming back
    /// for the matcher to reject.
    /// </summary>
    [Fact]
    public async Task An_unsupported_spatial_rel_is_refused_by_name_and_keeps_the_scan()
    {
        var query = (await ParseAsync(("objectIds", "1,2,3,4,5"), ("geometry", "80,80,90,90"))) with
        {
            SpatialRel = "esriSpatialRelDisjoint",
        };
        var store = new RecordingStore(Rows);

        var failure = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeatureSpatialMatcher.MatchAsync(Spec(Layer(), store, query), CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains("esriSpatialRelDisjoint", failure.Message);
        Assert.Equal(1, store.Scans);
        Assert.Equal(0, store.Queries);
    }

    /// <summary>
    /// The matcher is the verification path, not a fallback: a store that
    /// answers a plan with more rows than the plan selects is caught by the
    /// per-feature match rather than served. The attribute clause is the one
    /// facet the facade does not re-test, because a store that answered a
    /// different row set for it would look right either way (ADR-0097); the
    /// identities, the time extent and the spatial relation are.
    /// </summary>
    [Fact]
    public async Task The_matcher_still_verifies_every_row_the_store_returns()
    {
        var query = await ParseAsync(("objectIds", "1,2,3,4,5"), ("geometry", "0,0,10,10"));
        var store = new RecordingStore(Rows, answerEverything: true);

        var matches = await FeatureSpatialMatcher.MatchAsync(Spec(Layer(), store, query), CancellationToken.None);

        // The whole layer came back and only the two rows the box admits are
        // served: the far point, the far polygon and the feature with no
        // geometry were rejected here, not by the plan.
        Assert.Equal([1L, 2L], matches.Select(match => match.ObjectId));
    }

    /// <summary>
    /// A <c>time</c> extent over a layer with no date field matches every
    /// feature (ArcGIS Server ignores <c>time</c> on a layer with no
    /// time-aware field), so there is nothing to push for it and the rest of
    /// the envelope still is.
    /// </summary>
    [Fact]
    public async Task A_layer_with_no_date_field_pushes_no_time_predicate()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);
        var rows = new[]
        {
            new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(1), AttributeValue.FromGeometry(Point(1, 1))]),
        };
        var layer = new DatasetDescription("demo.sensors", "demo", "sensors", "geometry", 4326, "Point", 1, ["id"], schema);
        var store = new RecordingStore(rows, schema);
        var query = await ParseAsync(("objectIds", "1"), ("time", "1000000000000,1700000000000"));

        var matches = await FeatureSpatialMatcher.MatchAsync(Spec(layer, store, query), CancellationToken.None);

        Assert.Equal([1L], matches.Select(match => match.ObjectId));
        Assert.Equal(0, store.Scans);
        Assert.Null(store.LastPlan?.Where);
    }

    /// <summary>
    /// The served path end to end: a feature query with an envelope reads the
    /// store's plan and never the table, and the response is the one the
    /// scan-and-match path wrote.
    /// </summary>
    [Fact]
    public async Task A_feature_query_reads_the_store_plan_and_never_the_table()
    {
        var store = new RecordingStore(Rows);
        var body = await ServedAsync(Layer(), store, await ParseAsync(
            ("objectIds", "1,2,3,4,5"),
            ("where", "population >= 200"),
            ("geometry", "0,0,45,45"),
            ("f", "json")));

        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);
        Assert.Equal([2, 4], body.GetProperty("features").EnumerateArray()
            .Select(feature => feature.GetProperty("attributes").GetProperty("id").GetInt32()));
    }

    /// <summary>
    /// A request with nothing to restrict takes the whole-dataset read: a
    /// plan with no identity, no predicate and no box is the scan, and asking
    /// the store for it as a query would be a round trip that reads every row
    /// anyway.
    /// </summary>
    [Fact]
    public async Task A_request_with_nothing_to_restrict_keeps_the_whole_dataset_match()
    {
        var store = new RecordingStore(Rows);

        var matches = await FeatureSpatialMatcher.MatchAsync(Spec(Layer(), store, await ParseAsync()), CancellationToken.None);

        Assert.Equal(5, matches.Count);
        Assert.Equal(1, store.Scans);
        Assert.Equal(0, store.Queries);
    }

    /// <summary>
    /// A <c>time</c> extent is a restriction on its own: a query with nothing
    /// but <c>time</c> still reaches the store as a plan, because the date
    /// predicate is the only thing that decides which rows can match.
    /// </summary>
    [Fact]
    public async Task A_time_extent_alone_is_a_restriction()
    {
        var store = new RecordingStore(Rows);
        var query = await ParseAsync(("time", "1,2"));

        var matches = await FeatureSpatialMatcher.MatchAsync(Spec(Layer(), store, query), CancellationToken.None);

        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);
        Assert.NotNull(store.LastPlan?.Where);
        // Every date in the layer is outside the window, so nothing matches
        // except the features that carry no date at all.
        Assert.Equal([3L, 4L], matches.Select(match => match.ObjectId));
    }

    /// <summary>
    /// A designated layer's pushed <c>time</c> clause is the extent rule's
    /// overlaps shape over the designated pair, so it stays a superset of every
    /// relation the matcher can apply (ADR-0175 §5). The straddling row is the
    /// proof: the extent rule accepts it — the window falls inside the row's
    /// extent — so a clause that asked each designated field to be *inside* the
    /// window would have dropped a row the matcher serves.
    /// </summary>
    [Fact]
    public async Task A_designated_layers_pushed_time_clause_is_a_superset_of_the_extent_rule()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("begins", AttributeKind.DateTimeOffset, nullable: true),
            new FieldDefinition("ends", AttributeKind.DateTimeOffset, nullable: true),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);
        Feature Event(long id, long? begins, long? ends) => new(
            new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
            schema,
            [
                AttributeValue.FromInt64(id),
                begins is { } from ? AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(from)) : AttributeValue.Null,
                ends is { } to ? AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(to)) : AttributeValue.Null,
                AttributeValue.FromGeometry(Point(id, id)),
            ]);

        var rows = new[]
        {
            Event(1, Earlier - 1, Later + 1),
            Event(2, Earlier, null),
            Event(3, null, Later),
            Event(4, null, null),
            Event(5, Later + 1, Later + 2),
        };
        var layer = new DatasetDescription("demo.events", "demo", "events", "geometry", 4326, "Point", rows.Length, ["id"], schema)
        {
            TimeFields = new TemporalExtentFields("begins", "ends"),
        };
        var store = new RecordingStore(rows, schema);
        var query = await ParseAsync(("objectIds", "1,2,3,4,5"), ("time", $"{Earlier},{Later}"));

        var pushed = await FeatureSpatialMatcher.MatchAsync(Spec(layer, store, query), CancellationToken.None);
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);

        var scanned = await FeatureSpatialMatcher.ScanAndMatchAsync(Spec(layer, store, query), CancellationToken.None);

        Assert.Equal(scanned.Select(match => match.ObjectId), pushed.Select(match => match.ObjectId));
        Assert.Equal([1L, 2L, 3L, 4L], pushed.Select(match => match.ObjectId));
    }

    [Fact]
    public async Task A_cancelled_match_cancels_rather_than_answers()
    {
        var store = new RecordingStore(Rows);
        var spec = Spec(Layer(), store, await ParseAsync(("objectIds", "1,2,3,4,5")));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeatureSpatialMatcher.MatchAsync(spec, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    private static FeatureSpatialMatcher.QuerySpec Spec(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query) =>
        new(
            dataset,
            store,
            query,
            FeatureProjection.MatchGeometry(
                new FeatureProjection.QueryGeometryRequest(query, Crs4326, Services), CancellationToken.None),
            Services,
            EsriObjectIdScheme.For(dataset));

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        return EsriFeatureQuery.Parse(parameters, Crs4326);
    }

    /// <summary>The served response, through the whole served query path.</summary>
    private static async Task<JsonElement> ServedAsync(
        DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query)
    {
        var result = await FeatureService.QueryAsync(dataset, store, query, Services, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static async Task<string> BodyAsync(
        DatasetDescription dataset, IReadOnlyList<MatchedFeature> matches, EsriFeatureQuery query)
    {
        var result = FeatureQueryEngine.Project(dataset, matches, query, Crs4326, Services, CancellationToken.None);
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    /// <summary>
    /// A store that records what it was asked and answers with the shared
    /// reference executor — the same answer a pushing provider has to
    /// reproduce. <c>answerEverything</c> is the hostile variant that returns
    /// the whole layer whatever the plan said, so the tests can show the
    /// matcher is what the answer comes from.
    /// </summary>
    private sealed class RecordingStore(IReadOnlyList<Feature> features, FeatureSchema? schema = null, bool answerEverything = false) :
        IFeatureStore
    {
        private readonly FeatureSchema _schema = schema ?? features[0].Schema;

        public int Scans { get; private set; }

        public int Queries { get; private set; }

        public FeatureQuery? LastPlan { get; private set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scans++;
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(_schema, features)]);
        }

        public Task<FeatureQueryPage> QueryAsync(
            string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries++;
            LastPlan = query;
            return Task.FromResult(answerEverything
                ? FeaturePlanExecutor.Finish(_schema, features, query with { Ids = null, Where = null, BoundingBox = null }, cancellationToken)
                : FeaturePlanExecutor.Execute(_schema, features, query, cancellationToken));
        }

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
