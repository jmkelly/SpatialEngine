using Spatial.Core.Features;
using Spatial.PredicateConformance;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The SQL Server store against the shared conformance fixture (ADR-0074 §4):
/// the same cases the in-memory store answers in-process, run against a real
/// SQL Server container, so the T-SQL pushdown and the reference evaluator are
/// held to one answer. Skips with an explicit reason when Docker is
/// unavailable.
/// </summary>
public sealed class SqlServerPredicateConformanceTests : IClassFixture<SqlServerContainerFixture>
{
    private const string Dataset = "dbo.predicates";

    private readonly SqlServerContainerFixture _fixture;

    public SqlServerPredicateConformanceTests(SqlServerContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task The_sql_server_store_answers_every_conformance_case()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            "IF OBJECT_ID(N'dbo.predicates', N'U') IS NOT NULL DROP TABLE dbo.predicates");
        await context.Store.CreateAsync(
            Dataset, PredicateConformanceSuite.Sample, PredicateConformanceSuite.Srid);
        await context.Store.WriteAsync(
            Dataset, new FeatureBatch(PredicateConformanceSuite.Schema, PredicateConformanceSuite.Rows));

        var failures = await PredicateConformanceSuite.AssertAsync(context.Store, Dataset);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
