using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.QueryConformance;
using Spatial.Stores.PostGIS;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The PostGIS provider against the shared pushdown-equals-reference suite
/// (ADR-0098): the plan read, the count, the distinct set and the grouped
/// aggregate are pushed into SQL, and every one of them must answer exactly
/// what the shared reference executor answers over the same fixture — including
/// the rows a dialect is tempted to get wrong (ties, nulls, single-row groups
/// and the empty set). Skips with an explicit reason without Docker.
/// </summary>
public sealed class PostgisQueryConformanceTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisQueryConformanceTests(PostgisContainerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_pushed_down_answers_match_the_reference_over_the_conformance_fixture()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.fixture_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        await QueryConformanceSuite.RunAsync(context.Store, dataset);
    }

    [SkippableFact]
    public async Task A_paged_read_returns_the_page_the_store_was_asked_for_and_the_rest_as_a_cursor()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = $"public.paged_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        var seen = new List<long>();
        var page = await context.Store.QueryAsync(dataset, new FeatureQuery(Order: [new OrderTerm("id")], Limit: 2));
        seen.AddRange(page.Features.Select(feature => feature["id"].Int64Value));
        Assert.Equal(6, page.TotalCount);

        while (page.NextCursor is not null)
        {
            page = await context.Store.QueryAsync(
                dataset, new FeatureQuery(Order: [new OrderTerm("id")], Limit: 2, Cursor: page.NextCursor));
            seen.AddRange(page.Features.Select(feature => feature["id"].Int64Value));
        }

        Assert.Equal(6, seen.Count);
        Assert.Equal(seen.OrderBy(id => id), seen);
        Assert.Equal(6, seen.Distinct().Count());
    }
}
