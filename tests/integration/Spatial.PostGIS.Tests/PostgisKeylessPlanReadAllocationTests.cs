using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The cost of a plan read on a dataset that declares no identity column —
/// the shape <c>CreateAsync</c> leaves (ADR-0147, ADR-0149 §3) — against a
/// live PostGIS.
///
/// <para>
/// Such a layer's plan read is declined, so it is answered by a whole read and
/// finished by the reference executor (ADR-0097 §1, ADR-0116 §1). That answer
/// is correct; what the baseline spike found is that it cost <em>more</em>
/// than the full scan it had to do anyway — 46.7 MB against 31.1 MB on the
/// 34,135-row world-cities layer, a figure that did not move with the page
/// size (SpatialEngine-yup). The whole read is not negotiable, so the cost
/// that is: a page of 25 rows cannot cost more to shape than 34,135 rows cost
/// to read.
///
/// <para>
/// The claim is asserted as a ratio rather than a byte count, because the
/// absolute figure moves with the layer's column count while the ratio does
/// not. A read that maps every row twice — once into the row's feature and
/// once again into the projected result — costs about twice the scan; a read
/// that maps it once costs about the scan, plus the sort a whole read forces.
/// The bound is set so that a doubled materialisation fails it by a wide
/// margin and the ordinary overhead of the reference executor does not.
/// </para>
/// </summary>
/// <para>
/// The measurements run in their own collection: allocation is counted for the
/// whole process, so a figure is this test's only when nothing else in the
/// assembly is allocating.
/// </para>
/// </summary>
[Collection(PostgisKeylessPlanReadAllocationTests.Alone.Name)]
public sealed class PostgisKeylessPlanReadAllocationTests : IClassFixture<PostgisContainerFixture>
{
    private const int Rows = 5_000;

    /// <summary>
    /// How much more than the scan a whole-read plan page may cost: the
    /// reference executor's list copies and its ordering of the whole read,
    /// and nothing else.
    /// </summary>
    private const double Tolerance = 1.25;

    private readonly PostgisContainerFixture _fixture;

    public PostgisKeylessPlanReadAllocationTests(PostgisContainerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task A_capped_plan_read_on_a_keyless_layer_costs_no_more_than_the_scan_it_had_to_do()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");

        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);
        var plan = new FeatureQuery(Order: [new OrderTerm("city")], Limit: 25);

        var page = await context.Store.QueryAsync(dataset, plan);
        var scan = await AllocatedAsync(() => context.Store.ScanAsync(dataset));
        var read = await AllocatedAsync(() => context.Store.QueryAsync(dataset, plan));

        // The answer is the reference's: a whole read, finished over it.
        Assert.Equal(25, page.Batches.Sum(batch => batch.Count));
        Assert.Equal(Rows, page.TotalCount);

        Assert.True(
            read < scan * Tolerance,
            $"a capped plan read over {Rows} keyless rows allocated {read} bytes against {scan} for the scan it reads; "
            + $"the page is 25 rows and the bound is {Tolerance}× the scan.");
    }

    /// <summary>
    /// The same claim on the reduction faces, which read the whole table and
    /// reduce it in managed code (ADR-0184 §2): a count over a layer whose
    /// feature read cannot be pushed is a whole read and then one number, and
    /// the whole read must not be mapped twice to get there. Here the
    /// restriction <em>is</em> pushed, so the read it falls back to is the
    /// unrestricted one — the same shape, on the same rows.
    /// </summary>
    [SkippableFact]
    public async Task A_reduction_over_a_keyless_layer_reads_the_table_once_and_not_twice()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");

        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var dataset = await SeedAsync(context);

        var scan = await AllocatedAsync(() => context.Store.ScanAsync(dataset));
        var read = await AllocatedAsync(() => context.Store.CountAsync(dataset, FeatureQuery.All));

        Assert.Equal(Rows, await context.Store.CountAsync(dataset, FeatureQuery.All));
        Assert.True(
            read < scan * Tolerance,
            $"a whole-table count over {Rows} keyless rows allocated {read} bytes against {scan} for the scan; "
            + $"the answer is one number and the bound is {Tolerance}× the scan.");
    }

    /// <summary>Allocation of one call, warm: the second and third calls are the same shape.</summary>
    private static async Task<long> AllocatedAsync(Func<Task> action)
    {
        await action();
        await action();
        var before = GC.GetTotalAllocatedBytes(precise: true);
        await action();
        return GC.GetTotalAllocatedBytes(precise: true) - before;
    }

    /// <summary>
    /// A table with no primary key, so every feature is named by the ordinal of
    /// the read and no pushed <c>WHERE</c> is admissible (ADR-0097 §1) — the
    /// shape this file measures.
    /// </summary>
    private static async Task<string> SeedAsync(PostgisTestContext context)
    {
        var dataset = $"public.keyless_read_{Guid.NewGuid().ToString("N")[..8]}";
        await context.ExecuteAsync(
            $"CREATE TABLE {dataset} ("
            + "\"id\" bigint NOT NULL, \"city\" text, \"country\" text, \"population\" bigint, "
            + "\"geom\" geometry(Point, 4326))");
        await context.ExecuteAsync(
            $"INSERT INTO {dataset} (\"id\", \"city\", \"country\", \"population\", \"geom\") "
            + "SELECT g, 'city ' || g, 'country ' || (g % 200), g * 10, "
            + "ST_MakePoint(g % 1000 / 1000.0, 0) FROM generate_series(1, "
            + $"{Rows}) AS g");
        Assert.Empty((await context.Store.DescribeAsync(dataset)).IdColumns);
        return dataset;
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
        public const string Name = "postgis-allocation";
    }
}