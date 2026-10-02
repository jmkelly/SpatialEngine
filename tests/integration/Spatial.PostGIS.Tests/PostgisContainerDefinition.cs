using Xunit;

namespace Spatial.PostGIS.Tests;

/// <summary>
/// The one collection the container-backed classes belong to. Its fixture is
/// <see cref="PostgisContainerFixture"/>, so the container is started once for
/// the assembly rather than once per class — fifteen PostGIS containers
/// starting at once under a loaded lane is what degraded the suite into a run
/// that reported most of itself as skipped while saying nothing about why
/// (SpatialEngine-o5p, the same defect ADR-0187 fixed for SQL Server).
/// </summary>
/// <remarks>
/// The collection does not run its classes in parallel: they share one
/// PostGIS instance, and each is given a database of its own
/// (<see cref="PostgisDatabaseFixture"/>), so running them one at a time costs
/// the suite nothing it was not already paying in container starts — and it is
/// also what lets the assembly's whole-process allocation measurement
/// (<see cref="GC.GetTotalAllocatedBytes"/>) keep its own figure, since no
/// other class in the assembly is running alongside this one.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgisContainerDefinition : ICollectionFixture<PostgisContainerFixture>
{
    public const string Name = "PostGIS container";
}
