using Microsoft.Data.SqlClient;
using Spatial.Contracts;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// How a SQL Server error number becomes a contract error code
/// (ADR-0033's structured failures, as the SQL Server provider follows them).
/// A per-feature edit failure keeps the engine's own vocabulary rather than
/// leaking a provider number: a constraint violation and a bad request are
/// <c>invalid.arguments</c>, a missing object is <c>not.found</c>, and
/// everything else stays <c>invalid.arguments</c> because no other error is
/// attributable to the caller's input. The numbers are matched in one table so
/// the mapping is testable without a database.
/// </summary>
internal static class SqlServerFailureCode
{
    /// <summary>The SQL Server error for an object name that does not exist.</summary>
    public const int InvalidObjectName = 208;

    /// <summary>The SQL Server error for a primary key constraint violation.</summary>
    public const int ConstraintViolation = 2627;

    /// <summary>The SQL Server error for a unique index violation.</summary>
    public const int UniqueIndexViolation = 2601;

    /// <summary>The SQL Server error for a table that already exists.</summary>
    public const int ObjectExists = 2714;

    private static readonly Dictionary<int, string> Codes = new()
    {
        [InvalidObjectName] = SpatialException.NotFound,
        [ConstraintViolation] = SpatialException.InvalidArguments,
        [UniqueIndexViolation] = SpatialException.InvalidArguments,
        [ObjectExists] = SpatialException.InvalidArguments,
    };

    /// <summary>The contract error code for a provider failure.</summary>
    public static string For(SqlException exception) => ForNumber(exception.Number);

    /// <summary>The contract error code for a SQL Server error number (pure, so the mapping is unit-testable).</summary>
    public static string ForNumber(int number) =>
        Codes.TryGetValue(number, out var code) ? code : SpatialException.InvalidArguments;

    /// <summary>Whether a provider failure means the dataset already exists (the create contract's failure).</summary>
    public static bool IsAlreadyCreated(SqlException exception) => IsAlreadyCreatedNumber(exception.Number);

    /// <summary>Whether an error number means the dataset already exists (pure, for the same reason as <see cref="ForNumber"/>).</summary>
    public static bool IsAlreadyCreatedNumber(int number) => number == ObjectExists;
}
