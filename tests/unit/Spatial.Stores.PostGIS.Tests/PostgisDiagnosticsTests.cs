using System.Globalization;
using Spatial.Core.Features;
using Spatial.Stores.PostGIS.Configuration;
using Spatial.Stores.PostGIS.Core;

namespace Spatial.Stores.PostGIS.Tests;

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
    public void Parse_feature_identity_reverses_composite_ids_by_kind()
    {
        var values = PostgisDiagnostics.ParseFeatureIdentity(
            [AttributeKind.Int64, AttributeKind.String, AttributeKind.Guid],
            new FeatureId("7|berlin|6F9619FF-8B86-D011-B42D-00C04FC964FF"));

        Assert.Equal(7L, values[0]);
        Assert.Equal("berlin", values[1]);
        Assert.Equal(Guid.Parse("6F9619FF-8B86-D011-B42D-00C04FC964FF"), values[2]);
    }

    [Fact]
    public void Parse_feature_identity_rejects_a_column_count_mismatch()
    {
        var exception = Assert.Throws<Spatial.PluginSdk.SpatialException>(() =>
            PostgisDiagnostics.ParseFeatureIdentity([AttributeKind.Int64, AttributeKind.Int64], new FeatureId("7")));

        Assert.Equal(Spatial.PluginSdk.SpatialException.InvalidArguments, exception.Code);
    }

    [Fact]
    public void Parse_feature_identity_handles_all_scalar_kinds()
    {
        var values = PostgisDiagnostics.ParseFeatureIdentity(
            [AttributeKind.Double, AttributeKind.Boolean, AttributeKind.DateTimeOffset],
            new FeatureId("3.14|true|2023-11-14T22:13:20+00:00"));

        Assert.Equal(3.14, values[0]);
        Assert.Equal(true, values[1]);
        Assert.Equal(DateTimeOffset.Parse("2023-11-14T22:13:20+00:00", CultureInfo.InvariantCulture), values[2]);
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
