using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Operations.NetTopologySuite;
using Spatial.Querying;
using Spatial.Transformations.ProjNet;
using Nts = NetTopologySuite.Geometries;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The one spatial component the plan may carry, pinned as one
/// (SpatialEngine-8ab, ADR-0167): the plan's spatial member is the query
/// geometry's <em>envelope</em> and nothing narrower, and the DE-9IM
/// <c>spatialRel</c> verb is answered by the adapter over whatever rows the
/// box admits.
///
/// <para>
/// ADR-0074 §8 left the topology verbs adapter-side and pointed at this bead
/// for whether a store pushdown face should follow. The decision is no, and
/// these tests are the two halves of why it is checkable rather than
/// asserted: the <b>superset property</b> — every pair a served verb accepts
/// is admitted by the pushed box, which is what makes the box a pre-filter
/// rather than an answer — over all 289 ordered fixture pairs crossed with all
/// six served verbs, and the <b>cost</b> — a store that answered the relation
/// with its own spatial predicates would not answer the same question, named
/// pair by pair. A change that ever made the box narrower than a superset, or
/// a change to the served reading, fails here.
/// </para>
///
/// <para>
/// The residual is named too: a query whose answer depends on the relation
/// cannot have its <em>reductions</em> pushed, because a count taken over the
/// box is not a count of the matches.
/// <see cref="A_count_over_a_topology_query_is_the_adapter_s_count_and_not_the_rows_in_the_box"/>
/// pins that, so the cost of the decision is a test rather than a footnote.
/// </para>
/// </summary>
public sealed class SpatialRelationPushdownTests
{
    private static QueryServices Services => new(new NtsGeometryOperations(), Relations, new NtsGeometryMeasures(), Transforms, Transforms);

    private static readonly NtsGeometryRelations Relations = new();
    private static readonly ProjNetTransforms Transforms = new();
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>
    /// One feature per fixture shape, named the way the fixture table names
    /// it: the feature geometry is the unit square and every other fixture is
    /// the query geometry, walked in both directions by the pair battery.
    /// </summary>
    private static readonly Feature[] Rows =
        SpatialRelationMatrix.Names
            .Select((name, index) => new Feature(
                new FeatureId((index + 1).ToString(CultureInfo.InvariantCulture)),
                Schema,
                [
                    AttributeValue.FromInt64(index + 1),
                    AttributeValue.FromGeometry(SpatialRelationMatrix.Of(name).Geometry),
                ]))
            .ToArray();

    private static DatasetDescription Layer() =>
        new("demo.fixtures", "demo", "fixtures", "geometry", 4326, "Mixed", Rows.Length, ["id"], Schema);

    /// <summary>
    /// Every ordered fixture pair crossed with every served relation: the
    /// battery the superset property and the named divergences are measured
    /// over.
    /// </summary>
    public static TheoryData<string, string, string> Pairs() => SpatialRelationMatrix.PairCases();

    /// <summary>
    /// The plan a topology request compiles to is the query geometry's box and
    /// nothing else, and that box admits every pair the verb accepts — so the
    /// pushed read is a pre-filter whose superset property holds for all six
    /// served verbs, not a second answer to the relation.
    /// </summary>
    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task The_plan_carries_the_query_envelope_and_nothing_narrower(
        string featureName,
        string queryName,
        string spatialRel)
    {
        var spec = await SpecAsync(spatialRel, queryName);

        var plan = FeatureMatchPushdown.Compile(spec);

        var box = Assert.IsType<Spatial.Contracts.BoundingBox>(plan?.BoundingBox);
        var envelope = EnvelopeOf(queryName);
        Assert.Equal(envelope.MinX, box.MinX);
        Assert.Equal(envelope.MinY, box.MinY);
        Assert.Equal(envelope.MaxX, box.MaxX);
        Assert.Equal(envelope.MaxY, box.MaxY);

        // Nothing else narrows the read: the relation is not in the plan, and
        // no member that would reorder, page or project rides along either.
        Assert.Null(plan.Ids);
        Assert.Null(plan.Where);
        Assert.Null(plan.Projection);
        Assert.Null(plan.Order);
        Assert.Null(plan.Limit);
        Assert.Null(plan.Offset);
        Assert.Null(plan.Cursor);

        // The superset property: a pair the served verb accepts is inside the
        // box, so the box cannot drop a match the adapter would have served.
        if (Served(await QueryForAsync(spatialRel), featureName, queryName))
        {
            var pushed = new Envelope(box.MinX, box.MinY, box.MaxX, box.MaxY);
            Assert.True(
                pushed.Intersects(EnvelopeOf(featureName)),
                $"'{spatialRel}' accepts {featureName} against {queryName}, and the pushed box does not admit it.");
        }
    }

