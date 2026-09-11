using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Core;

namespace Spatial.Provider.PostGIS.Tests;

/// <summary>
/// Redaction and identity helpers (ADR-0033): a connection string or
/// password must never appear in any diagnostic, and feature identity
/// rendering is stable.
/// </summary>
public sealed class PostgisDiagnosticsTests
{
    [Fact]
    public void Redaction_scrubs_the_connection_string_and_password()
    {
        var configuration = PostgisConnectionConfiguration.FromConnectionString(
            "Host=db.internal;Port=5432;Database=spatial;Username=admin;Password=hunter2-secret");

        var scrubbed = configuration.Redact(
            "cannot reach Host=db.internal;Port=5432;Database=spatial;Username=admin;Password=hunter2-secret - timeout");

        Assert.DoesNotContain("hunter2-secret", scrubbed);
        Assert.DoesNotContain("db.internal", scrubbed);
        Assert.Contains("[redacted]", scrubbed);
        Assert.Contains("'spatial'", configuration.RedactedKey);
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

    [Fact]
    public void Unconfigured_store_reports_its_setting()
    {
        Assert.Contains(
            PostgisOptions.EnvironmentVariable,
            PostgisConnectionConfiguration.FromEnvironmentValue(null).RedactedKey);
    }
}
