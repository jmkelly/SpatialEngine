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
/// Paged store reads on the served surface (ADR-0116): a large-layer query
/// asks the store for one page and holds one page, the continuation the client
/// replays is a <em>store position</em> rather than an index into a match set
/// this process materialised, and the REST JS <c>queryAllFeatures</c> loop still
/// terminates with the exact total, in stable <c>OBJECTID</c> order, with no
/// duplicates.
///
/// <para>
/// The large layer here is synthetic and generated on demand, so the store
/// double materialises exactly the rows a plan's cap and page start name — which
/// is what makes the measurement meaningful: a read that asked for no cap, or a
/// surface that paged a materialised match set itself, would show up as a
/// quarter of a million rows allocated on the way to a thousand-row response.
/// </para>
/// <para>
/// The memory claim below runs in a collection of its own: allocation is
/// counted for the whole process, so a figure is this test's only when nothing
/// else in the assembly is allocating beside it.
/// </para>
/// </summary>
[Collection(PagedStoreReadTests.Alone.Name)]
public sealed class PagedStoreReadTests
{
    private const int Rows = 200_000;
    private const int PageSize = 1_000;

    private static QueryServices Services => new(Operations!, Relations!, new NtsGeometryMeasures(), Transforms!, Transforms!);

    private static readonly CoordinateReference Crs4326 = CoordinateReference.Epsg(4326);
    private static readonly NtsGeometryOperations Operations = new();
    private static readonly NtsGeometryRelations Relations = new();
    private static readonly ProjNetTransforms Transforms = new();

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static DatasetDescription Layer() => new(
        "demo.large", "demo", "large", "geometry", 4326, "Point", Rows, ["id"], Schema);

