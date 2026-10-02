using Spatial.Core.Features;
using Spatial.PredicateConformance;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The SQL Server store against the shared conformance fixture (ADR-0074 §4):
/// the same cases the in-memory store answers in-process, run against a real
/// SQL Server container, so the T-SQL pushdown and the reference evaluator are
/// held to one answer. Skips with an explicit reason when Docker is
/// unavailable.
/// </summary>
[Collection(SqlServerContainerDefinition.Name)]
public sealed class SqlServerPredicateConformanceTests : IClassFixture<SqlServerDatabaseFixture>
{
    private const string Dataset = "dbo.predicates";

    private readonly SqlServerDatabaseFixture _fixture;

    public SqlServerPredicateConformanceTests(SqlServerDatabaseFixture fixture)
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
    /// (ADR-0123). The key is the fixture's own <c>code</c> column, so the
    /// identity this case compares by is a <em>text</em> identity (ADR-0126) —
    /// which is why it is declared under a binary collation, and why it is a
    /// <c>nvarchar(64)</c>: the store's own string type is <c>nvarchar(max)</c>,
    /// and SQL Server refuses that as a key.
    /// </summary>
    [SkippableFact]
    public async Task Every_conformance_case_answers_the_same_through_a_pushed_where()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        const string dataset = "dbo.predicates_pushed";
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        await context.ExecuteAsync(
            "IF OBJECT_ID(N'dbo.predicates_pushed', N'U') IS NOT NULL DROP TABLE dbo.predicates_pushed; "
            + "CREATE TABLE dbo.predicates_pushed ("
            + $"[code] nvarchar(64) COLLATE {SqlServerPredicateSql.ByteOrderCollation} NOT NULL PRIMARY KEY, "
            + "[population] bigint NULL, [score] float NULL, [active] bit NULL, "
            + "[reference] uniqueidentifier NULL, [seen] datetimeoffset NULL, [geometry] geometry NULL)");
        await context.Store.WriteAsync(
            dataset,
            new FeatureBatch(PredicateConformanceSuite.Schema, PredicateConformanceSuite.Rows));

        var failures = await PredicateConformanceSuite.AssertAsync(context.Store, dataset);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
