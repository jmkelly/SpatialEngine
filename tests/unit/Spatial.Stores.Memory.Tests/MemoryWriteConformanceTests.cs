using Spatial.Stores.Memory;
using Spatial.WriteConformance;

namespace Spatial.Stores.Memory.Tests;

/// <summary>
/// The in-memory provider against the shared write conformance suite
/// (ADR-0037, ADR-0041, ADR-0065). The memory store is the reference the
/// PostGIS and SQL Server suites are read against, so it also runs the suite
/// itself: if the reference's write semantics change, this fails before a
/// provider is measured against the old ones — exactly the shape
/// <c>MemoryQueryConformanceTests</c> gives the read path.
/// </summary>
public sealed class MemoryWriteConformanceTests
{
    [Fact]
    public async Task The_memory_store_matches_the_write_conformance_suite()
    {
        var store = new MemoryStore();
        var harness = new WriteConformanceHarness(
            "memory",
            store,
            store,
            new MemoryEditor(store),
            new MemoryAttachments(store),
            new MemoryIngest(store),
            cap => new MemoryAttachments(store, cap));

        await WriteConformanceSuite.RunAsync(harness);
    }
}
