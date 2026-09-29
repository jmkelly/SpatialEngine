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
/// The MapServer and per-feature read surfaces compiled onto the store's own
/// faces (SpatialEngine-u2x.12): the identify match envelope, the find text
/// search, the generateRenderer classification, the layer extent and the
/// per-feature (object) resource and attachment targets.
///
/// <para>
/// The claim is not "the pushed read is cheaper" but "the pushed read answers
/// the same question". Every test asserts both halves: the store was asked a
/// plan, a distinct set, an aggregate or a lookup and never a whole-dataset
/// scan, and the response is the one a store handing back the whole layer
/// produced — which is exactly what the pre-change code saw.
/// </para>
///
/// <para>
/// The refusal half is pinned too: a layer whose <c>OBJECTID</c> is the scan
/// ordinal keeps the scan (ADR-0097), a clause the plan cannot carry keeps the
/// scan, and nothing falls back to a scan after a pushdown has been attempted.
/// </para>
/// </summary>
public sealed class MapResourcePushdownTests
{
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly NtsGeometryMeasures Measures = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static QueryServices Services => new(Operations, Relations, Measures, Transforms, Transforms);

    private static readonly long Earlier = 1_000_000_000_000;
    private static readonly long Later = 1_700_000_000_000;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("class", AttributeKind.String, nullable: true),
        new FieldDefinition("population", AttributeKind.Int64, nullable: true),
        new FieldDefinition("score", AttributeKind.Double, nullable: true),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>A layer whose <c>OBJECTID</c> is a durable identity column (ADR-0037).</summary>
    private static DatasetDescription Layer() =>
        new("demo.places", "demo", "places", "geometry", 4326, "Point", Rows.Count, ["id"], Schema);

    /// <summary>The same layer without an identity column: its <c>OBJECTID</c> is the scan ordinal.</summary>
    private static DatasetDescription OrdinalLayer() =>
        new("demo.places", "demo", "places", "geometry", 4326, "Point", Rows.Count, [], Schema);

