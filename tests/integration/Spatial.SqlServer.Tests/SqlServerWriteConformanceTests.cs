using Spatial.Stores.SqlServer;
using Spatial.WriteConformance;

namespace Spatial.SqlServer.Tests;

/// <summary>
/// The SQL Server provider against the shared write conformance suite
/// (ADR-0037, ADR-0041, ADR-0065). SQL Server's write planners are shaped by
/// the batch's schema already — the rule the PostGIS planner was missing when
/// this suite first ran against it — so the same facts are asserted here to
/// keep the two providers honest against each other. The datasets live under
/// the store's <c>dbo</c> schema, and every test skips with an explicit reason
/// without Docker (ADR-0187).
/// </summary>
[Collection(SqlServerContainerDefinition.Name)]
public sealed class SqlServerWriteConformanceTests : IClassFixture<SqlServerDatabaseFixture>
{
    private readonly SqlServerDatabaseFixture _fixture;

    public SqlServerWriteConformanceTests(SqlServerDatabaseFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_sql_server_store_matches_the_write_conformance_suite()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = SqlServerTestContext.Create(_fixture.ConnectionString);
        var harness = new WriteConformanceHarness(
            "dbo",
            context.Store,
            context.Store,
            context.Editor,
            context.Attachments,
            context.Ingest,
            cap => new SqlServerAttachmentStore(context.Store, cap));

        await WriteConformanceSuite.RunAsync(harness);
    }
}