    private static Feature Row(long id) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString("row"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id % 180, (id % 90) - 45, Crs4326)),
        ]);

    private static async Task<EsriFeatureQuery> ParseAsync(params (string Key, string? Value)[] values)
    {
        var context = new DefaultHttpContext();
        context.Request.QueryString = QueryString.Create(values.Select(value => new KeyValuePair<string, string?>(value.Key, value.Value)));
        var parameters = await EsriRequestParameters.ReadAsync(context, CancellationToken.None);
        return EsriFeatureQuery.Parse(parameters, Crs4326);
    }

    private static async Task<JsonElement> BodyAsync(DatasetDescription dataset, IFeatureStore store, EsriFeatureQuery query)
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
    /// The memory claim, measured warm: the first query pays the JIT and the
    /// static caches (NTS, ProjNet, JSON), so a throwaway store answers it once
    /// before the measured page. What is counted is one page's rows, not the
    /// runtime's first use of the path.
    /// </summary>
    [Fact]
    public async Task A_large_layer_query_holds_one_page_and_not_the_layer()
    {
        var store = new LargeLayerStore();
        var query = await ParseAsync(
            ("orderByFields", "OBJECTID"),
            ("outFields", "*"),
            ("returnExceededLimitFeatures", "true"),
            ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("f", "json"));

        await BodyAsync(Layer(), new LargeLayerStore(), query);

        var before = GC.GetTotalAllocatedBytes(precise: true);
        var body = await BodyAsync(Layer(), store, query);
        var allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        Assert.Equal(PageSize, body.GetProperty("features").GetArrayLength());
        Assert.True(body.GetProperty("exceededTransferLimit").GetBoolean());
        Assert.Equal(PageSize, store.Materialised);
        Assert.All(store.Plans, plan => Assert.Equal(PageSize, plan.Limit));
        Assert.True(
            allocated < 8L * 1024 * 1024,
            $"a {Rows}-row paged query allocated {allocated} bytes; the page is {PageSize} rows.");
    }

    /// <summary>
    /// The token the client replays is the store's own continuation, so the
    /// position lives where the rows do. The surface does not mint an index into
    /// a match set it no longer holds.
    /// </summary>
    [Fact]
    public async Task The_pagination_token_is_the_stores_own_continuation()
    {
        var store = new LargeLayerStore();
        var first = await ParseAsync(
            ("orderByFields", "OBJECTID"),
            ("outFields", "*"),
            ("returnExceededLimitFeatures", "true"),
            ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("f", "json"));

        var body = await BodyAsync(Layer(), store, first);
        var token = body.GetProperty("resultPaginationToken").GetString();
        Assert.Contains(token, store.Issued);

        // The replay carries the token as the plan's continuation: the store
        // reads the page it points at, and hands back its own next one.
        var next = await ParseAsync(
            ("orderByFields", "OBJECTID"),
            ("outFields", "*"),
            ("returnExceededLimitFeatures", "true"),
            ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("resultPaginationToken", token),
            ("f", "json"));

        var second = await BodyAsync(Layer(), store, next);
        Assert.Equal(token, store.Plans[^1].Cursor);
        Assert.Null(store.Plans[^1].Offset);

        // The second page starts where the first stopped, and the token it
        // hands back is its own continuation rather than a re-mint of the first.
        Assert.Equal(PageSize + 1, FirstId(second));
        Assert.Equal(1, FirstId(body));
        Assert.NotEqual(token, second.GetProperty("resultPaginationToken").GetString());
        Assert.Contains(second.GetProperty("resultPaginationToken").GetString(), store.Issued);
    }

    private static long FirstId(JsonElement body) =>
        body.GetProperty("features")[0].GetProperty("attributes").GetProperty("id").GetInt64();

    /// <summary>
    /// A token that is not a continuation of <em>this</em> plan is a typed
    /// invalid-argument failure, not a page of a different question: the store
    /// refuses it, and the client restarts from the first page.
    /// </summary>
    [Fact]
    public async Task A_token_issued_for_another_plan_is_rejected()
    {
        var store = new LargeLayerStore();
        var first = await ParseAsync(
            ("orderByFields", "OBJECTID"),
            ("outFields", "*"),
            ("returnExceededLimitFeatures", "true"),
            ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
            ("f", "json"));
        var token = (await BodyAsync(Layer(), store, first)).GetProperty("resultPaginationToken").GetString();

        // Same token, different page cap: the token points into a plan this
        // request is not asking, so the store refuses it — the token is its
        // own continuation and it recognises its own. Over the wire this is
        // still the Esri error envelope with invalidParameters (spec §9.3);
        // in process it is the store's typed failure, as every other refusal
        // on this path is.
        var other = await ParseAsync(
            ("orderByFields", "OBJECTID"),
            ("outFields", "*"),
            ("returnExceededLimitFeatures", "true"),
            ("resultRecordCount", "500"),
            ("resultPaginationToken", token),
            ("f", "json"));

        var failure = await Assert.ThrowsAsync<SpatialException>(
            () => FeatureService.QueryAsync(Layer(), store, other, Services, CancellationToken.None));
        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    /// <summary>
    /// The REST JS <c>queryAllFeatures</c> loop, replayed over the large layer
    /// with the token workflow: it terminates, collects exactly the total the
    /// layer holds, in ascending <c>OBJECTID</c> order, with no duplicates — and
    /// never asks the store for more than one page at a time.
    /// </summary>
    [Fact]
    public async Task A_token_paged_replay_terminates_with_the_exact_total_in_stable_order()
    {
        var store = new LargeLayerStore();
        var total = await BodyAsync(Layer(), store, await ParseAsync(("returnCountOnly", "true"), ("f", "json")));
        var expected = total.GetProperty("count").GetInt32();
        Assert.Equal(Rows, expected);

        var seen = new List<long>();
        string? token = null;
        var pages = 0;
        while (true)
        {
            var query = await ParseAsync(
                ("orderByFields", "OBJECTID"),
                ("outFields", "*"),
                ("returnExceededLimitFeatures", "true"),
                ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
                ("resultPaginationToken", token),
                ("f", "json"));
            var page = await BodyAsync(Layer(), store, query);
            foreach (var feature in page.GetProperty("features").EnumerateArray())
            {
                seen.Add(feature.GetProperty("attributes").GetProperty("id").GetInt64());
            }

            pages++;
            token = page.TryGetProperty("resultPaginationToken", out var next) ? next.GetString() : null;
            if (token is null)
            {
                break;
            }

            Assert.Equal(PageSize, page.GetProperty("features").GetArrayLength());
            Assert.True(pages <= (Rows / PageSize) + 1, "the replay did not terminate");
        }

        Assert.Equal(expected, seen.Count);
        Assert.Equal(seen.OrderBy(id => id).ToArray(), seen.ToArray());
        Assert.Equal(seen.Count, seen.Distinct().Count());
        Assert.Equal(PageSize * pages, store.Materialised);
    }

    /// <summary>
    /// The <c>resultOffset</c> workflow is unchanged: the page start rides in
    /// the plan as an offset, and the store still answers a page. Paging is not
    /// a token-only workflow, because clients that never send a token must not
    /// be pushed onto one.
    /// </summary>
    [Fact]
    public async Task An_offset_paged_replay_terminates_with_the_exact_total_in_stable_order()
    {
        var store = new LargeLayerStore();
        var seen = new List<long>();
        var offset = 0;
        while (true)
        {
            var query = await ParseAsync(
                ("orderByFields", "OBJECTID"),
                ("outFields", "*"),
                ("returnExceededLimitFeatures", "true"),
                ("resultOffset", offset.ToString(CultureInfo.InvariantCulture)),
                ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
                ("f", "json"));
            var page = await BodyAsync(Layer(), store, query);
            var count = page.GetProperty("features").GetArrayLength();
            foreach (var feature in page.GetProperty("features").EnumerateArray())
            {
                seen.Add(feature.GetProperty("attributes").GetProperty("id").GetInt64());
            }

            offset += count;
            if (count < PageSize || !page.GetProperty("exceededTransferLimit").GetBoolean())
            {
                break;
            }
        }

        Assert.Equal(Rows, seen.Count);
        Assert.Equal(seen.OrderBy(id => id).ToArray(), seen.ToArray());
        Assert.Equal(seen.Count, seen.Distinct().Count());
    }

    /// <summary>
    /// A layer whose <c>OBJECTID</c> is the scan ordinal has no store position
    /// to page by, so it keeps the surface's own cursor over the match set it
    /// evaluated (ADR-0037). The token is still opaque and still only valid for
    /// the query that minted it; what changes is who owns the position.
    /// </summary>
    [Fact]
    public async Task A_layer_without_a_durable_object_id_pages_with_the_surfaces_own_token()
    {
        var store = new LargeLayerStore();
        var layer = new DatasetDescription("demo.large", "demo", "large", "geometry", 4326, "Point", Rows, [], Schema);

        var first = await BodyAsync(
            layer,
            store,
            await ParseAsync(
                ("outFields", "*"),
                ("returnExceededLimitFeatures", "true"),
                ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
                ("f", "json")));
        var token = first.GetProperty("resultPaginationToken").GetString();
        Assert.NotNull(token);
        Assert.DoesNotContain(token, store.Issued);
        Assert.Equal(PageSize, first.GetProperty("features").GetArrayLength());

        var second = await BodyAsync(
            layer,
            store,
            await ParseAsync(
                ("outFields", "*"),
                ("returnExceededLimitFeatures", "true"),
                ("resultRecordCount", PageSize.ToString(CultureInfo.InvariantCulture)),
                ("resultPaginationToken", token),
                ("f", "json")));
        // The surface's cursor is a position in the match set it evaluated: the
        // scan numbered the rows, so the second page starts after the first.
        Assert.Equal(PageSize + 1, second.GetProperty("features")[0].GetProperty("attributes").GetProperty("id").GetInt64());
    }

    [Fact]
    public async Task A_cancelled_paged_read_cancels_rather_than_answers()
    {
        var store = new LargeLayerStore();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var query = await ParseAsync(("outFields", "*"), ("resultRecordCount", "10"), ("f", "json"));

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FeatureService.QueryAsync(Layer(), store, query, Services, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }

    /// <summary>
    /// A store over a large generated layer: it builds the rows the plan's cap
    /// and page start name and nothing else, exactly as a store with a
    /// <c>LIMIT</c> does, records every plan it was asked (so a read that
    /// arrived uncapped is visible), and issues the shared plan cursor. It is
    /// the reference for "one page, held once".
    /// </summary>
    private sealed class LargeLayerStore : IFeatureStore, IFeatureAggregateStore
    {
        private readonly List<string> _issued = [];

        /// <summary>Every plan this store was asked, in order.</summary>
        public List<FeatureQuery> Plans { get; } = [];

        /// <summary>How many feature rows this store has built since it was made.</summary>
        public int Materialised { get; private set; }

        /// <summary>The continuation tokens this store has issued.</summary>
        public IReadOnlyList<string> Issued => _issued;

        public Task<IReadOnlyList<FeatureBatch>> ScanAsync(string dataset, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<FeatureBatch>>([new FeatureBatch(Schema, Build(0, Rows))]);
        }

        public Task<FeatureQueryPage> QueryAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plans.Add(query);
            var start = FeaturePageCursor.StartOffset(query);
            var end = Math.Min(start + (query.Limit ?? Rows), Rows);
            if (start > Rows)
            {
                return Task.FromResult(FeatureQueryPage.Page([new FeatureBatch(Schema, [])], false, totalCount: Rows));
            }

            var features = Build(start, end);
            var more = end < Rows;
            string? cursor = null;
            if (more)
            {
                cursor = FeaturePageCursor.Issue(query, end);
                _issued.Add(cursor);
            }

            return Task.FromResult(FeatureQueryPage.Page([new FeatureBatch(Schema, features)], more, cursor, Rows));
        }

        public Task<int> CountAsync(string dataset, FeatureQuery query, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Plans.Add(query);
            return Task.FromResult(Rows);
        }

        public Task<DistinctPage> DistinctAsync(string dataset, FeatureQuery query, DistinctQuery distinct, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AggregatePage> AggregateAsync(string dataset, FeatureQuery query, AggregateQuery aggregate, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> WriteAsync(string dataset, FeatureBatch batch, string? transaction = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        private List<Feature> Build(int start, int end)
        {
            var features = new List<Feature>(Math.Max(end - start, 0));
            for (var id = start + 1; id <= end; id++)
            {
                features.Add(Row(id));
            }

            Materialised += features.Count;
            return features;
        }
    }

    /// <summary>
    /// A collection of its own, so an allocation figure is not another test's:
    /// <see cref="GC.GetTotalAllocatedBytes"/> counts the process, and the
    /// other classes in this assembly run in parallel with any test that is not
    /// in a collection that forbids it.
    /// </summary>
    [CollectionDefinition(Name, DisableParallelization = true)]
    public sealed class Alone
    {
        public const string Name = "paged-read-allocation";
    }
}
