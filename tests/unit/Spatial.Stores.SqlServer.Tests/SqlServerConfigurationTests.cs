using Spatial.Contracts;
using Spatial.Stores.SqlServer.Configuration;

namespace Spatial.Stores.SqlServer.Tests;

/// <summary>
/// The connection configuration and the store's argument surface (ADR-0072):
/// the secret never reaches a diagnostic, an unconfigured store fails with
/// <c>store.unavailable</c> naming the setting, and a client-supplied dataset
/// identifier never becomes SQL.
/// </summary>
public sealed class SqlServerConfigurationTests
{
    private const string Secret = "Str0ng-Passw0rd";

    [Fact]
    public void An_explicit_connection_string_is_configured()
    {
        var configuration = SqlServerConnectionConfiguration.FromConnectionString(ConnectionString);

        Assert.True(configuration.IsConfigured);
        Assert.Contains("spatial", configuration.RedactedKey, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_environment_value_is_unconfigured_and_names_the_setting(string? value)
    {
        var configuration = SqlServerConnectionConfiguration.FromEnvironmentValue(value);

        Assert.False(configuration.IsConfigured);
        Assert.Contains(SqlServerConnectionConfiguration.EnvironmentVariable, configuration.RedactedKey, StringComparison.Ordinal);
    }

    [Fact]
    public void Redaction_scrubs_the_connection_string_and_the_bare_password()
    {
        var configuration = SqlServerConnectionConfiguration.FromConnectionString(ConnectionString);

        var scrubbed = configuration.Redact(
            $"failed to reach {ConnectionString} with {Secret} rejected");

        Assert.DoesNotContain(Secret, scrubbed, StringComparison.Ordinal);
        Assert.DoesNotContain(ConnectionString, scrubbed, StringComparison.Ordinal);
        Assert.Equal(2, scrubbed.Split("[redacted]", StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void Redaction_of_an_unconfigured_store_leaves_the_message_alone()
    {
        var configuration = SqlServerConnectionConfiguration.FromEnvironmentValue(null);

        Assert.Equal("nothing to hide", configuration.Redact("nothing to hide"));
    }

    [Fact]
    public async Task An_unconfigured_store_fails_with_store_unavailable()
    {
        await using var store = new SqlServerStore(new SqlServerOptions { ConnectionString = string.Empty });

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.ListAsync());

        Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
        Assert.Contains(SqlServerOptions.EnvironmentVariable, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unconfigured_store_rejects_every_face_with_the_same_code()
    {
        await using var store = new SqlServerStore(new SqlServerOptions { ConnectionString = string.Empty });
        var schema = new Spatial.Core.Features.FeatureSchema(
            [new Spatial.Core.Features.FieldDefinition("id", Spatial.Core.Features.AttributeKind.Int64, false)]);

        var catalogue = await Assert.ThrowsAsync<SpatialException>(() => store.DescribeAsync("dbo.places"));
        var scan = await Assert.ThrowsAsync<SpatialException>(() => store.ScanAsync("dbo.places"));
        var write = await Assert.ThrowsAsync<SpatialException>(() =>
            store.WriteAsync("dbo.places", new Spatial.Core.Features.FeatureBatch(schema, [])));
        var begin = await Assert.ThrowsAsync<SpatialException>(() => store.BeginAsync());

        Assert.All(new[] { catalogue, scan, write, begin }, failure =>
        {
            Assert.Equal(SpatialException.StoreUnavailable, failure.Code);
            Assert.Contains("Spatial:SqlServer:ConnectionString", failure.Message, StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("dbo.places; DROP TABLE dbo.places")]
    [InlineData("dbo.Places")]
    [InlineData("")]
    [InlineData(null)]
    public async Task A_client_dataset_identifier_never_reaches_sql(string? dataset)
    {
        await using var store = new SqlServerStore(new SqlServerOptions { ConnectionString = ConnectionString });

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.ScanAsync(dataset!));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task An_inverted_bounding_box_is_rejected_before_any_connection()
    {
        await using var store = new SqlServerStore(new SqlServerOptions { ConnectionString = ConnectionString });

        var failure = await Assert.ThrowsAsync<SpatialException>(() => store.QueryAsync(
            "dbo.places", new Spatial.Contracts.BoundingBox(10, 10, 1, 1)));

        Assert.Equal(SpatialException.InvalidArguments, failure.Code);
    }

    [Fact]
    public async Task An_empty_identity_list_short_circuits_without_a_connection()
    {
        await using var store = new SqlServerStore(new SqlServerOptions { ConnectionString = ConnectionString });

        Assert.Empty(await store.GetAsync("dbo.nowhere", []));
    }

    [Fact]
    public void The_options_read_the_documented_environment_variable()
    {
        Assert.Equal("SPATIAL_SQLSERVER_CONNECTION", SqlServerOptions.EnvironmentVariable);
        Assert.Equal(SqlServerOptions.EnvironmentVariable, SqlServerConnectionConfiguration.EnvironmentVariable);
    }

    private static string ConnectionString =>
        $"Server=localhost,1433;Database=spatial;User ID=sa;Password={Secret};Encrypt=False;TrustServerCertificate=True";
}
