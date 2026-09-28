using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.QueryConformance;
using Spatial.Stores.SqlServer;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The SQL Server provider against the shared pushdown-equals-reference suite
/// (ADR-0098): the restriction is pushed into T-SQL and the plan read, the
/// count, the distinct set and the grouped aggregate are finished by the
/// shared reference executor, so every one of them must answer exactly what
/// that reference answers over the same fixture — including the rows a
/// dialect is tempted to get wrong (ties, nulls, single-row groups, the empty
/// set) and the plans that carry a predicate. Skips with an explicit reason
/// without Docker.
/// </summary>
public sealed class SqlServerQueryConformanceTests : IClassFixture<SqlServerContainerFixture>
{
    private readonly SqlServerContainerFixture _fixture;

    public SqlServerQueryConformanceTests(SqlServerContainerFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_pushed_down_answers_match_the_reference_over_the_conformance_fixture()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.fixture_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        await QueryConformanceSuite.RunAsync(context.Store, dataset);
    }

    [SkippableFact]
    public async Task A_restricted_read_keeps_the_identity_the_whole_read_gave_each_row()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var dataset = $"dbo.identity_{Guid.NewGuid().ToString("N")[..8]}";
        await context.Store.CreateAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features), 4326);
        await context.Store.WriteAsync(dataset, new FeatureBatch(QueryFixture.Schema, QueryFixture.Features));

        // The dataset has no identity column, so a feature's id is its ordinal
        // in the read. A restriction that reached SQL would renumber the rows
        // that survived it, and the same feature would come back with an id
        // that depends on the query (ADR-0097) — so the store keeps the
        // restriction and the ids are the full read's.
        var all = await context.Store.ScanAsync(dataset);
        var restricted = await context.Store.QueryAsync(dataset, new FeatureQuery(Where: FeatureFilter.Parse("score >= 0")));

        var expected = all.SelectMany(batch => batch.Features)
            .Where(feature => !feature["score"].IsNull)
            .Select(feature => feature.Id.Value)
            .ToArray();
        Assert.NotEmpty(expected);
        Assert.Equal(expected, restricted.Features.Select(feature => feature.Id.Value).ToArray());
    }
}
