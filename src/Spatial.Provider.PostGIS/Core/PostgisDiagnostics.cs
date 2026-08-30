using System.Globalization;
using Spatial.PluginSdk.Capabilities;
using Spatial.Provider.PostGIS.Configuration;
using Spatial.Provider.PostGIS.Geometry;

namespace Spatial.Provider.PostGIS.Core;

/// <summary>
/// Uniform failure mapping and redaction for the PostGIS provider (plan §19,
/// ADR-0028): input problems (dataset/filter/batch/target errors the caller
/// can fix, write-side CRS conflicts) are <c>invalid.arguments</c>; malformed
/// stored geometry and database/storage failures are <c>provider.failure</c>.
/// Every message passes through the configuration's redaction first, so a
/// connection string or password can never appear in a diagnostic. The
/// no-configuration state is a separate actionable
/// <c>provider.unavailable</c> (security-model.md).
/// </summary>
internal static class PostgisDiagnostics
{
    /// <summary>Maps a completed exception to a provider-failure error with the secret scrubbed.</summary>
    public static CapabilityError ProviderFailure(PostgisConnectionConfiguration configuration, CapabilityId capability, Exception exception)
    {
        var detail = configuration.Redact(exception.Message);
        return CapabilityError.ProviderFailure(
            $"{capability} failed against {configuration.RedactedKey}: {detail}");
    }

    /// <summary>Builds the no-connection-configuration error (actionable, secret-free).</summary>
    public static CapabilityError Unavailable(CapabilityId capability) =>
        CapabilityError.ProviderUnavailable(
            $"{capability} cannot run: the provider has no connection configuration. "
            + $"Set {PostgisConnectionConfiguration.EnvironmentVariable} in the provider's launch environment.");

    /// <summary>Fails a streaming capability: cancellation fails the stream, everything else is a redacted provider failure.</summary>
    public static ICapabilityError StreamFailure(CapabilityId capability, Exception exception, PostgisConnectionConfiguration configuration)
    {
        if (exception is OperationCanceledException)
        {
            return CapabilityError.Cancelled(capability);
        }

        return CapabilityError.ProviderFailure(
            $"{capability} failed while streaming: {configuration.Redact(exception.Message)}");
    }

    /// <summary>Builds an invalid.arguments error naming the offending value.</summary>
    public static CapabilityError InvalidArgument(CapabilityId capability, string detail) =>
        CapabilityError.InvalidArguments($"{capability} rejected the input: {detail}");

    /// <summary>Renders a stored geometry id (primary key values joined with '|', else a row ordinal).</summary>
    public static string FeatureIdentity(IReadOnlyList<int> identityIndexes, IReadOnlyList<object?> values, long ordinal)
    {
        if (identityIndexes.Count == 0)
        {
            return ordinal.ToString(CultureInfo.InvariantCulture);
        }

        var parts = new string[identityIndexes.Count];
        for (var i = 0; i < identityIndexes.Count; i++)
        {
            parts[i] = values[identityIndexes[i]]?.ToString() ?? string.Empty;
        }

        return string.Join('|', parts);
    }

    /// <summary>Reads a date-only or timestamp value into a <see cref="DateTimeOffset"/> (UTC unless the value says otherwise).</summary>
    public static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, value.Kind is DateTimeKind.Utc or DateTimeKind.Local ? value.Kind : DateTimeKind.Utc));
}