    private static Feature Row(long id, string name, string? kind, long? population, double? score, long? observed, IGeometry? geometry) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString(name),
            kind is null ? AttributeValue.Null : AttributeValue.FromString(kind),
            population is { } count ? AttributeValue.FromInt64(count) : AttributeValue.Null,
            score is { } value ? AttributeValue.FromDouble(value) : AttributeValue.Null,
            observed is { } millis ? AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(millis)) : AttributeValue.Null,
            geometry is null ? AttributeValue.Null : AttributeValue.FromGeometry(geometry),
        ]);

    private static readonly IReadOnlyList<Feature> Rows =
    [
        Row(1, "alpha", "urban", 100, 1.5, Earlier, Point(1, 1)),
        Row(2, "beta", "rural", 200, 2.5, Later, Point(2, 2)),
        Row(3, "gamma", "urban", null, null, null, Point(30, 30)),
        Row(4, "delta", "rural", 400, 4.5, null, Polygon(20, 20, 40, 40)),
        Row(5, "epsilon%tagged", "urban", 500, 5.5, Earlier, null),
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

    private static MapLayerInfo Info(DatasetDescription dataset) =>
        new(new PublishedLayer(0, dataset.Id, "Places"), dataset, new Envelope(0, 0, 45, 45));

    /// <summary>The identify query geometry: the point, buffered by the pixel tolerance.</summary>
    private static IGeometry Tolerance(double tolerance) => tolerance > 0
        ? Operations.Buffer(Point(1, 1), tolerance, 8, CancellationToken.None)
        : Point(1, 1);

    private static IdentifyQuery Identify(IGeometry queryGeometry) => new(null, null, queryGeometry, Crs4326, true);

    private static IdentifyQuery WithDefs(IdentifyQuery query, string clause) => query with
    {
        LayerDefs = new Dictionary<int, EsriWhere> { [0] = Parse(clause) },
    };

    private static EsriWhere Parse(string clause) =>
        EsriWhere.TryParse(clause, out var parsed, out var error) && parsed is not null
            ? parsed
            : throw new InvalidOperationException(error);

    // ---------------------------------------------------------------- identify

    /// <summary>
    /// The identify match reads a plan carrying the query geometry's envelope
    /// in the layer's own CRS, and the scan is never issued. The hits, and the
    /// JSON the writer builds from them, are the ones the whole-layer read
    /// produced.
    /// </summary>
    [Fact]
    public async Task Identify_pushes_the_query_envelope_down_and_answers_the_same()
    {
        var store = new PushingStore(Rows);
        var query = Identify(Tolerance(1.5));

        var hits = await Match(store, query, Layer());

        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);
        var box = Assert.IsType<Spatial.Contracts.BoundingBox>(Assert.IsType<FeatureQuery>(store.LastPlan).BoundingBox);
        Assert.True(box.MinX <= 1 && box.MaxX >= 2);

        var legacy = await Match(new LegacyStore(Rows), query, Layer());
        Assert.Equal(legacy.Select(hit => hit.Feature.Id.Value), hits.Select(hit => hit.Feature.Id.Value));
        Assert.Equal(await Results(legacy, query), await Results(hits, query));
    }

    /// <summary>
    /// The pushed box is a pre-filter and the intersection test stays the
    /// answer: a store that hands back the whole layer is matched here, so
    /// only the features the query geometry really intersects are served.
    /// </summary>
    [Fact]
    public async Task Identify_still_tests_the_intersection_over_the_rows_the_store_returns()
    {
        var store = new PushingStore(Rows, answerEverything: true);

        var hits = await Match(store, Identify(Tolerance(1.5)), Layer());

        // The whole layer came back; the far point, the far polygon and the
        // geometry-less feature were rejected here rather than by the plan.
        Assert.Equal(["1", "2"], hits.Select(hit => hit.Feature.Id.Value));
    }

    /// <summary>
    /// A tolerance widens the identify geometry, the pushed box follows it, and
    /// the result set is the one the whole-layer read produced, hit for hit
    /// (ADR-0048).
    /// </summary>
    [Theory]
    [InlineData(0.0, new[] { "1" })]
    [InlineData(1.5, new[] { "1", "2" })]
    [InlineData(30.0, new[] { "1", "2", "4" })]
    public async Task Identify_with_a_tolerance_returns_the_same_result_set_as_the_whole_layer_read(double tolerance, string[] expected)
    {
        var query = Identify(Tolerance(tolerance));
        var store = new PushingStore(Rows);

        var hits = await Match(store, query, Layer());
        var legacy = await Match(new LegacyStore(Rows), query, Layer());

        Assert.Equal(expected, hits.Select(hit => hit.Feature.Id.Value));
        Assert.Equal(legacy.Select(hit => hit.Feature.Id.Value), hits.Select(hit => hit.Feature.Id.Value));
        Assert.Equal(await Results(legacy, query), await Results(hits, query));
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);
    }

    /// <summary>
    /// <c>returnGeometry</c> is the response's business and is unchanged by the
    /// pushdown: the same hits, with and without the projected geometry.
    /// </summary>
    [Fact]
    public async Task Identify_returning_geometry_is_unchanged_by_the_pushdown()
    {
        var withGeometry = Identify(Tolerance(1.5));
        var without = withGeometry with { ReturnGeometry = false };
        var store = new PushingStore(Rows);

        var pushed = await Match(store, withGeometry, Layer());
        var legacy = await Match(new LegacyStore(Rows), withGeometry, Layer());

        Assert.Equal(await Results(legacy, withGeometry), await Results(pushed, withGeometry));
        Assert.All(pushed, hit => Assert.NotNull(hit.Geometry));

        var bare = await Match(store, without, Layer());
        Assert.All(bare, hit => Assert.Null(hit.Geometry));
        Assert.Equal(await Results(legacy, without), await Results(bare, without));
    }

    /// <summary>
    /// The <c>layerDefs</c> filter stays the adapter's: a feature the box
    /// admits but the definition expression rejects is not a hit, which is
    /// what the pre-change pipeline did.
    /// </summary>
    [Fact]
    public async Task Identify_keeps_the_layer_defs_filter_in_the_adapter()
    {
        var query = WithDefs(Identify(Tolerance(1.5)), "population > 150");
        var store = new PushingStore(Rows);

        var hits = await Match(store, query, Layer());
        var legacy = await Match(new LegacyStore(Rows), query, Layer());

        Assert.Equal(["2"], hits.Select(hit => hit.Feature.Id.Value));
        Assert.Equal(legacy.Select(hit => hit.Feature.Id.Value), hits.Select(hit => hit.Feature.Id.Value));
        Assert.Equal(await Results(legacy, query), await Results(hits, query));
        Assert.Equal(0, store.Scans);
        Assert.Null(Assert.IsType<FeatureQuery>(store.LastPlan).Where);
    }

    /// <summary>
    /// A temporal <c>layerDefs</c> selection is the adapter's too: the pushed
    /// plan carries no time predicate, and a dated feature outside the window
    /// is dropped here rather than at the store.
    /// </summary>
    [Fact]
    public async Task Identify_keeps_the_temporal_selection_in_the_adapter()
    {
        var query = Identify(Tolerance(1.5)) with
        {
            Times = new Dictionary<int, MapTimeExtent> { [0] = new(Later, Later + 1000) },
        };
        var store = new PushingStore(Rows);

        var hits = await Match(store, query, Layer());
        var legacy = await Match(new LegacyStore(Rows), query, Layer());

        Assert.Equal(["2"], hits.Select(hit => hit.Feature.Id.Value));
        Assert.Equal(await Results(legacy, query), await Results(hits, query));
        Assert.Equal(0, store.Scans);
    }

    /// <summary>
    /// A layer whose <c>OBJECTID</c> is the scan ordinal keeps the scan: the
    /// identify filters resolve the synthetic <c>OBJECTID</c> against the scan
    /// ordinal, so a read that returned only the boxed rows would renumber that
    /// key (ADR-0097).
    /// </summary>
    [Fact]
    public async Task Identify_on_an_ordinal_object_id_keeps_the_scan()
    {
        var query = WithDefs(Identify(Tolerance(1.5)), "OBJECTID = 2");
        var store = new PushingStore(Rows);

        var hits = await Match(store, query, OrdinalLayer());

        Assert.Equal(1, store.Scans);
        Assert.Equal(0, store.Queries);
        Assert.Equal(["2"], hits.Select(hit => hit.Feature.Id.Value));
    }

    [Fact]
    public async Task A_cancelled_identify_cancels_rather_than_answers()
    {
        var store = new PushingStore(Rows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MapIdentifyMatcher.MatchAsync(
                new IdentifyServices(store, Operations, Transforms),
                [Info(Layer())],
                Identify(Tolerance(1.5)),
                cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    // -------------------------------------------------------------------- find

    /// <summary>
    /// The text search becomes a disjunction of <c>LIKE</c> tests the store
    /// evaluates, and the adapter's own case-insensitive match still decides
    /// each candidate. The results are the ones the whole-layer read produced,
    /// including the searches whose text a <c>LIKE</c> reads differently.
    /// </summary>
    [Theory]
    [InlineData("alp", true, null, new[] { "alpha" })]
    [InlineData("ALP", true, null, new[] { "alpha" })]
    [InlineData("be", false, null, new[] { "beta" })]
    [InlineData("be", false, "name", new[] { "beta" })]
    [InlineData("urban", true, "class", new[] { "urban", "urban", "urban" })]
    [InlineData("%tagged", true, "name", new[] { "epsilon%tagged" })]
    [InlineData("a_pha", true, "name", new string[0])]
    [InlineData("nothing-matches-this", true, null, new string[0])]
    public async Task Find_pushes_the_text_search_down_and_answers_the_same(string text, bool contains, string? fields, string[] expected)
    {
        var store = new PushingStore(Rows);
        var parameters = await Parameters(
            [("searchText", text), ("contains", Bool(contains)), ("returnGeometry", "true"), .. Search(fields)]);

        var pushed = await Text(await MapFindEngine.FindAsync(store, [Info(Layer())], parameters, Transforms, CancellationToken.None));
        var legacy = await Text(await MapFindEngine.FindAsync(new LegacyStore(Rows), [Info(Layer())], parameters, Transforms, CancellationToken.None));

        Assert.Equal(expected, Hits(pushed));
        Assert.Equal(legacy, pushed);
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Queries);
    }

    /// <summary>
    /// The pushed predicate is a pre-filter and the adapter's match is the
    /// answer: a store that returns the whole layer is filtered here, so a
    /// case-insensitive <c>contains</c> the store's <c>LIKE</c> could never
    /// have found is still a hit.
    /// </summary>
    [Fact]
    public async Task Find_still_matches_case_insensitively_over_the_rows_the_store_returns()
    {
        var store = new PushingStore(Rows, answerEverything: true);
        var parameters = await Parameters([("searchText", "ALP"), ("returnGeometry", "false")]);

        var body = await Text(await MapFindEngine.FindAsync(store, [Info(Layer())], parameters, Transforms, CancellationToken.None));

        Assert.Equal(["alpha"], Hits(body));
        Assert.Equal(0, store.Scans);
    }

    /// <summary>
    /// The plan is the one restriction that can be stated without changing the
    /// answer: a row can only match when a searched field carries a value. The
    /// text is not in the plan, because the vocabulary's <c>LIKE</c> is
    /// case-sensitive on some stores and this search is not.
    /// </summary>
    [Fact]
    public async Task The_find_plan_excludes_the_rows_no_searched_field_can_match()
    {
        var store = new PushingStore(Rows);
        var parameters = await Parameters([("searchText", "alp"), ("returnGeometry", "false")]);

        await MapFindEngine.FindAsync(store, [Info(Layer())], parameters, Transforms, CancellationToken.None);

        var plan = Assert.IsType<FeatureQuery>(store.LastPlan);
        var some = Assert.IsType<Predicate.Some>(plan.Where);
        Assert.Equal(["name", "class"], some.Terms.Select(term => Assert.IsType<Predicate.IsNull>(term).Field.Name));
        Assert.All(some.Terms, term => Assert.True(Assert.IsType<Predicate.IsNull>(term).Negated));
        Assert.Null(plan.BoundingBox);
    }

    /// <summary>
    /// A layer with no string field to search is not a request the store is
    /// asked about at all: the search is empty, so no plan is compiled.
    /// </summary>
    [Fact]
    public async Task Find_on_a_layer_with_no_searchable_field_reads_nothing()
    {
        var schema = new FeatureSchema(
        [
            new FieldDefinition("id", AttributeKind.Int64),
            new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
        ]);
        var rows = new[] { new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(1), AttributeValue.FromGeometry(Point(1, 1))]) };
        var dataset = new DatasetDescription("demo.places", "demo", "places", "geometry", 4326, "Point", 1, ["id"], schema);
        var store = new PushingStore(rows, schema);
        var parameters = await Parameters([("searchText", "al"), ("returnGeometry", "false")]);

        var body = await Text(await MapFindEngine.FindAsync(store, [Info(dataset)], parameters, Transforms, CancellationToken.None));

        Assert.Empty(Hits(body));
        Assert.Equal(0, store.Scans);
        Assert.Equal(0, store.Queries);
    }

    [Fact]
    public async Task A_cancelled_find_cancels_rather_than_answers()
    {
        var store = new PushingStore(Rows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var parameters = await Parameters([("searchText", "alp")]);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MapFindEngine.FindAsync(store, [Info(Layer())], parameters, Transforms, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    // -------------------------------------------------------- generateRenderer

    private const string ClassBreaks = """{"type":"classBreaksDef","classificationField":"population","breakCount":2}""";
    private const string UniqueValues = """{"type":"uniqueValueDef","uniqueValueFields":["class"]}""";

    /// <summary>
    /// The class-break quantisation is a store aggregate: the minimum and the
    /// maximum of the classification field are the store's answer, and the
    /// breaks are the adapter's arithmetic over the two.
    /// </summary>
    [Fact]
    public async Task Class_breaks_are_a_store_aggregate_and_the_renderer_is_unchanged()
    {
        var store = new PushingStore(Rows);

        var pushed = await MapGenerateRenderer.GenerateAsync(store, Layer(), ClassBreaks, null, CancellationToken.None);
        var legacy = await MapGenerateRenderer.GenerateAsync(new LegacyStore(Rows), Layer(), ClassBreaks, null, CancellationToken.None);

        Assert.Equal(Json(legacy), Json(pushed));
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Aggregates);
        var specs = Assert.IsType<AggregateQuery>(store.LastAggregate!).Specs;
        Assert.Equal([AggregateStatistic.Minimum, AggregateStatistic.Maximum], specs.Select(spec => spec.Statistic));
        Assert.All(specs, spec => Assert.Equal("population", spec.Field));
        Assert.Null(Assert.IsType<FeatureQuery>(store.LastPlan).Where);

        // 100..500 over two breaks: the quantisation ADR-0055 pins.
        Assert.Equal(100, pushed.MinValue);
        Assert.Equal([300d, 500d], pushed.ClassBreakInfos!.Select(info => info.ClassMaxValue));
        Assert.Equal(["100 - 300", "300 - 500"], pushed.ClassBreakInfos!.Select(info => info.Label));
    }

    /// <summary>
    /// The unique-value domain is the store's distinct set, deduplicated on the
    /// rendered value exactly as the in-memory set was.
    /// </summary>
    [Fact]
    public async Task Unique_values_are_a_store_distinct_and_the_renderer_is_unchanged()
    {
        var store = new PushingStore(Rows);

        var pushed = await MapGenerateRenderer.GenerateAsync(store, Layer(), UniqueValues, null, CancellationToken.None);
        var legacy = await MapGenerateRenderer.GenerateAsync(new LegacyStore(Rows), Layer(), UniqueValues, null, CancellationToken.None);

        Assert.Equal(Json(legacy), Json(pushed));
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Distincts);
        Assert.Equal(["rural", "urban"], pushed.UniqueValueInfos!.Select(info => info.Value));
        Assert.Equal(["class"], Assert.IsType<DistinctQuery>(store.LastDistinct!).Fields);
    }

    /// <summary>
    /// A <c>where</c> the plan can carry rides with the reduction, and the
    /// store's filtered aggregate is the answer: the adapter does not reduce
    /// the whole layer and pick the rows afterwards.
    /// </summary>
    [Fact]
    public async Task A_where_clause_is_pushed_with_the_class_break_aggregate()
    {
        var store = new PushingStore(Rows);

        var pushed = await MapGenerateRenderer.GenerateAsync(store, Layer(), ClassBreaks, "population > 150", CancellationToken.None);
        var legacy = await MapGenerateRenderer.GenerateAsync(new LegacyStore(Rows), Layer(), ClassBreaks, "population > 150", CancellationToken.None);

        Assert.Equal(Json(legacy), Json(pushed));
        Assert.Equal(200, pushed.MinValue);
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Aggregates);
        Assert.Equal(["population"], Assert.IsType<FeatureQuery>(store.LastPlan).Where!.Fields().Select(field => field.Name));
    }

    /// <summary>
    /// A <c>where</c> on the synthetic <c>OBJECTID</c> of a layer whose object
    /// id is the scan ordinal is a clause no store can read, so it keeps the
    /// scan: reducing without it would answer a different question
    /// (ADR-0097).
    /// </summary>
    [Fact]
    public async Task A_where_clause_the_plan_cannot_carry_keeps_the_class_break_scan()
    {
        var store = new PushingStore(Rows);

        var pushed = await MapGenerateRenderer.GenerateAsync(store, OrdinalLayer(), ClassBreaks, "OBJECTID < 3", CancellationToken.None);
        var legacy = await MapGenerateRenderer.GenerateAsync(new LegacyStore(Rows), OrdinalLayer(), ClassBreaks, "OBJECTID < 3", CancellationToken.None);

        Assert.Equal(Json(legacy), Json(pushed));
        Assert.Equal(100, pushed.MinValue);
        Assert.Equal([150d, 200d], pushed.ClassBreakInfos!.Select(info => info.ClassMaxValue));
        Assert.Equal(1, store.Scans);
        Assert.Equal(0, store.Aggregates);
    }

    /// <summary>
    /// An empty domain is still the typed refusal the surface has always
    /// written, and the aggregate's null answer reads as one.
    /// </summary>
    [Fact]
    public async Task An_empty_class_break_domain_is_still_refused_by_name()
    {
        var store = new PushingStore(Rows);

        var failure = await Assert.ThrowsAsync<EsriInteropException>(
            () => MapGenerateRenderer.GenerateAsync(store, Layer(), ClassBreaks, "population > 10000", CancellationToken.None));
        var legacy = await Assert.ThrowsAsync<EsriInteropException>(
            () => MapGenerateRenderer.GenerateAsync(new LegacyStore(Rows), Layer(), ClassBreaks, "population > 10000", CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains("No features of layer 'demo.places' match", failure.Message);
        Assert.Equal(legacy.Message, failure.Message);
        Assert.Equal(1, store.Aggregates);
    }

    [Fact]
    public async Task An_empty_unique_value_domain_is_still_refused_by_name()
    {
        var store = new PushingStore(Rows);

        var failure = await Assert.ThrowsAsync<EsriInteropException>(
            () => MapGenerateRenderer.GenerateAsync(store, Layer(), UniqueValues, "class = 'suburban'", CancellationToken.None));
        var legacy = await Assert.ThrowsAsync<EsriInteropException>(
            () => MapGenerateRenderer.GenerateAsync(new LegacyStore(Rows), Layer(), UniqueValues, "class = 'suburban'", CancellationToken.None));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
        Assert.Contains("No features of layer 'demo.places' match", failure.Message);
        Assert.Equal(legacy.Message, failure.Message);
        Assert.Equal(1, store.Distincts);
    }

    [Fact]
    public async Task A_cancelled_generate_renderer_cancels_rather_than_answers()
    {
        var store = new PushingStore(Rows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MapGenerateRenderer.GenerateAsync(store, Layer(), ClassBreaks, null, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    // ------------------------------------------------------------ layer extent

    /// <summary>
    /// The layer extent is the store's own envelope reduction: one aggregate
    /// over the layer, no whole-dataset read behind it, and the extent the
    /// union over the whole layer produced (ADR-0120).
    /// </summary>
    [Fact]
    public async Task The_layer_extent_is_a_store_aggregate()
    {
        var store = new PushingStore(Rows);

        var layer = await MapServerResources.ReadLayerAsync(store, Catalogue(Layer()), Published(), CancellationToken.None);
        var legacy = await MapServerResources.ReadLayerAsync(new LegacyStore(Rows), Catalogue(Layer()), Published(), CancellationToken.None);

        Assert.Equal(legacy.Extent, layer.Extent);
        Assert.Equal(new Envelope(1, 1, 40, 40), layer.Extent);
        Assert.Equal(0, store.Scans);
        Assert.Equal(0, store.Queries);
        Assert.Equal(1, store.Aggregates);
        var spec = Assert.Single(Assert.IsType<AggregateQuery>(store.LastAggregate!).Specs);
        Assert.Equal(AggregateStatistic.Envelope, spec.Statistic);
        Assert.Equal("geometry", spec.Field);
    }

    /// <summary>
    /// A table layer has no geometry column, so there is nothing to reduce: the
    /// extent is the union over nothing, and the table is not scanned to
    /// discover that none of its features has one.
    /// </summary>
    [Fact]
    public async Task A_table_layer_extents_to_nothing_without_reading_it()
    {
        var schema = new FeatureSchema([new FieldDefinition("id", AttributeKind.Int64)]);
        var dataset = new DatasetDescription("demo.places", "demo", "places", string.Empty, 0, string.Empty, 1, ["id"], schema);
        var store = new PushingStore([new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(1)])], schema);

        var layer = await MapServerResources.ReadLayerAsync(store, Catalogue(dataset), Published(), CancellationToken.None);
        var legacy = await MapServerResources.ReadLayerAsync(
            new LegacyStore([new Feature(new FeatureId("1"), schema, [AttributeValue.FromInt64(1)])]),
            Catalogue(dataset),
            Published(),
            CancellationToken.None);

        Assert.Equal(legacy.Extent, layer.Extent);
        Assert.True(layer.Extent.IsEmpty);
        Assert.Equal(0, store.Scans);
        Assert.Equal(0, store.Queries);
        Assert.Equal(0, store.Aggregates);
    }

    // -------------------------------------------------- per-feature resources

    [Fact]
    public async Task The_feature_resource_resolves_its_target_through_the_lookup()
    {
        var store = new PushingStore(Rows);
        var query = EsriFeatureQuery.Parse(await Parameters([("f", "json")]), Crs4326);

        var pushed = await Text(await FeatureService.FeatureAsync(Layer(), store, 2, query, Services, CancellationToken.None));
        var legacy = await Text(await FeatureService.FeatureAsync(Layer(), new LegacyStore(Rows), 2, query, Services, CancellationToken.None));

        Assert.Equal(legacy, pushed);
        Assert.Equal(0, store.Scans);
        Assert.Equal(0, store.Queries);
        Assert.Equal(1, store.Lookups);
        Assert.Equal(["2"], store.LastIds!.Select(id => id.Value));
        Assert.Contains("beta", pushed, StringComparison.Ordinal);
    }

    /// <summary>
    /// An object id no feature carries is still <c>not.found</c>, with the
    /// message the pre-change scan wrote. The targeted read cannot be evidence
    /// of absence on its own — a store may key its features by something other
    /// than the identity column — so the scan decides and the refusal is
    /// unchanged.
    /// </summary>
    [Fact]
    public async Task An_unknown_object_id_is_still_not_found()
    {
        var store = new PushingStore(Rows);
        var query = EsriFeatureQuery.Parse(await Parameters([("f", "json")]), Crs4326);

        var failure = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeatureService.FeatureAsync(Layer(), store, 99, query, Services, CancellationToken.None));

        Assert.Equal(EsriErrorCodes.NotFound, failure.Code);
        Assert.Equal("Feature 99 does not exist in layer 'demo.places'.", failure.Message);
        Assert.Equal(1, store.Lookups);
    }

    /// <summary>
    /// A store whose <see cref="Feature.Id"/> is not the layer's identity
    /// column — a source-identity ingest numbers features as it reads them —
    /// answers a targeted read with a row that belongs to another object. The
    /// row is keyed by the id it carries, so the request misses and the scan
    /// serves the right feature rather than the wrong one.
    /// </summary>
    [Fact]
    public async Task A_lookup_keyed_by_something_else_than_the_object_id_does_not_serve_the_wrong_feature()
    {
        // Two features whose stored identities and object ids are crossed.
        var crossed = new[]
        {
            new Feature(new FeatureId("10"), Schema,
            [
                AttributeValue.FromInt64(20), AttributeValue.FromString("twenty"),
                AttributeValue.FromString("urban"), AttributeValue.FromInt64(20), AttributeValue.FromDouble(2),
                AttributeValue.Null, AttributeValue.FromGeometry(Point(20, 20)),
            ]),
            new Feature(new FeatureId("20"), Schema,
            [
                AttributeValue.FromInt64(10), AttributeValue.FromString("ten"),
                AttributeValue.FromString("rural"), AttributeValue.FromInt64(10), AttributeValue.FromDouble(1),
                AttributeValue.Null, AttributeValue.FromGeometry(Point(10, 10)),
            ]),
        };
        var store = new PushingStore(crossed);

        var feature = await FeatureAttachmentTargets.FindFeatureAsync(Layer(), store, 10, CancellationToken.None);

        Assert.Equal("ten", MapFeatures.StringValue(feature, "name"));
        Assert.Equal(1, store.Lookups);
        Assert.Equal(1, store.Scans);
    }

    /// <summary>
    /// A layer whose object id is the scan ordinal has no durable identity for
    /// a lookup to read, so the resource keeps the scan.
    /// </summary>
    [Fact]
    public async Task The_feature_resource_of_an_ordinal_layer_keeps_the_scan()
    {
        var store = new PushingStore(Rows);
        var query = EsriFeatureQuery.Parse(await Parameters([("f", "json")]), Crs4326);

        var pushed = await Text(await FeatureService.FeatureAsync(OrdinalLayer(), store, 3, query, Services, CancellationToken.None));

        Assert.Contains("gamma", pushed, StringComparison.Ordinal);
        Assert.Equal(1, store.Scans);
        Assert.Equal(0, store.Lookups);
    }

    [Fact]
    public async Task A_cancelled_feature_resource_cancels_rather_than_answers()
    {
        var store = new PushingStore(Rows);
        var query = EsriFeatureQuery.Parse(await Parameters([("f", "json")]), Crs4326);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeatureService.FeatureAsync(Layer(), store, 2, query, Services, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    // ------------------------------------------------------------- attachments

    /// <summary>
    /// The attachment targets resolve through one identity lookup, in request
    /// order, and never scan.
    /// </summary>
    [Fact]
    public async Task Attachment_targets_resolve_through_one_lookup()
    {
        var store = new PushingStore(Rows);

        var targets = await FeatureAttachmentTargets.ResolveAsync(Layer(), store, [4, 1, 1], CancellationToken.None);
        var legacy = await FeatureAttachmentTargets.ResolveAsync(Layer(), new LegacyStore(Rows), [4, 1, 1], CancellationToken.None);

        Assert.Equal(legacy.Select(target => target.ObjectId), targets.Select(target => target.ObjectId));
        Assert.Equal(legacy.Select(target => target.Feature.Id.Value), targets.Select(target => target.Feature.Id.Value));
        Assert.Equal(3, targets.Count);
        Assert.Equal(0, store.Scans);
        Assert.Equal(1, store.Lookups);
    }

    /// <summary>
    /// A single per-feature attachment read is the same targeted lookup, and an
    /// unknown object id is still <c>not.found</c>.
    /// </summary>
    [Fact]
    public async Task A_single_attachment_target_is_a_lookup_and_an_unknown_id_is_not_found()
    {
        var store = new PushingStore(Rows);
        var scheme = EsriObjectIdScheme.For(Layer());

        var feature = await FeatureAttachmentTargets.FindFeatureAsync(Layer(), store, 5, CancellationToken.None);
        var failure = await Assert.ThrowsAsync<EsriInteropException>(
            () => FeatureAttachmentTargets.FindFeatureAsync(Layer(), store, 99, CancellationToken.None));

        Assert.True(scheme.TryResolve(feature, 0, out var objectId));
        Assert.Equal(5, objectId);
        Assert.Equal(EsriErrorCodes.NotFound, failure.Code);
        Assert.Equal("Feature 99 does not exist in layer 'demo.places'.", failure.Message);
        Assert.Equal(2, store.Lookups);
    }

    /// <summary>
    /// Enumerating every feature of a layer (the request that names no object
    /// ids) still reads the whole dataset — it is the layer's own list, not a
    /// keyed read — and its order is the scan order the ordinals come from.
    /// </summary>
    [Fact]
    public async Task Attachment_targets_without_ids_enumerate_the_layer()
    {
        var store = new PushingStore(Rows);

        var targets = await FeatureAttachmentTargets.ResolveAsync(Layer(), store, null, CancellationToken.None);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], targets.Select(target => target.ObjectId));
        Assert.Equal(1, store.Scans);
        Assert.Equal(0, store.Lookups);
    }

    // ------------------------------------------------------------------ doubles

    private static string Bool(bool value) => value ? "true" : "false";

    private static (string Key, string Value)[] Search(string? fields) =>
        fields is null ? [] : [("searchFields", fields)];

    private static async Task<List<IdentifyHit>> Match(IFeatureStore store, IdentifyQuery query, DatasetDescription dataset) =>
        await MapIdentifyMatcher.MatchAsync(new IdentifyServices(store, Operations, Transforms), [Info(dataset)], query, CancellationToken.None);

    private static string Json(EsriRenderer renderer) => JsonSerializer.Serialize(renderer);

    private static string[] Hits(string body)
    {
        using var document = JsonDocument.Parse(body);
        return [.. document.RootElement.GetProperty("results").EnumerateArray().Select(result => result.GetProperty("value").GetString() ?? string.Empty)];
    }

    private static async Task<string> Text(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static async Task<string> Results(IReadOnlyList<IdentifyHit> hits, IdentifyQuery query) =>
        await Text(EsriJson.Write(writer => MapIdentifyWriter.WriteResults(writer, hits, query.ReturnGeometry)));

    private static Task<EsriRequestParameters> Parameters(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static PublishedLayer Published() => new(0, "demo.places", "Places");

    private static StubCatalogue Catalogue(DatasetDescription dataset) => new(dataset);

    private sealed class StubCatalogue(DatasetDescription dataset) : IDataCatalogue
    {
        public Task<IReadOnlyList<DatasetSummary>> ListAsync(string? pattern = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DatasetSummary>>([]);

        public Task<DatasetDescription> DescribeAsync(string datasetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(dataset);

        public Task<string> CreateAsync(string datasetId, FeatureBatch sample, int srid, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// A store that records what it was asked and answers with the shared
    /// reference executor — the same answer a pushing provider has to
    /// reproduce. <c>answerEverything</c> is the hostile variant that returns
    /// the whole layer whatever the plan said, so the tests can show the
    /// adapter is what the answer comes from.
    /// </summary>
    private sealed class PushingStore(IReadOnlyList<Feature> features, FeatureSchema? schema = null, bool answerEverything = false) :
        IFeatureStore, IFeatureAggregateStore, IFeatureLookup
    {
        private readonly FeatureSchema _schema = schema ?? features[0].Schema;

        public int Scans { get; private set; }

        public int Queries { get; private set; }

        public int Aggregates { get; private set; }

        public int Distincts { get; private set; }

        public int Lookups { get; private set; }

        public FeatureQuery? LastPlan { get; private set; }

        public AggregateQuery? LastAggregate { get; private set; }

        public DistinctQuery? LastDistinct { get; private set; }

        public IReadOnlyList<FeatureId>? LastIds { get; private set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Scans++;
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(_schema, features)]);
        }

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries++;
            LastPlan = query;
            return Task.FromResult(answerEverything
                ? FeaturePlanExecutor.Finish(_schema, features, Whole(query), cancellationToken)
                : FeaturePlanExecutor.Execute(_schema, features, query, cancellationToken));
        }

        public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastPlan = query;
            return Task.FromResult(FeatureReduction.CountFeatures(FeaturePlanExecutor.Select(_schema, features, query, cancellationToken)));
        }

        public Task<DistinctPage> DistinctAsync(
            string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Distincts++;
            LastPlan = query;
            LastDistinct = distinct;
            return Task.FromResult(FeatureReduction.Distinct(_schema, FeaturePlanExecutor.Select(_schema, features, query, cancellationToken), distinct));
        }

        public Task<AggregatePage> AggregateAsync(
            string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Aggregates++;
            LastPlan = query;
            LastAggregate = aggregate;
            return Task.FromResult(FeatureReduction.Aggregate(_schema, FeaturePlanExecutor.Select(_schema, features, query, cancellationToken), aggregate));
        }

        public Task<IReadOnlyList<Feature>> GetAsync(
            string dataset, IReadOnlyList<FeatureId> ids, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Lookups++;
            LastIds = ids;
            var wanted = ids.ToHashSet();
            return Task.FromResult<IReadOnlyList<Feature>>(features.Where(feature => wanted.Contains(feature.Id)).ToArray());
        }

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// The store the pre-change code saw: no reduction, no lookup, and a plan
    /// read that hands back the whole layer whatever it was asked for.
    /// </summary>
    private sealed class LegacyStore(IReadOnlyList<Feature> features) : IFeatureStore
    {
        private readonly FeatureSchema _schema = features[0].Schema;

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(_schema, features)]);
        }

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(FeaturePlanExecutor.Finish(_schema, features, Whole(query), cancellationToken));
        }

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <summary>The plan that restricts nothing, which is what the whole layer is.</summary>
    private static FeatureQuery Whole(FeatureQuery query) => query with
    {
        Ids = null,
        Where = null,
        BoundingBox = null,
        Projection = null,
        Order = null,
        Limit = null,
        Offset = null,
        Cursor = null,
    };
}
