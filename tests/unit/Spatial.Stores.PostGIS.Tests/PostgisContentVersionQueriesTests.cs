using Spatial.Stores.PostGIS;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS.Tests;

/// <summary>
/// The content-version statements (ADR-0129): a durable counter per dataset,
/// in the dataset's own schema, created idempotently and moved by a single
/// statement so a concurrent writer never reads-then-writes. The dataset
/// identifier is validated before it reaches here, so the quoted parts are
/// never free text, and the dataset's own name is a bound parameter.
/// </summary>
public sealed class PostgisContentVersionQueriesTests
{
    private static PostgisDatasetName Dataset()
    {
        Assert.True(PostgisDatasetName.TryParse("public.places", out var dataset, out _));
        return dataset;
    }

    [Fact]
    public void The_version_table_lives_in_the_datasets_own_schema_and_is_created_idempotently()
    {
        Assert.Equal(
            "CREATE TABLE IF NOT EXISTS \"public\".\"spatial_dataset_version\" "
            + "(\"dataset\" text PRIMARY KEY, \"version\" bigint NOT NULL)",
            PostgisQueries.CreateVersionTable(Dataset()));
    }

    [Fact]
    public void Bumping_inserts_the_first_row_or_increments_the_one_there_is()
    {
        Assert.Equal(
            "INSERT INTO \"public\".\"spatial_dataset_version\" (\"dataset\", \"version\") VALUES (@p0, 1) "
            + "ON CONFLICT (\"dataset\") DO UPDATE SET \"version\" = spatial_dataset_version.\"version\" + 1",
            PostgisQueries.BumpVersion(Dataset()));
    }

    [Fact]
    public void Reading_takes_the_dataset_name_as_a_bound_parameter()
    {
        Assert.Equal(
            "SELECT \"version\" FROM \"public\".\"spatial_dataset_version\" WHERE \"dataset\" = @p0",
            PostgisQueries.SelectVersion(Dataset()));
    }

    [Fact]
    public void The_schema_part_is_the_datasets_own()
    {
        Assert.True(PostgisDatasetName.TryParse("tenant_a.places", out var dataset, out _));

        Assert.Contains("\"tenant_a\".\"spatial_dataset_version\"", PostgisQueries.SelectVersion(dataset), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("42P01")] // undefined_table
    [InlineData("3F000")] // invalid_schema_name
    [InlineData("42501")] // insufficient_privilege
    public void A_read_that_cannot_see_the_counter_reports_the_unversioned_token(string sqlState)
    {
        Assert.True(PostgisContentVersions.IsNoVersionTable(sqlState));
    }

    [Theory]
    [InlineData("08006")] // connection_failure
    [InlineData("53300")] // too_many_connections
    [InlineData("57014")] // query_canceled
    public void Any_other_read_failure_is_a_store_failure(string sqlState)
    {
        Assert.False(PostgisContentVersions.IsNoVersionTable(sqlState));
    }
}
