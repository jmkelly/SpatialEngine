using System.Globalization;

namespace Spatial.Provider.PostGIS.Core;

/// <summary>
/// Pure row-identity and date helpers for the PostGIS store (ADR-0033).
/// Failure mapping lives in <see cref="PostgisStore"/> via
/// <see cref="Spatial.PluginSdk.SpatialException"/>; every message passes
/// through the configuration's redaction there, so a connection string or
/// password can never appear in a diagnostic.
/// </summary>
internal static class PostgisDiagnostics
{
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
