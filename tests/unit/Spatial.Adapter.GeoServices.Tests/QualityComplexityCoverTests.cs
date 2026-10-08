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
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// The uncovered arms of the complexity splits: the evaluator's type dispatch
/// and overlay, the relationship-key literal kinds, the quantization of every
/// multi-part shape, the relation operation's pattern spellings, the
/// traversal's rejected shapes and the designated-time pushdown. Each case
/// asserts served behavior, not the helper structure.
/// </summary>
public sealed class QualityComplexityCoverTests
{
    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static QueryServices Services => new(Operations, Relations, new NtsGeometryMeasures(), Transforms, Transforms);

    private static readonly GeometryServiceCapabilities Capabilities = new(
        Operations,
        new NtsGeometryMeasures(),
        new NtsGeometryProcessing(),
        Relations,
        Transforms,
        Transforms,
        new ProjNetGeodesicBuffering(Operations, new NtsGeometryProcessing()));

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("active", AttributeKind.Boolean, nullable: true),
        new FieldDefinition("observed", AttributeKind.DateTimeOffset, nullable: true),
        new FieldDefinition("uid", AttributeKind.Guid, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    private static readonly Guid Uid = Guid.Parse("11112222-3333-4444-5555-666677778888");

    private static Feature Row() => new(
        new FeatureId("7"),
        Schema,
        [
            AttributeValue.FromInt64(7),
            AttributeValue.FromString("Berlin"),
            AttributeValue.FromBoolean(true),
            AttributeValue.FromDateTimeOffset(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000)),
            AttributeValue.FromGuid(Uid),
            AttributeValue.Null,
        ]);

    private static Predicate.Compare Term(string field, ComparisonOperator comparison, Literal literal) =>
        new(new FieldRef(field), comparison, literal);

    // ---- EsriPredicateEvaluator ----