    /// <summary>
    /// The fixture the class ADR-0169 serves deliberately has to be in
    /// <em>this</em> battery, or the divergence measurement below is a
    /// statement about fixtures that cannot reach the class: a line lying
    /// wholly inside the area whose interior reaches the area's boundary —
    /// position 1 and position 4 non-empty and position 7 empty — is the pair
    /// this engine serves <c>Crosses</c> and the provider does not, and until
    /// the table carried one, the measurement walked 1,536 checks without ever
    /// asking that question. The fixture is found by the geometry rather than
    /// by name, so a fixture that reaches the class is what makes this pass.
    /// </summary>
    [Fact]
    public void The_battery_carries_a_line_inside_an_area_touching_its_boundary_from_inside()
    {
        var square = SpatialRelationMatrix.Of(SpatialRelationMatrix.Feature).ReferenceGeometry;

        var inTheClass = SpatialRelationMatrix.Names
            .Select(SpatialRelationMatrix.Of)
            .Where(fixture => fixture.ReferenceGeometry.Dimension == Nts.Dimension.Curve)
            .Where(fixture => TouchesTheEdgeFromInside(fixture.ReferenceGeometry, square))
            .Select(fixture => fixture.Name)
            .ToArray();

        Assert.NotEmpty(inTheClass);
    }

    /// <summary>
    /// Whether the pair is the ADR-0169 class, read off the matrix with the
    /// area on the left: position 1 (interior against interior) and position 4
    /// (the area's boundary against the line's interior) are non-empty and
    /// position 7 (the area's exterior against the line's interior) is empty.
    /// The served <c>T**T*****</c> mask asks the first two and the provider's
    /// predicate asks all three.
    /// </summary>
    private static bool TouchesTheEdgeFromInside(Nts.Geometry line, Nts.Geometry square)
    {
        var matrix = square.Relate(line).ToString();

        return matrix[0] != 'F' && matrix[3] != 'F' && matrix[6] == 'F';
    }

