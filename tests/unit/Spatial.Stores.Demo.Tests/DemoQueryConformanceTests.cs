using Spatial.QueryConformance;
using Spatial.Stores.Demo;

namespace Spatial.Stores.Demo.Tests;

/// <summary>
/// The demo provider against the shared pushdown-equals-reference suite
/// (ADR-0075). Its datasets are read-only and procedurally generated, so the
/// suite runs over whatever rows the provider itself returns: the comparison
/// that matters is the provider's answer against the shared reference's, and
/// the empty-box case is empty here for the same reason it is empty anywhere.
/// </summary>
public sealed class DemoQueryConformanceTests
{
    [Theory]
    [InlineData("demo.points")]
    [InlineData("demo.cities")]
    public async Task The_demo_store_matches_the_reference(string dataset)
    {
        await QueryConformanceSuite.RunAsync(new DemoStore(), dataset);
    }
}
