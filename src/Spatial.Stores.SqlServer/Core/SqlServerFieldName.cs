using System.Text;
using Spatial.Contracts;
using Spatial.Core.Features;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// A column name discovered in an ingest/create schema (ADR-0041 §3). Unlike a
/// dataset identifier (<see cref="SqlServerDatasetName"/>, which parses
/// client text and must be a bare lowercase identifier), a field name is
/// data: real files carry mixed case (<c>LABELRANK</c>), camel case
/// (<c>magType</c>) and spaces, and the store keeps them verbatim. It is safe
/// because every generated statement bracket-quotes the name, so only names a
/// quoted identifier cannot represent are refused: an empty name, a name
/// containing a closing bracket or NUL, or a name past SQL Server's 128-byte
/// identifier limit (which the server would silently truncate, risking a
/// collision).
/// </summary>
internal static class SqlServerFieldName
{
    /// <summary>SQL Server truncates identifiers to 128 bytes.</summary>
    public const int MaxBytes = 128;

    /// <summary>Whether <paramref name="name"/> can be carried as a bracketed column identifier.</summary>
    public static bool IsValid(string? name) => TryValidate(name, out _);

    /// <summary>Validates <paramref name="name"/>, returning an actionable reason when it cannot be carried.</summary>
    public static bool TryValidate(string? name, out string reason)
    {
        reason = string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            reason = "the name is empty";
            return false;
        }

        if (name.Contains(']'))
        {
            reason = "it contains a closing bracket, which a bracketed identifier cannot carry";
            return false;
        }

        if (name.Contains('\0'))
        {
            reason = "it contains a NUL character";
            return false;
        }

        if (Encoding.UTF8.GetByteCount(name) > MaxBytes)
        {
            reason = $"it is longer than SQL Server's {MaxBytes}-byte identifier limit";
            return false;
        }

        return true;
    }

    /// <summary>Throws <c>invalid.arguments</c> for the first field of <paramref name="schema"/> that cannot be carried.</summary>
    public static void RequireValid(SqlServerDatasetName dataset, IFeatureSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        foreach (var field in schema.Fields)
        {
            if (!TryValidate(field.Name, out var reason))
            {
                throw SpatialException.BadArguments(
                    $"The field name '{field.Name}' of dataset '{dataset}' cannot be used as a column: {reason}.");
            }
        }
    }
}
