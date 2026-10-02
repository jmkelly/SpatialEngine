using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.PredicateConformance;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The PostGIS store against the shared conformance fixture (ADR-0074 §4):
/// the same cases the in-memory store answers in-process, run against a real
/// PostGIS container, so the SQL pushdown and the reference evaluator are held
/// to one answer. Skips with an explicit reason when Docker is unavailable.
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisPredicateConformanceTests : IClassFixture<PostgisDatabaseFixture>
{
    private readonly PostgisDatabaseFixture _fixture;

    public PostgisPredicateConformanceTests(PostgisDatabaseFixture fixture)
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

    /// <summary>
    /// The same cases against a table that has a <em>primary key</em>, which is
    /// the precondition for a plan's <c>WHERE</c> to be pushed at all: a
    /// dataset with no identity column names its features by the ordinal of the
    /// read, so the store keeps the restriction in the caller and finishes the
    /// plan in managed code (ADR-0097). Over a table without one, the pushed
    /// comparison — and the collation it inherits or states — is never measured,
    /// which is why this case is the one that catches a store whose
    /// <c>WHERE</c> compares strings by the database's collation instead of by
    /// the contract's bytes (ADR-0123).
    /// </summary>
    [SkippableFact]
    public async Task Every_conformance_case_answers_the_same_through_a_pushed_where()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "public.predicates_pushed";
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            $"DROP TABLE IF EXISTS {dataset}; CREATE TABLE {dataset} ("
            + "\"code\" text PRIMARY KEY, \"population\" bigint, \"score\" double precision, "
            + "\"active\" boolean, \"reference\" uuid, \"seen\" timestamptz, "
            + "\"geometry\" geometry(Geometry, 4326))");
        await context.Store.WriteAsync(
            dataset,
            new FeatureBatch(PredicateConformanceSuite.Schema, PredicateConformanceSuite.Rows));

        var failures = await PredicateConformanceSuite.AssertAsync(context.Store, dataset);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>
    /// A cancelled pushdown fails as a cancellation and leaves nothing behind:
    /// the collation the comparison needs is a catalog read of its own, so a
    /// token cancelled before the statement is a token cancelled before the
    /// answer, and the next caller asks the database again instead of
    /// inheriting a half-read value.
    /// </summary>
    [SkippableFact]
    public async Task A_cancelled_pushed_filter_is_a_cancellation_and_leaves_the_next_read_correct()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "public.predicates_cancelled";
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            $"DROP TABLE IF EXISTS {dataset}; CREATE TABLE {dataset} ("
            + "\"code\" text PRIMARY KEY, \"population\" bigint, \"score\" double precision, "
            + "\"active\" boolean, \"reference\" uuid, \"seen\" timestamptz, "
            + "\"geometry\" geometry(Geometry, 4326))");
        await context.Store.WriteAsync(
            dataset,
            new FeatureBatch(PredicateConformanceSuite.Schema, PredicateConformanceSuite.Rows));

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => context.Store.QueryAsync(
                dataset,
                new FeatureQuery(Where: FeatureFilter.Parse("code < 'delta'")),
                cancellation.Token));
        Assert.Equal(cancellation.Token, failure.CancellationToken);

        // Same store, live token: the restriction is still the reference's, and
        // it is still the restriction the database was asked for.
        var page = await context.Store.QueryAsync(
            dataset,
            new FeatureQuery(Where: FeatureFilter.Parse("code < 'delta'")));
        Assert.Equal(
            ["Delta", "_bravo", "alpha", "beta"],
            page.Features.Select(feature => feature["code"].StringValue).Order(StringComparer.Ordinal).ToArray());
    }
}
