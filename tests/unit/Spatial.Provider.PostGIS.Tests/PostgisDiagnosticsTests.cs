using Spatial.PluginSdk.Capabilities;
using Spatial.PluginSdk.Providers;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// Redaction and structured diagnostics (plan §19, architecture/distilled/host-and-clients.md): a
/// connection string or password must never appear in any error, the
/// unconfigured message is actionable, stream failures distinguish
/// cancellation, and feature identity rendering is stable.
/// </summary>
public sealed class PostgisDiagnosticsTests
{
    private static readonly CapabilityId Scan = FeatureScanContract.Id;

    [Fact]
    public void Provider_failure_redacts_the_connection_string_and_password()
    {
        var configuration = PostgisConnectionConfiguration.FromConnectionString(
            "Host=db.internal;Port=5432;Database=spatial;Username=admin;Password=hunter2-secret");

        var error = PostgisDiagnostics.ProviderFailure(configuration, Scan, new InvalidOperationException(
            $"cannot reach Host=db.internal;Port=5432;Database=spatial;Username=admin;Password=hunter2-secret - timeout"));

        Assert.Equal(CapabilityErrorKind.ProviderFailure, error.Kind);
        Assert.DoesNotContain("hunter2-secret", error.Message);
        Assert.DoesNotContain("db.internal", error.Message);
        Assert.DoesNotContain("admin", error.Message);
        Assert.Contains("[redacted]", error.Message);
        Assert.Contains("'spatial'", error.Message);
    }

    [Fact]
    public void Password_alone_is_redacted_from_provider_supplied_detail()
    {
        var configuration = PostgisConnectionConfiguration.FromConnectionString(
            "Host=localhost;Username=admin;Password=s3cret;Database=spatial");

        var scrubbed = configuration.Redact("authentication failed for user \"admin\" with password \"s3cret\"");

        Assert.DoesNotContain("s3cret", scrubbed);
        Assert.Contains("[redacted]", scrubbed);
    }

    [Fact]
    public void Unavailable_error_names_the_environment_variable_and_no_secret()
    {
        var error = PostgisDiagnostics.Unavailable(Scan);

        Assert.Equal(CapabilityErrorKind.ProviderUnavailable, error.Kind);
        Assert.Equal("provider.unavailable", error.Code);
        Assert.Contains(PostgisConnectionConfiguration.EnvironmentVariable, error.Message);
    }

    [Fact]
    public void Stream_failure_maps_cancellation_and_redacts_other_failures()
    {
        var configuration = PostgisConnectionConfiguration.FromConnectionString(
            "Host=localhost;Password=secret;Database=spatial;Username=admin");

        var cancelled = PostgisDiagnostics.StreamFailure(
            Scan, new OperationCanceledException(), configuration);

        Assert.Equal(CapabilityErrorKind.Cancelled, cancelled.Kind);
        Assert.Equal("operation.cancelled", cancelled.Code);

        var failed = PostgisDiagnostics.StreamFailure(
            Scan, new Npgsql.NpgsqlException("connection to Host=localhost;Password=secret;Database=spatial;Username=admin failed"), configuration);

        Assert.Equal(CapabilityErrorKind.ProviderFailure, failed.Kind);
        Assert.DoesNotContain("secret", failed.Message);
    }

    [Fact]
    public void Feature_identity_joins_keys_and_falls_back_to_ordinals()
    {
        Assert.Equal("7|berlin", PostgisDiagnostics.FeatureIdentity([0, 1], [7L, "berlin"], 9));
        Assert.Equal("9", PostgisDiagnostics.FeatureIdentity([], [7L], 9));
        Assert.Equal("7|", PostgisDiagnostics.FeatureIdentity([0, 1], [7L, null], 9));
    }

    [Fact]
    public void Redact_handles_empty_messages()
    {
        var configuration = PostgisConnectionConfiguration.FromConnectionString(
            "Host=localhost;Database=spatial;Username=admin;Password=pw");

        Assert.Equal(string.Empty, configuration.Redact(string.Empty));
    }

    [Fact]
    public void Redacted_key_never_contains_the_password()
    {
        var configuration = PostgisConnectionConfiguration.FromConnectionString(
            "Host=x;Password=pw123;Database=spatial;Username=admin");

        Assert.DoesNotContain("pw123", configuration.RedactedKey);
        Assert.Contains("'spatial'", configuration.RedactedKey);
    }
}
