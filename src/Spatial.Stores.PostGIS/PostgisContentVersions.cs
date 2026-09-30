using Npgsql;
using Spatial.Contracts;
using Spatial.Stores.PostGIS.Core;
using Spatial.Stores.PostGIS.Data;

namespace Spatial.Stores.PostGIS;

/// <summary>
/// The durable content version of a PostGIS dataset (ADR-0129): a counter row
/// in the dataset's own schema, moved in the same transaction as the write.
/// It is deliberately a set of stateless statements rather than an object
/// holding state, because the read path (<see cref="PostgisStore"/>) and the
/// write paths (<see cref="PostgisEditStore"/>, <see cref="PostgisIngestStore"/>,
/// <see cref="PostgisTransactions"/>) are separate DI singletons: any fact kept
/// in process could be right in one of them and wrong in another — and a second
/// host reading the same database would be wrong about a version only the first
/// host knows. Here the only source of truth is a row in the database, read
/// fresh on every call, so every face of every process reads the same answer.
/// </summary>
/// <para>
/// A dataset the engine has never written has no row and reports
/// <see cref="ContentVersions.Unversioned"/> — exactly the behaviour ADR-0083
/// gave every store before this one. The read path never issues DDL, so a
/// read-only connection still answers.
/// </para>
internal static class PostgisContentVersions
{
    /// <summary>PostgreSQL <c>undefined_table</c> (the version table is not there).</summary>
    private const string UndefinedTable = "42P01";

    /// <summary>PostgreSQL <c>invalid_schema_name</c> (the dataset's schema is not there).</summary>
    private const string InvalidSchema = "3F000";

    /// <summary>PostgreSQL <c>insufficient_privilege</c> (the reader may not read the counter).</summary>
    private const string InsufficientPrivilege = "42501";

    /// <summary>
    /// Moves a dataset's version, on the connection and transaction of the
    /// write it belongs to: a write inside a transaction handle moves it when
    /// that transaction commits and not when it rolls back. The version table
    /// is created first, idempotently, in the dataset's own schema — the same
    /// schema the write itself needed, so this asks for no privilege the write
    /// did not already need — and idempotently rather than behind a
    /// process-local flag, which a second process would not share.
    /// </summary>
    public static async Task BumpAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PostgisDatasetName dataset,
        CancellationToken cancellationToken)
    {
        await PostgisDataStore.ExecuteNonQueryAsync(
            connection, transaction, PostgisQueries.CreateVersionTable(dataset), [], cancellationToken).ConfigureAwait(false);
        await PostgisDataStore.ExecuteNonQueryAsync(
            connection,
            transaction,
            PostgisQueries.BumpVersion(dataset),
            [dataset.Table],
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The dataset's content version as an opaque token, read from the database
    /// on every call. A missing version row, or a missing version table, is the
    /// unversioned token rather than a failure.
    /// </summary>
    public static async Task<string> VersionAsync(
        NpgsqlConnection connection, PostgisDatasetName dataset, CancellationToken cancellationToken)
    {
        try
        {
            var rows = await PostgisDataStore
                .ReadRowsAsync(connection, PostgisQueries.SelectVersion(dataset), [dataset.Table], cancellationToken)
                .ConfigureAwait(false);
            return rows.Count > 0
                ? Convert.ToString(rows[0][0], System.Globalization.CultureInfo.InvariantCulture) ?? ContentVersions.Unversioned
                : ContentVersions.Unversioned;
        }
        catch (PostgresException exception) when (IsNoVersionTable(exception))
        {
            return ContentVersions.Unversioned;
        }
    }

    /// <summary>
    /// Whether a read failure means "there is no counter this reader may read"
    /// — the answer that becomes the unversioned token rather than a
    /// <c>store.unavailable</c>. Insufficient privilege is in that set on
    /// purpose: a role granted <c>SELECT</c> table by table rather than through
    /// a schema grant may read a dataset it never wrote, and a render must not
    /// fail because it could not read a cache-invalidation side table. It
    /// degrades to the behaviour ADR-0083 gave every store, which is no worse
    /// than before this ADR.
    /// </summary>
    public static bool IsNoVersionTable(string? sqlState) =>
        sqlState is UndefinedTable or InvalidSchema or InsufficientPrivilege;

    /// <inheritdoc cref="IsNoVersionTable(string?)"/>
    public static bool IsNoVersionTable(PostgresException exception) => IsNoVersionTable(exception.SqlState);
}
