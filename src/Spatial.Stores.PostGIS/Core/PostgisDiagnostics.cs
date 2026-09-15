using System.Globalization;
using Spatial.Contracts;
using Spatial.Core.Features;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// Pure row-identity and date helpers for the PostGIS store (ADR-0033).
/// Failure mapping lives in <see cref="PostgisStore"/> via
/// <see cref="Spatial.Contracts.SpatialException"/>; every message passes
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

    /// <summary>
    /// Reverses <see cref="FeatureIdentity"/> for an edit: splits a composite
    /// id on '|' and converts each part to its identity column's type
    /// (ADR-0037). A malformed id yields a typed invalid-argument failure.
    /// </summary>
    public static object?[] ParseFeatureIdentity(
        IReadOnlyList<AttributeKind> identityKinds,
        FeatureId id)
    {
        var parts = id.Value.Split('|');
        if (parts.Length != identityKinds.Count)
        {
            throw SpatialException.BadArguments(
                $"Feature id '{id.Value}' does not match the dataset's {identityKinds.Count} identity column(s).");
        }

        var values = new object?[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            values[i] = ParseIdentityPart(identityKinds[i], parts[i]);
        }

        return values;
    }

    private static readonly Dictionary<AttributeKind, Func<string, object?>> IdentityParsers = new()
    {
        [AttributeKind.Int64] = value => long.Parse(value, CultureInfo.InvariantCulture),
        [AttributeKind.Double] = value => double.Parse(value, CultureInfo.InvariantCulture),
        [AttributeKind.Boolean] = value => bool.Parse(value),
        [AttributeKind.Guid] = value => Guid.Parse(value),
        [AttributeKind.DateTimeOffset] = value => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture),
    };

    private static object? ParseIdentityPart(AttributeKind kind, string value) =>
        IdentityParsers.TryGetValue(kind, out var parse) ? parse(value) : value;

    /// <summary>Reads a date-only or timestamp value into a <see cref="DateTimeOffset"/> (UTC unless the value says otherwise).</summary>
    public static DateTimeOffset ToDateTimeOffset(DateTime value) =>
        new(DateTime.SpecifyKind(value, value.Kind is DateTimeKind.Utc or DateTimeKind.Local ? value.Kind : DateTimeKind.Utc));
}
