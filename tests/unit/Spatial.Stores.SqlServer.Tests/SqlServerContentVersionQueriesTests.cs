using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The content-version statements (ADR-0129): a durable counter per dataset in
/// the provider's own sidecar, created idempotently and moved by an
/// update-then-insert pair. The read guards itself on the sidecar's existence
/// rather than failing, so a read-only connection still answers and a dataset
/// the engine never wrote is the unversioned token rather than an error.
/// </summary>
public sealed class SqlServerContentVersionQueriesTests
{
    [Fact]
    public void The_version_sidecar_is_created_idempotently_and_keyed_by_qualified_name()
    {
        Assert.Equal(
            "IF OBJECT_ID(N'spatial_dataset_versions', N'U') IS NULL CREATE TABLE spatial_dataset_versions "
            + "(dataset nvarchar(300) NOT NULL PRIMARY KEY, version bigint NOT NULL)",
            SqlServerQueries.EnsureVersionTable());
    }

    [Fact]
    public void Bumping_increments_the_row_or_inserts_the_first_one()
    {
        Assert.Equal(
            "UPDATE spatial_dataset_versions SET version = version + 1 WHERE dataset = @p0; "
            + "IF @@ROWCOUNT = 0 INSERT INTO spatial_dataset_versions (dataset, version) VALUES (@p0, 1);",
            SqlServerQueries.BumpVersion());
    }

    [Fact]
    public void Reading_guards_on_the_sidecars_existence_and_binds_the_dataset_name()
    {
        Assert.Equal(
            "IF OBJECT_ID(N'spatial_dataset_versions', N'U') IS NOT NULL "
            + "SELECT TOP 1 version FROM spatial_dataset_versions WHERE dataset = @p0",
            SqlServerQueries.SelectVersion());
    }

    [Fact]
    public void The_read_is_never_a_merge()
    {
        Assert.DoesNotContain("MERGE", SqlServerQueries.BumpVersion(), StringComparison.OrdinalIgnoreCase);
    }
}
