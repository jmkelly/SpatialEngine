using Spatial.Stores.PostGIS;
using Spatial.WriteConformance;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The PostGIS provider against the shared write conformance suite (ADR-0037,
/// ADR-0041, ADR-0065): over a real PostGIS container, the same facts the
/// memory store answers are asserted of the PostGIS write path, so a
/// divergence between the two stores is a failing test rather than a reading.
/// The dataset names are the store's own <c>public</c> schema and every test
/// skips with an explicit reason without Docker (ADR-0189).
/// </summary>
[Collection(PostgisContainerDefinition.Name)]
public sealed class PostgisWriteConformanceTests : IClassFixture<PostgisDatabaseFixture>
{
    private readonly PostgisDatabaseFixture _fixture;

    public PostgisWriteConformanceTests(PostgisDatabaseFixture fixture) => _fixture = fixture;

    [SkippableFact]
    public async Task The_postgis_store_matches_the_write_conformance_suite()
    {
        Skip.If(!_fixture.DockerAvailable, _fixture.SkipReason ?? "no reason");
        await using var context = PostgisTestContext.Create(_fixture.ConnectionString);
        var harness = new WriteConformanceHarness(
            "public",
            context.Store,
            context.Store,
            context.Editor,
            context.Attachments,
            context.Ingest,
            cap => new PostgisAttachmentStore(context.Store, cap));

        await WriteConformanceSuite.RunAsync(harness);
    }
}
