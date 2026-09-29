using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Stores.Memory;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// Paged reads on the store with no pushdown (ADR-0116 §3). The in-memory
/// provider has nothing to push a plan into, so it answers the plan itself
/// through the shared reference executor — which pages: a capped read returns
/// <em>one page</em> and a continuation, never the whole match set, so a caller
/// that pages a large layer never holds more than a page of it. What the
/// provider cannot do is read fewer rows than it holds: the dataset is the
/// provider, so the restriction is evaluated over the rows it already has and
/// only the page crosses into the answer.
///
/// <para>
/// The measurement the paged read on a <em>large layer</em> is held to lives on
/// the store that can push the page down (ADR-0116 §1); what this file pins is
/// the half a store with no pushdown owes its callers: the page, the
/// continuation, the "one more" signal, and a typed failure for a cursor that
/// was not its own.
/// </para>
/// </summary>
public sealed class MemoryPagedReadTests
{
    private const int Rows = 5_000;
    private const int PageSize = 100;

    private static readonly FeatureSchema Schema = new(
    [
        new FieldDefinition("id", AttributeKind.Int64),
        new FieldDefinition("name", AttributeKind.String, nullable: true),
        new FieldDefinition("geometry", AttributeKind.Geometry),
    ]);

    private static Feature Row(long id) => new(
        new FeatureId(id.ToString(CultureInfo.InvariantCulture)),
        Schema,
        [
            AttributeValue.FromInt64(id),
            AttributeValue.FromString("row"),
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id % 180, (id % 90) - 45, CoordinateReference.Epsg(4326))),
        ]);

    private static async Task<MemoryStore> SeededAsync()
    {
        var store = new MemoryStore();
        await store.CreateAsync("memory.large", new FeatureBatch(Schema, [Row(1)]), 4326);
        var batch = new List<Feature>(1_000);
        for (var id = 1L; id <= Rows; id++)
        {
            batch.Add(Row(id));
            if (batch.Count == 1_000)
            {
                await store.WriteAsync("memory.large", new FeatureBatch(Schema, batch));
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await store.WriteAsync("memory.large", new FeatureBatch(Schema, batch));
        }

        return store;
    }

    /// <summary>A capped read answers one page and says whether more remain.</summary>
    [Fact]
    public async Task A_capped_read_answers_one_page_and_states_whether_more_remain()
    {
        var store = await SeededAsync();
        var page = await store.QueryAsync("memory.large", new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize));

        Assert.Equal(PageSize, page.Features.Count());
        Assert.Equal(Rows, page.TotalCount);
        Assert.True(page.HasMore);
        Assert.NotNull(page.NextCursor);

        var last = await store.QueryAsync(
            "memory.large",
            new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize, Cursor: page.NextCursor, Offset: null));
        while (last.HasMore)
        {
            last = await store.QueryAsync(
                "memory.large",
                new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize, Cursor: last.NextCursor, Offset: null));
        }

        Assert.False(last.HasMore);
        Assert.Null(last.NextCursor);
    }

    /// <summary>
    /// The walk the Esri surface performs: page by page to the end, in order,
    /// with no row repeated and none missed, and a page that never exceeds the
    /// cap.
    /// </summary>
    [Fact]
    public async Task A_paged_walk_terminates_with_every_row_once_in_order()
    {
        var store = await SeededAsync();
        var seen = new List<long>();
        var query = new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize);
        FeatureQueryPage last = FeatureQueryPage.Empty;

        for (var page = 0; page <= (Rows / PageSize) + 1; page++)
        {
            var read = await store.QueryAsync("memory.large", query);
            seen.AddRange(read.Features.Select(feature => feature["id"].Int64Value));
            Assert.True(read.Features.Count() <= PageSize);
            last = read;
            if (!read.HasMore)
            {
                break;
            }

            query = query with { Cursor = read.NextCursor, Offset = null };
        }

        // The last page is the one that says there is no next one.
        Assert.Null(last.NextCursor);
        Assert.Equal(Rows % PageSize == 0 ? PageSize : Rows % PageSize, last.Features.Count());
        Assert.Equal(Rows, seen.Count);
        Assert.Equal(seen.OrderBy(id => id).ToArray(), seen.ToArray());
        Assert.Equal(Rows, seen.Distinct().Count());
    }

    /// <summary>
    /// What a store with no pushdown can promise, stated rather than implied:
    /// the <em>answer</em> is a page, whatever the layer's size, and the page
    /// is all that crosses back. What it cannot promise is to read fewer rows
    /// than it holds — the dataset <em>is</em> this provider, so evaluating the
    /// plan's order over it costs what the order costs — and the contract says
    /// so (ADR-0116 §3) instead of pretending otherwise. A store that can push
    /// the order and the cap down is the one that never holds the layer at all;
    /// see the paged-read measurement on the PostGIS path.
    /// </summary>
    [Fact]
    public async Task The_answer_is_one_page_of_a_large_layer_and_the_total_is_the_whole_layer()
    {
        var store = await SeededAsync();
        var page = await store.QueryAsync("memory.large", new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize));

        Assert.Equal(PageSize, page.Batches.Sum(batch => batch.Features.Count));
        Assert.Equal(PageSize, page.Features.Count());
        Assert.Equal(Rows, page.TotalCount);
        Assert.True(page.HasMore);
    }

    /// <summary>
    /// An uncapped plan is the unbounded read, not a page: the default page
    /// size is what a <em>caller</em> asks for, and a plan that asks for
    /// nothing is answered with everything.
    /// </summary>
    [Fact]
    public async Task An_uncapped_plan_is_the_whole_match_set()
    {
        var store = await SeededAsync();
        var page = await store.QueryAsync("memory.large", new FeatureQuery(Order: [new OrderTerm("id")]));

        Assert.Equal(Rows, page.Features.Count());
        Assert.False(page.HasMore);
        Assert.Null(page.NextCursor);
        Assert.Equal(Rows, page.TotalCount);
    }

    /// <summary>A cursor that was not issued for this plan is a typed failure, not a different page.</summary>
    [Fact]
    public async Task A_cursor_from_another_plan_is_invalid_arguments()
    {
        var store = await SeededAsync();
        var page = await store.QueryAsync("memory.large", new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize));

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.QueryAsync(
            "memory.large",
            new FeatureQuery(Order: [new OrderTerm("name")], Limit: PageSize, Cursor: page.NextCursor)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    /// <summary>A negative cap is rejected before a row is read.</summary>
    [Fact]
    public async Task A_negative_cap_is_invalid_arguments()
    {
        var store = await SeededAsync();
        var failure = await Assert.ThrowsAsync<SpatialException>(
            () => store.QueryAsync("memory.large", new FeatureQuery(Order: [new OrderTerm("id")], Limit: -1)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task A_cancelled_paged_read_cancels_rather_than_answers()
    {
        var store = await SeededAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.QueryAsync("memory.large", new FeatureQuery(Order: [new OrderTerm("id")], Limit: PageSize), cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }
}
