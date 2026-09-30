using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Esri.Codec;
using Spatial.Querying;
using Spatial.Transformations.ProjNet;

namespace Spatial.Adapter.GeoServices.Tests;

/// <summary>
/// MapServer <c>find</c> pushing its search text into the plan, and the
/// comparison that makes it pushable (ADR-0132).
///
/// <para>
/// The search was narrowed by the store to "a searched string field is not
/// null" and nothing more, because the vocabulary's only text comparison was
/// <c>LIKE</c> and a <c>LIKE</c> is case-sensitive on some back ends and not on
/// others: a pushed pattern would have <em>lost</em> rows this search has to
/// match, and the answer would have depended on the store's collation. The
/// vocabulary now carries a comparison whose case behaviour every back end
/// states the same way, so the text itself rides in the plan — and the
/// adapter's own case-insensitive match stays the answer over whatever the plan
/// selected.
/// </para>
///
/// <para>
/// Every test asserts both halves, as the rest of the pushdown family does: the
/// store was asked a plan and never a scan, and the response is the one a store
/// handing back the whole layer produced.
/// </para>
/// </summary>
public sealed class MapFindFoldedPushdownTests
{
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("class", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry, nullable: true),
    ]);

    /// <summary>
    /// A layer whose rows carry both cases of the same search — including a row
    /// whose only string value is null, which no pattern can match.
    /// </summary>
    private static readonly Feature[] Rows =
    [
        Row(1, "alpha", "urban"),
        Row(2, "ALPHA", null),
        Row(3, "bravo", null),
        Row(4, null, null),
    ];

    private static Feature Row(long id, string? name, string? kind) => new(
        new FeatureId(id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            name is null ? AttributeValue.Null : AttributeValue.FromString(name),
            kind is null ? AttributeValue.Null : AttributeValue.FromString(kind),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(1, 1, CoordinateReference.Epsg(4326))),
        ]);

    private static DatasetDescription Layer() =>
        new("demo.places", "demo", "places", "geometry", 4326, "Point", Rows.Length, ["id"], Schema);

    private static MapLayerInfo Info(DatasetDescription dataset) =>
        new(new PublishedLayer(0, dataset.Id, "Places"), dataset, new Envelope(0, 0, 45, 45));

    [Theory]
    // A contains search becomes one folded pattern per searched field, with the
    // text in the middle: `%alp%` for contains, `alp%` for startsWith.
    [InlineData("ALP", true, null, new[] { "alpha", "ALPHA" })]
    [InlineData("alp", true, null, new[] { "alpha", "ALPHA" })]
    [InlineData("ALP", false, null, new[] { "alpha", "ALPHA" })]
    [InlineData("lph", true, null, new[] { "alpha", "ALPHA" })]
    [InlineData("BRAVO", true, null, new[] { "bravo" })]
    [InlineData("bra", false, null, new[] { "bravo" })]
    // A wildcard in the client's own text is a wildcard in the pattern, which
    // widens the plan: it admits every row that carries a value, and the
    // adapter's literal match then rejects all of them. A pre-filter has to
    // admit a superset, never less.
    [InlineData("%tagged", true, null, new string[0])]
    [InlineData("nothing-matches-this", true, null, new string[0])]
    public async Task Find_pushes_the_search_text_and_answers_exactly_as_before(
        string text, bool contains, string? fields, string[] expected)
    {
        var store = new RecordingStore(Rows);

        var pushed = await Find(store, text, contains, fields);
        var legacy = await Find(new RecordingStore(Rows) { AnswerEverything = true }, text, contains, fields);

        Assert.Equal(expected, pushed);
        Assert.Equal(legacy, pushed);
        Assert.Equal(1, store.Queries);
    }

    [Fact]
    public async Task The_plan_carries_the_search_text_as_a_folded_pattern_per_searched_field()
    {
        var store = new RecordingStore(Rows);

        await Find(store, "ALP", contains: true, fields: null);

        var plan = Assert.IsType<FeatureQuery>(store.LastPlan);
        var some = Assert.IsType<Predicate.Some>(plan.Where);
        Assert.Equal(["name", "class"], some.Terms.Select(term => Assert.IsType<Predicate.Compare>(term).Field.Name));
        Assert.All(some.Terms, term =>
        {
            var compare = Assert.IsType<Predicate.Compare>(term);
            Assert.Equal(ComparisonOperator.LikeFolded, compare.Operator);
            Assert.Equal("%ALP%", compare.Value.Text);
        });
        Assert.Null(plan.BoundingBox);
    }

    [Fact]
    public async Task A_starts_with_search_pushes_a_prefixed_pattern()
    {
        var store = new RecordingStore(Rows);

        await Find(store, "al", contains: false, fields: null);

        var some = Assert.IsType<Predicate.Some>(Assert.IsType<FeatureQuery>(store.LastPlan).Where);
        Assert.All(some.Terms, term => Assert.Equal("al%", Assert.IsType<Predicate.Compare>(term).Value.Text));
    }

    [Fact]
    public async Task A_search_text_outside_the_ascii_alphabet_keeps_the_restriction_the_plan_can_state()
    {
        // The plan's comparison folds ASCII and only ASCII, which is what every
        // back end states identically. The served search is case-insensitive in
        // Unicode, so a search text outside that alphabet is not pushed as a
        // pattern: the null tests stay, the text stays in the adapter, and the
        // served answer is unchanged either way.
        var store = new RecordingStore(Rows);

        var hits = await Find(store, "é", contains: true, fields: null);

        var some = Assert.IsType<Predicate.Some>(Assert.IsType<FeatureQuery>(store.LastPlan).Where);
        Assert.All(some.Terms, term => Assert.IsType<Predicate.IsNull>(term));
        Assert.Equal([], hits);
    }

    [Fact]
    public async Task A_search_text_the_two_dialects_read_differently_is_not_pushed_either()
    {
        // A backslash is Postgres's `LIKE` escape character and nothing at all
        // in T-SQL, so a search text ending in one is a statement Postgres
        // refuses and a pattern SQL Server reads as two characters. The plan is
        // one tree for every store, so the text stays in the adapter.
        var store = new RecordingStore(Rows);

        var hits = await Find(store, @"bra\", contains: false, fields: null);

        var some = Assert.IsType<Predicate.Some>(Assert.IsType<FeatureQuery>(store.LastPlan).Where);
        Assert.All(some.Terms, term => Assert.IsType<Predicate.IsNull>(term));
        Assert.Equal([], hits);
    }

    [Fact]
    public async Task The_adapter_still_matches_case_insensitively_over_the_rows_the_plan_admitted()
    {
        // The pushed rows are a pre-filter, not the answer: a store that answers
        // more than the plan asked for is still filtered here.
        var store = new RecordingStore(Rows) { AnswerEverything = true };

        var hits = await Find(store, "ALP", contains: true, fields: null);

        Assert.Equal(["alpha", "ALPHA"], hits);
    }

    [Fact]
    public async Task A_cancelled_find_cancels_rather_than_answers()
    {
        var store = new RecordingStore(Rows);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var parameters = await Parameters(("searchText", "alp"));
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MapFindEngine.FindAsync(store, [Info(Layer())], parameters, Transforms, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    private static async Task<string[]> Find(IFeatureStore store, string text, bool contains, string? fields)
    {
        var values = new List<(string Key, string Value)>
        {
            ("searchText", text),
            ("contains", contains ? "true" : "false"),
            ("returnGeometry", "false"),
        };
        if (fields is not null)
        {
            values.Add(("searchFields", fields));
        }

        var parameters = await Parameters([.. values]);
        var body = await Text(await MapFindEngine.FindAsync(store, [Info(Layer())], parameters, Transforms, CancellationToken.None));
        return Hits(body);
    }

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

    private static Task<EsriRequestParameters> Parameters(params (string Key, string Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        return EsriRequestParameters.ReadAsync(context, CancellationToken.None);
    }

    /// <summary>
    /// A store that answers with the shared reference executor — the answer a
    /// pushing provider has to reproduce — and records the plan it was asked.
    /// <see cref="AnswerEverything"/> is the hostile variant that hands back the
    /// whole layer whatever the plan said, so a test can show the response is
    /// the adapter's match rather than the plan's.
    /// </summary>
    private sealed class RecordingStore(IReadOnlyList<Feature> features) : IFeatureStore
    {
        private readonly FeatureSchema _schema = features[0].Schema;

        public bool AnswerEverything { get; init; }

        public int Queries { get; private set; }

        public FeatureQuery? LastPlan { get; private set; }

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("find must not read the whole layer.");

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries++;
            LastPlan = query;
            var plan = AnswerEverything
                ? query with { Ids = null, Where = null, BoundingBox = null, Projection = null, Order = null, Limit = null, Offset = null }
                : query;
            return Task.FromResult(FeaturePlanExecutor.Execute(_schema, features, plan, cancellationToken));
        }

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
