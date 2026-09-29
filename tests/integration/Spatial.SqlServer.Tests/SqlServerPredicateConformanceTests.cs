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

    /// <summary>
    /// The same cases against a table that has a <em>primary key</em>, which is
    /// the precondition for a plan's <c>WHERE</c> to be pushed at all: a dataset
    /// with no identity column names its features by the ordinal of the read,
    /// so the store keeps the restriction in the caller and finishes the plan
    /// in managed code (ADR-0097). Over a table without one, the pushed
    /// comparison — and the collation it would inherit, which for SQL Server is
    /// a <em>case-insensitive</em> one by default — is never measured, which is
    /// why this case is the one that catches a store whose <c>WHERE</c> compares
    /// strings by the database's collation instead of by the contract's bytes
    /// (ADR-0123).
    /// </summary>
    [SkippableFact]
    public async Task Every_conformance_case_answers_the_same_through_a_pushed_where()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "dbo.predicates_pushed";
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        // The key is an integer surrogate rather than the `code` column itself:
        // a text key under the database's case-insensitive collation cannot hold
        // `Delta` and `delta` at all, which would be a statement about keying
        // rather than about the comparison. `code` is left as the store's own
        // `nvarchar(max)` under the database's own collation, which is the
        // column a pushed `WHERE` compares and the collation it must correct.
        await context.ExecuteAsync(
            "IF OBJECT_ID(N'dbo.predicates_pushed', N'U') IS NOT NULL DROP TABLE dbo.predicates_pushed; "
            + "CREATE TABLE dbo.predicates_pushed ("
            + "[gid] int IDENTITY(1,1) NOT NULL PRIMARY KEY, [code] nvarchar(max) NULL, "
            + "[population] bigint NULL, [score] float NULL, [active] bit NULL, "
            + "[reference] uniqueidentifier NULL, [seen] datetimeoffset NULL, [geometry] geometry NULL)");
        await context.Store.WriteAsync(
            dataset,
            new FeatureBatch(PredicateConformanceSuite.Schema, PredicateConformanceSuite.Rows));

        var failures = await PredicateConformanceSuite.AssertAsync(context.Store, dataset);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
