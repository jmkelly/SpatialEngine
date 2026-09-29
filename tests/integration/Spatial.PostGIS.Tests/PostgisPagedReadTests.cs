using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The paged read of a large layer against a real database (ADR-0116 §1): a
/// plan that restricts nothing is read with a <c>LIMIT</c> and an
/// <c>OFFSET</c>, not fetched whole and paged on the way in, and the walk a
/// client performs over it terminates with every row exactly once, in order.
///
/// <para>
/// The memory claim is pinned where it can be measured honestly: the shape of
/// the statement the store issues (a capped read of an <em>unrestricted</em>
/// plan carries an <c>ORDER BY</c> and a <c>LIMIT</c> — see
/// <c>PostgisPlanSurfaceTests</c>), and the allocation of a large-layer query
/// on the served surface (<c>PagedStoreReadTests</c>). It is deliberately not
/// measured in bytes here, and no longer because the catalogue was in the way:
/// a read re-used to re-discover the dataset's description, a fixed cost two
/// orders of magnitude larger than the difference between a page and a whole
/// table, so the instrument measured the catalogue rather than the read. That
/// cost is gone (ADR-0122) and the walk's per-page description reads are
/// pinned as a count in <c>PostgisDescriptionCacheTests</c>; the byte
/// measurement stays where it is, on the served surface.
/// </para>
/// </summary>
public sealed class PostgisPagedReadTests : IClassFixture<PostgisContainerFixture>
{
    private const int Rows = 20_000;
    private const int PageSize = 100;

    private readonly PostgisContainerFixture _fixture;

    public PostgisPagedReadTests(PostgisContainerFixture fixture) => _fixture = fixture;

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
            AttributeValue.FromGeometry(GeometryFactory.CreatePoint(id % 180, (id % 90) - 45)),
        ]);

    private async Task<string> SeededAsync()
    {
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.paged_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(Schema, [Row(1)]), 4326);
        var batch = new List<Feature>(1_000);
        for (var id = 1L; id <= Rows; id++)
        {
            batch.Add(Row(id));
            if (batch.Count == 1_000)
            {
                await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, batch));
                batch.Clear();
            }
        }

        if (batch.Count > 0)
        {
            await context.Store.WriteAsync(dataset, new FeatureBatch(Schema, batch));
        }

        return dataset;
    }

    [SkippableFact]
    public async Task A_paged_walk_over_a_large_layer_terminates_with_every_row_once_in_order()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync();

        var seen = new List<long>(Rows);
        var query = new FeatureQuery(Order: new[] { new OrderTerm("id") }) with { Limit = PageSize };
        var pages = 0;
        while (true)
        {
            var read = await context.Store.QueryAsync(dataset, query);
            Assert.True(read.Features.Count() <= PageSize);
            seen.AddRange(read.Features.Select(feature => feature["id"].Int64Value));
            pages++;
            if (!read.HasMore)
            {
                Assert.Null(read.NextCursor);
                break;
            }

            Assert.NotNull(read.NextCursor);
            query = query with { Cursor = read.NextCursor, Offset = null };
            Assert.True(pages <= (Rows / PageSize) + 1, "the walk did not terminate");
        }

        Assert.Equal(Rows, seen.Count);
        Assert.Equal(seen.OrderBy(id => id).ToArray(), seen.ToArray());
        Assert.Equal(Rows, seen.Distinct().Count());
        Assert.Equal((Rows / PageSize) + (Rows % PageSize == 0 ? 0 : 1), pages);
    }

    /// <summary>
    /// The exact total a <c>returnCountOnly</c> request reports is the same
    /// number the paged walk collects: the count and the page are the same
    /// question asked two ways.
    /// </summary>
    [SkippableFact]
    public async Task The_paged_walk_collects_exactly_what_the_count_reports()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync();
        var clause = new Predicate.Compare(new FieldRef("name"), ComparisonOperator.Equals, Literal.FromText("row"));

        var total = await context.Store.CountAsync(dataset, new FeatureQuery(Where: clause, Order: new[] { new OrderTerm("id") }));

        var seen = 0;
        var query = new FeatureQuery(Where: clause, Order: new[] { new OrderTerm("id") }) with { Limit = PageSize };
        while (true)
        {
            var read = await context.Store.QueryAsync(dataset, query);
            seen += read.Features.Count();
            if (!read.HasMore)
            {
                break;
            }

            query = query with { Cursor = read.NextCursor, Offset = null };
        }

        Assert.Equal(Rows, total);
        Assert.Equal(total, seen);
    }

    [SkippableFact]
    public async Task A_cancelled_paged_read_cancels_rather_than_answers()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeededAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => context.Store.QueryAsync(
            dataset, new FeatureQuery(Order: new[] { new OrderTerm("id") }) with { Limit = PageSize }, cancellation.Token));

        Assert.Equal(cancellation.Token, failure.CancellationToken);
    }
}
