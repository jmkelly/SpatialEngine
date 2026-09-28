using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.PredicateConformance;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The PostGIS store against the shared conformance fixture (ADR-0074 §4):
/// the same cases the in-memory store answers in-process, run against a real
/// PostGIS container, so the SQL pushdown and the reference evaluator are held
/// to one answer. Skips with an explicit reason when Docker is unavailable.
/// </summary>
public sealed class PostgisPredicateConformanceTests : IClassFixture<PostgisContainerFixture>
{
    private readonly PostgisContainerFixture _fixture;

    public PostgisPredicateConformanceTests(PostgisContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task The_postgis_store_answers_every_conformance_case()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync($"DROP TABLE IF EXISTS {PredicateConformanceSuite.Dataset}");
        await context.Store.CreateAsync(
            PredicateConformanceSuite.Dataset, PredicateConformanceSuite.Sample, PredicateConformanceSuite.Srid);
        await context.Store.WriteAsync(
            PredicateConformanceSuite.Dataset,
            new FeatureBatch(PredicateConformanceSuite.Schema, PredicateConformanceSuite.Rows));

        var failures = await PredicateConformanceSuite.AssertAsync(context.Store, PredicateConformanceSuite.Dataset);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