    [Fact]
    public void The_evaluator_orders_text_like_the_stores()
    {
        var feature = Row();

        Assert.True(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.GreaterThan, Literal.FromText("Aachen")), feature));
        Assert.False(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.LessThan, Literal.FromText("Aachen")), feature));
        Assert.True(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.LessOrEqual, Literal.FromText("Berlin")), feature));
        Assert.True(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.GreaterOrEqual, Literal.FromText("Berlin")), feature));
        Assert.False(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.Equals, Literal.FromText("berlin")), feature));
    }

    [Fact]
    public void The_evaluator_folds_like_when_asked()
    {
        var feature = Row();

        Assert.True(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.LikeFolded, Literal.FromText("ber%")), feature));
        Assert.False(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.LikeFolded, Literal.FromText("par%")), feature));
        Assert.False(EsriPredicateEvaluator.Matches(
            new Predicate.Compare(new FieldRef("id"), ComparisonOperator.LikeFolded, Literal.FromText("7")), feature));
    }

    [Fact]
    public void The_evaluator_tests_flags_guids_and_instants()
    {
        var feature = Row();

        Assert.True(EsriPredicateEvaluator.Matches(Term("active", ComparisonOperator.Equals, Literal.FromBoolean(true)), feature));
        Assert.False(EsriPredicateEvaluator.Matches(Term("active", ComparisonOperator.GreaterThan, Literal.FromBoolean(true)), feature));
        Assert.False(EsriPredicateEvaluator.Matches(Term("active", ComparisonOperator.Equals, Literal.FromText("true")), feature));
        Assert.True(EsriPredicateEvaluator.Matches(
            Term("uid", ComparisonOperator.Equals, Literal.FromText("11112222-3333-4444-5555-666677778888")), feature));
        Assert.False(EsriPredicateEvaluator.Matches(
            Term("uid", ComparisonOperator.Equals, Literal.FromText("not-a-guid")), feature));
        Assert.True(EsriPredicateEvaluator.Matches(
            Term("observed", ComparisonOperator.Equals, Literal.FromMilliseconds(1_700_000_000_000)), feature));
        Assert.True(EsriPredicateEvaluator.Matches(
            Term("observed", ComparisonOperator.LessThan, Literal.FromInteger("1700000000001")), feature));
        Assert.True(EsriPredicateEvaluator.Matches(
            Term("id", ComparisonOperator.Equals, Literal.FromNumber(7.0)), feature));
    }

    [Fact]
    public void The_evaluator_tests_membership_and_nullability()
    {
        var feature = Row();

        Assert.True(EsriPredicateEvaluator.Matches(
            new Predicate.IsIn(new FieldRef("name"), [Literal.FromText("Paris"), Literal.FromText("Berlin")], Negated: false), feature));
        Assert.False(EsriPredicateEvaluator.Matches(
            new Predicate.IsIn(new FieldRef("name"), [Literal.FromText("Paris")], Negated: false), feature));
        Assert.True(EsriPredicateEvaluator.Matches(
            new Predicate.IsIn(new FieldRef("name"), [Literal.FromText("Paris")], Negated: true), feature));
        Assert.True(EsriPredicateEvaluator.Matches(new Predicate.IsNull(new FieldRef("geometry"), Negated: false), feature));
        Assert.False(EsriPredicateEvaluator.Matches(new Predicate.IsNull(new FieldRef("name"), Negated: false), feature));
        Assert.True(EsriPredicateEvaluator.Matches(new Predicate.IsNull(new FieldRef("name"), Negated: true), feature));
        Assert.False(EsriPredicateEvaluator.Matches(Term("name", ComparisonOperator.Equals, Literal.Null), feature));
    }

    [Fact]
    public void The_evaluator_resolves_the_synthetic_object_id_before_the_schema()
    {
        var feature = Row();
        var overlay = new EsriFieldOverlay("OBJECTID", AttributeValue.FromInt64(3));

        Assert.True(EsriPredicateEvaluator.Matches(Term("OBJECTID", ComparisonOperator.Equals, Literal.FromInteger("3")), feature, overlay));
        Assert.False(EsriPredicateEvaluator.Matches(Term("OBJECTID", ComparisonOperator.Equals, Literal.FromInteger("7")), feature, overlay));
        Assert.True(EsriPredicateEvaluator.Matches(null, feature, overlay));
    }

    [Fact]
    public void The_evaluator_refuses_an_unknown_field_by_name()
    {
        var failure = Assert.Throws<EsriInteropException>(
            () => EsriPredicateEvaluator.Matches(Term("nope", ComparisonOperator.Equals, Literal.FromInteger("1")), Row()));

        Assert.Contains("nope", failure.Message);
    }

    // ---- FeatureRelationshipKeys ----

    [Fact]
    public void A_double_key_renders_invariantly()
    {
        Assert.Equal("score = 2.5", FeatureRelationshipKeys.Equality("score", AttributeValue.FromDouble(2.5)));
    }

    [Fact]
    public void A_key_without_a_literal_form_is_a_typed_failure()
    {
        var failure = Assert.Throws<EsriInteropException>(
            () => FeatureRelationshipKeys.Equality("observed", AttributeValue.FromDateTimeOffset(DateTimeOffset.UtcNow)));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    // ---- EsriQuantization ----

    private static EsriQuantization Grid(double tolerance = 1.0) =>
        EsriQuantization.Parse(
            $"{{\"tolerance\":{tolerance.ToString(CultureInfo.InvariantCulture)},\"extent\":{{\"xmin\":0,\"ymin\":0,\"xmax\":10,\"ymax\":10}}}}")!;

    [Fact]
    public void Quantize_snaps_a_multi_point()
    {
        var grid = Grid();
        var multi = GeometryFactory.CreateMultiPoint(
            [GeometryFactory.CreatePoint(0.4, 0.6, Crs4326), GeometryFactory.CreatePoint(2.5, 3.5, Crs4326)], Crs4326);

        var snapped = Assert.IsType<MultiPoint>(grid.Quantize(multi));

        // The grid anchors y on the view's top edge (upperLeft), so 3.5
        // snaps to 3, not 4: 10 + round(-6.5) away from zero is 10 - 7.
        Assert.Equal([(0, 1), (3, 3)], snapped.Points.Select(point => (point.X!.Value, point.Y!.Value)));
    }

    [Fact]
    public void Quantize_snaps_multi_line_polygon_and_collection_shapes()
    {
        var grid = Grid();
        var line = GeometryFactory.CreateLineString([new Coordinate(0.4, 0.4), new Coordinate(5.6, 5.6)], Crs4326);
        var ring = GeometryFactory.CreateLineString(
            [new Coordinate(0.4, 0.4), new Coordinate(4, 0.4), new Coordinate(4, 4), new Coordinate(0.4, 0.4)], Crs4326);

        var multiLine = Assert.IsType<MultiLineString>(grid.Quantize(
            GeometryFactory.CreateMultiLineString([line], Crs4326)));
        Assert.Equal(0, multiLine.LineStrings[0].Sequence.GetCoordinate(0).X);

        var multiPolygon = Assert.IsType<MultiPolygon>(grid.Quantize(
            GeometryFactory.CreateMultiPolygon([GeometryFactory.CreatePolygon(ring, null, Crs4326)], Crs4326)));
        Assert.Equal(0, multiPolygon.Polygons[0].ExteriorRing.Sequence.GetCoordinate(0).X);

        var collection = Assert.IsType<GeometryCollection>(grid.Quantize(
            GeometryFactory.CreateGeometryCollection([GeometryFactory.CreatePoint(0.4, 0.6, Crs4326)], Crs4326)));
        var member = Assert.IsType<Point>(collection.Geometries[0]);
        Assert.Equal((0, 1), (member.X!.Value, member.Y!.Value));
    }

    // ---- GeometryService relation spellings ----

    private static async Task<EsriRequestParameters> ParamsAsync(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    private static async Task<JsonElement> ExecuteAsync(IResult result)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> DispatchAsync(string operation, params (string Key, string Value)[] values) =>
        await ExecuteAsync(GeometryService.Dispatch(operation, await ParamsAsync(values), Capabilities, CancellationToken.None));

    private const string Square = """[{"rings":[[[0,0],[2,0],[2,2],[0,2],[0,0]]]}]""";
    private const string FarSquare = """[{"rings":[[[10,10],[12,10],[12,12],[10,12],[10,10]]]}]""";

    [Fact]
    public async Task Relation_answers_a_bare_de9im_pattern()
    {
        var result = await DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr", "4326"),
            ("relation", "T*F**FFF*"));

        Assert.Equal([1], result.GetProperty("relations").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public async Task Relation_unwraps_the_relate_spelling()
    {
        var result = await DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr", "4326"),
            ("relationParam", "RELATE(G1, G2, 'T*F**FFF*')"));

        Assert.Equal([1], result.GetProperty("relations").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public async Task Relation_answers_disjoint_and_equals_by_pattern()
    {
        var disjoint = await DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", FarSquare), ("sr", "4326"),
            ("relation", "esriSpatialRelDisjoint"));
        Assert.Equal([1], disjoint.GetProperty("relations").EnumerateArray().Select(value => value.GetInt32()));

        var equals = await DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr", "4326"),
            ("relation", "esriSpatialRelEquals"));
        Assert.Equal([1], equals.GetProperty("relations").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Fact]
    public async Task Relation_serves_a_named_custom_relation()
    {
        var result = await DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr", "4326"),
            ("relation", "esriSpatialRelRelation"), ("relationParam", "T*F**FFF*"));

        Assert.Equal([1], result.GetProperty("relations").EnumerateArray().Select(value => value.GetInt32()));
    }

    [Theory]
    [InlineData("esriSpatialRelRelation")]
    [InlineData("esriGeometryRelationRelation")]
    public async Task Relation_refuses_a_custom_relation_without_a_pattern(string relation)
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr", "4326"),
            ("relation", relation)));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public async Task Relation_refuses_an_unknown_name()
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr", "4326"),
            ("relation", "esriSpatialRelNear")));

        Assert.Contains("esriSpatialRelNear", failure.Message);
    }

    [Fact]
    public async Task Relation_refuses_mismatched_pair_references()
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr1", "4326"), ("sr2", "3857"),
            ("relation", "esriSpatialRelIntersects")));

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public void ParseRelationPattern_rejects_an_unquoted_relate()
    {
        var failure = Assert.Throws<EsriInteropException>(() => DispatchAsync("relation",
            ("geometries1", Square), ("geometries2", Square), ("sr", "4326"),
            ("relationParam", "RELATE(G1, G2, T*F**FFF*)")).GetAwaiter().GetResult());

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    // ---- RelatedQuery ----

    private static DatasetDescription RelatedLayer() =>
        new("demo.children", "demo", "children", "geometry", 4326, "Point", 0, ["id"], Schema);

    private static async Task<RelatedQuery> RelatedAsync(params (string Key, string Value)[] values) =>
        RelatedQuery.Parse(await ParamsAsync(values), RelatedLayer());

    [Fact]
    public async Task A_plain_related_query_parses()
    {
        var query = await RelatedAsync();

        Assert.NotNull(query.Query);
    }

    [Theory]
    [InlineData("returnIdsOnly", "true")]
    [InlineData("returnCountOnly", "true")]
    [InlineData("returnExtentOnly", "true")]
    [InlineData("returnDistinctValues", "true")]
    [InlineData("resultOffset", "10")]
    [InlineData("resultRecordCount", "10")]
    public async Task A_traversal_rejects_the_shapes_that_count_or_page(string key, string value)
    {
        var failure = await Assert.ThrowsAsync<EsriInteropException>(() => RelatedAsync((key, value)).AsTask());

        Assert.Equal(EsriErrorCodes.InvalidParameters, failure.Code);
    }

    [Fact]
    public async Task A_traversal_rejects_statistics_and_unique_ids()
    {
        var statistics = await Assert.ThrowsAsync<EsriInteropException>(() => RelatedAsync(
            ("outStatistics", """[{"statisticType":"count","onStatisticField":"id","outStatisticFieldName":"total"}]""")).AsTask());
        Assert.Equal(EsriErrorCodes.InvalidParameters, statistics.Code);

        var unique = await Assert.ThrowsAsync<EsriInteropException>(() => RelatedAsync(("uniqueIds", "abc")).AsTask());
        Assert.Equal(EsriErrorCodes.InvalidParameters, unique.Code);
    }

    // ---- FeatureMatchPushdown designated time ----

    private static DatasetDescription TimedLayer(string? start, string? end) =>
        new("demo.events", "demo", "events", "geometry", 4326, "Point", 0, ["id"], Schema)
        {
            TimeFields = new TemporalExtentFields(start, end),
        };

    private static FeatureSpatialMatcher.QuerySpec TimedSpec(DatasetDescription layer, EsriFeatureQuery query) =>
        new(layer, new MemoryFeatureStore(layer, []), query, null, Services, EsriObjectIdScheme.For(layer));

    private static async Task<EsriFeatureQuery> QueryAsync(params (string Key, string Value)[] values)
    {
        var parameters = await ParamsAsync(values);
        return EsriFeatureQuery.Parse(parameters, Crs4326);
    }

    [Fact]
    public async Task A_start_only_designation_pushes_one_bound()
    {
        var plan = FeatureMatchPushdown.Compile(TimedSpec(
            TimedLayer("observed", null), await QueryAsync(("time", "1000,2000"))));

        Assert.NotNull(plan?.Where);
        Assert.IsType<Predicate.Some>(plan!.Where);
    }

    [Fact]
    public async Task An_end_only_designation_pushes_one_bound()
    {
        var plan = FeatureMatchPushdown.Compile(TimedSpec(
            TimedLayer(null, "observed"), await QueryAsync(("time", "1000,2000"))));

        Assert.NotNull(plan?.Where);
    }

    [Fact]
    public async Task A_designation_naming_no_declared_field_pushes_nothing_for_time()
    {
        var plan = FeatureMatchPushdown.Compile(TimedSpec(
            TimedLayer("missing", "also-missing"), await QueryAsync(("time", "1000,2000"))));

        Assert.Null(plan);
    }

    private sealed class MemoryFeatureStore(DatasetDescription layer, IReadOnlyList<Feature> rows) : IFeatureStore
    {
        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch((FeatureSchema)layer.Schema, rows)]);

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(FeatureQueryPage.Empty);

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

file static class TaskExtensions
{
    public static async Task<T> AsTask<T>(this Task<T> task) => await task;
}