    /// <summary>
    /// The cost of the decision, measured: the served reading against the
    /// provider's own named predicates over all 289 ordered pairs and six
    /// verbs. Every pair the two disagree on is named here, so a store that
    /// answered a pushed relation with its own spatial predicates is a store
    /// that does not answer the same question — and a divergence that moves is
    /// a failing test (ADR-0166 measured the same disagreement over a wider
    /// battery; this is the adapter's own fixture table).
    ///
    /// <para>The result is not zero, and saying so is the point: the battery
    /// now carries <c>line-touch-edge</c> — the line lying wholly inside the
    /// area whose interior reaches the area's boundary — so the pair ADR-0169
    /// serves deliberately is measured here rather than only characterised in
    /// <c>NtsRelateCellSemanticsTests</c>. It is the one single-part pair the
    /// served table and the provider's predicate answer differently, and the
    /// list is asserted exactly, so a battery that stopped covering the class
    /// and a battery that started covering more of it both fail.</para>
    /// </summary>
    [Fact]
    public async Task A_store_answering_the_relation_with_its_own_predicates_diverges_on_exactly_the_line_touching_the_edge_class()
    {
        var queries = new Dictionary<string, EsriFeatureQuery>(StringComparer.Ordinal);
        foreach (var spatialRel in SpatialRelationMatrix.Relations)
        {
            queries[spatialRel] = await QueryForAsync(spatialRel);
        }

        var measured = new List<string>();
        var checks = 0;
        foreach (var featureName in SpatialRelationMatrix.Names)
        {
            foreach (var queryName in SpatialRelationMatrix.Names)
            {
                var feature = SpatialRelationMatrix.Of(featureName);
                var query = SpatialRelationMatrix.Of(queryName);
                foreach (var spatialRel in SpatialRelationMatrix.Relations)
                {
                    checks++;
                    if (Served(queries[spatialRel], featureName, queryName)
                        != StoreSideVerdict(feature, query, spatialRel))
                    {
                        measured.Add($"{spatialRel}|{featureName}|{queryName}");
                    }
                }
            }
        }

        // 17 fixtures, ordered both ways, six served verbs.
        Assert.Equal(1734, checks);
        Assert.Equal(DocumentedDivergences, measured.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// The single-part pairs the served reading and the provider's own
    /// predicates disagree on, as <c>relation|feature|query</c>, carrying why.
    /// One row, and it is the class ADR-0169 serves deliberately: the served
    /// <c>T**T*****</c> mask asks that the interiors meet and that the line's
    /// interior reach the area's boundary, both of which hold; the predicate
    /// also asks that the line reach the area's <em>exterior</em>, which it
    /// never does, and answers false. With the area on the left the served
    /// answer is <c>true</c> — with the line on the left the mask is
    /// <c>T*T******</c>, whose position 3 is empty for this pair, so it is
    /// <c>false</c> there and there is no divergence to record. The wider
    /// battery (multi-part spellings and the 0-D class) is
    /// <c>NtsRelateCellSemanticsTests</c>.
    /// </summary>
    private static readonly string[] DocumentedDivergences =
        [$"{EsriFeatureQuery.Crosses}|{SpatialRelationMatrix.Feature}|line-touch-edge"];

    /// <summary>
    /// …and the second place a store's own predicates part company with the
    /// served table is a collection spelling (ADR-0166 measured 16 divergences
    /// over its wider battery, all <c>Crosses</c>): the served table keys
    /// on the collection's dimension and names no mask for a pair involving a
    /// point, while a provider's predicate is component-wise over a
    /// <c>MultiPoint</c> and answers true when one of its points crosses.
    /// Pinned here as the reason the term is refused, not re-measured: this
    /// suite walks the single-part fixture table, whose one divergence is named
    /// above, and the collection battery is characterised where it lives.
    /// </summary>
    [Fact]
    public void A_multi_part_operand_is_where_the_two_readings_part_company()
    {
        var feature = GeometryFactory.CreateMultiPolygon(
        [
            GeometryFactory.CreatePolygon(
            [
                new Coordinate(0, 0),
                new Coordinate(10, 0),
                new Coordinate(10, 10),
                new Coordinate(0, 10),
                new Coordinate(0, 0),
            ]),
        ]);
        var query = GeometryFactory.CreateMultiPoint(
            [GeometryFactory.CreatePoint(5, 5), GeometryFactory.CreatePoint(20, 20)]);

        var pair = GeometryPair.Of(feature, query)!.Value;
        var served = SpatialRelationPredicates.Crosses(pair, Relations, CancellationToken.None);
        var storeSide = new Nts.GeometryFactory().CreateMultiPolygon(
            [new Nts.GeometryFactory().CreatePolygon(
            [
                new Nts.Coordinate(0, 0),
                new Nts.Coordinate(10, 0),
                new Nts.Coordinate(10, 10),
                new Nts.Coordinate(0, 10),
                new Nts.Coordinate(0, 0),
            ])])
            .Crosses(
                new Nts.GeometryFactory().CreateMultiPoint(
                [
                    new Nts.GeometryFactory().CreatePoint(new Nts.Coordinate(5, 5)),
                    new Nts.GeometryFactory().CreatePoint(new Nts.Coordinate(20, 20)),
                ]));

        Assert.False(served);
        Assert.True(storeSide);
    }

    /// <summary>
    /// The served verdict, through the adapter's own matcher — the definition
    /// of "matches" the query path and the image-service catalog share.
    /// </summary>
    private static bool Served(EsriFeatureQuery query, string featureName, string queryName) =>
        FeatureSpatialMatcher.Matches(
            new FeatureSpatialMatcher.MatchCandidate(
                query,
                RowFor(featureName),
                1,
                SpatialRelationMatrix.Of(queryName).Geometry,
                Relations),
            CancellationToken.None);

    /// <summary>
    /// The verdict a store would give if it evaluated the relation with the
    /// provider's own named predicate — what <c>ST_Contains</c>,
    /// <c>STCrosses</c> and their T-SQL equivalents are, and what a mask the
    /// provider reads off its own matrix is one reading away from.
    ///
    /// <para><c>Contains</c> and <c>Within</c> are read in the protocol's
    /// frame, so a store implementing the same <c>spatialRel</c> asks the
    /// provider's <c>Within</c> for a served <c>Contains</c> and its
    /// <c>Contains</c> for a served <c>Within</c> (ADR-0171). A store that
    /// forwarded the name to a real FeatureServer inherits exactly this
    /// mapping, because that is the direction the provider itself serves.</para>
    /// </summary>
    private static bool StoreSideVerdict(RelationFixture feature, RelationFixture query, string spatialRel)
    {
        var (left, right) = (feature.ReferenceGeometry, query.ReferenceGeometry);
        return spatialRel switch
        {
            EsriFeatureQuery.Contains => left.Within(right),
            EsriFeatureQuery.Within => left.Contains(right),
            EsriFeatureQuery.Touches => left.Touches(right),
            EsriFeatureQuery.Overlaps => left.Overlaps(right),
            EsriFeatureQuery.Crosses => left.Crosses(right),
            _ => left.Intersects(right),
        };
    }

    /// <summary>
    /// The residual the decision costs, pinned: a <c>returnCountOnly</c>
    /// request with a topology relation is counted by the adapter over the
    /// matches, not by the store over the box the plan pushed. The two numbers
    /// are different here, and the served answer is the first — a pushdown
    /// that took the count from the store would serve the second.
    /// </summary>
    [Fact]
    public async Task A_count_over_a_topology_query_is_the_adapter_s_count_and_not_the_rows_in_the_box()
    {
        var store = new RecordingStore(CountRows);
        var body = await ServedAsync(Layer(), store, await ParseAsync(
            ("geometry", "2,2,4,4"),
            ("spatialRel", "esriSpatialRelContains"),
            ("returnCountOnly", "true"),
            ("f", "json")));

        Assert.Equal(1, store.Queries);
        Assert.Equal(0, store.Scans);
        // The box admits both points inside it; the served 'Contains' names
        // the input geometry's relation to the feature (ADR-0171), so it
        // accepts the one point the query square contains and refuses the
        // corner point lying on its boundary.
        Assert.Equal(2, store.BoxAdmitted);
        Assert.Equal(1, body.GetProperty("count").GetInt32());
    }

    /// <summary>
    /// The same request, cancelled: the match is a cancellable read, so a
    /// cancelled token stops it rather than letting it answer.
    /// </summary>
    [Fact]
    public async Task A_cancelled_topology_query_cancels_rather_than_answers()
    {
        var query = await ParseAsync(("geometry", "2,2,4,4"), ("spatialRel", "esriSpatialRelContains"));
        var spec = new FeatureSpatialMatcher.QuerySpec(
            Layer(),
            new RecordingStore(CountRows),
            query,
            GeometryFactory.CreatePolygon(
            [
                new Coordinate(2, 2),
                new Coordinate(4, 2),
                new Coordinate(4, 4),
                new Coordinate(2, 4),
                new Coordinate(2, 2),
            ]),
            Services,
            EsriObjectIdScheme.For(Layer()));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeatureSpatialMatcher.MatchAsync(spec, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    /// <summary>
    /// The counting fixture: a point the query square contains, a point on its
    /// corner, and a point outside the pushed box entirely.
    /// </summary>
    private static readonly IReadOnlyList<Feature> CountRows =
    [
        Row(1, Point(3, 3)),
        Row(2, Point(2, 2)),
        Row(3, Point(50, 50)),
    ];

    private static Feature Row(long id, IGeometry geometry) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [AttributeValue.FromInt64(id), AttributeValue.FromGeometry(geometry)]);

    private static Point Point(double x, double y) => GeometryFactory.CreatePoint(x, y, Crs4326);

    /// <summary>The fixture's envelope, or a named failure rather than a null.</summary>
    private static Envelope EnvelopeOf(string fixtureName) =>
        SpatialRelationMatrix.Of(fixtureName).Geometry.Envelope
        ?? throw new ArgumentOutOfRangeException(nameof(fixtureName), fixtureName, "the fixture carries no envelope");

    private static Feature RowFor(string fixtureName) =>
        Rows[SpatialRelationMatrix.Names.ToList().IndexOf(fixtureName)];

    /// <summary>A query carrying only the relation; the geometry rides the spec.</summary>
    private static async Task<EsriFeatureQuery> QueryForAsync(string spatialRel) =>
        (await ParseAsync()) with { SpatialRel = spatialRel };

    private static async Task<FeatureSpatialMatcher.QuerySpec> SpecAsync(string spatialRel, string queryName) =>
        new(
            Layer(),
            new RecordingStore(Rows),
            await QueryForAsync(spatialRel),
            SpatialRelationMatrix.Of(queryName).Geometry,
            Services,
            EsriObjectIdScheme.For(Layer()));

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        return EsriFeatureQuery.Parse(parameters, Crs4326);
    }

    /// <summary>The served response, through the whole served query path.</summary>
    private static async Task<JsonElement> ServedAsync(
        DatasetDescription dataset,
        IFeatureStore store,
        EsriFeatureQuery query)
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

    /// <summary>
    /// A store that records what it was asked and answers with the reference
    /// executor, counting how many rows the pushed box admitted.
    /// </summary>
    private sealed class RecordingStore(IReadOnlyList<Feature> features) : IFeatureStore
    {
        public int Scans { get; private set; }

        public int Queries { get; private set; }

        public int BoxAdmitted { get; private set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scans++;
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, features)]);
        }

        public Task<FeatureQueryPage> QueryAsync(
            string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries++;
            var page = FeaturePlanExecutor.Execute(Schema, features, query, cancellationToken);
            BoxAdmitted = page.Batches.SelectMany(batch => batch.Features).Count();
            return Task.FromResult(page);
        }

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}