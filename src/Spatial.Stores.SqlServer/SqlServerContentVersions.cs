using Microsoft.Data.SqlClient;
using Spatial.Contracts;
using Spatial.Stores.SqlServer.Core;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The durable content version of a SQL Server dataset (ADR-0129): a counter
/// row in the provider's own sidecar table, moved in the same transaction as
/// the write. It is deliberately a set of stateless statements rather than an
/// object holding state, because the read path
/// (<see cref="SqlServerStore"/>) and the write paths
/// (<see cref="SqlServerEditStore"/>, <see cref="SqlServerIngestStore"/>,
/// <see cref="SqlServerTransactions"/>) are separate DI singletons: any fact
/// kept in process could be right in one of them and wrong in another — and a
/// second host reading the same database would be wrong about a version only
/// the first host knows. Here the only source of truth is a row in the
/// database, read fresh on every call, so every face of every process reads
/// the same answer.
/// </summary>
/// <para>
/// It sits beside <c>spatial_datasets</c>, the SRID sidecar ADR-0073 already
/// keeps, and is keyed by the dataset's qualified name: a SQL Server dataset
/// may live in a schema the engine never created anything in, so a per-schema
/// table would ask for a privilege the write did not need. The read never
/// issues DDL — the statement guards itself on the table's existence and
/// answers with no row when it is not there — so a read-only connection still
/// answers, and a dataset the engine has never written reports
/// <see cref="ContentVersions.Unversioned"/>, exactly the behaviour ADR-0083
/// gave every store before this one.
/// </para>
internal static class SqlServerContentVersions
{
    /// <summary>
    /// Moves a dataset's version, on the connection and transaction of the
    /// write it belongs to: a write inside a transaction handle moves it when
    /// that transaction commits and not when it rolls back. The sidecar is
    /// created first, idempotently, so the write needs no process-local
    /// "already created" flag that a second process would not share.
    /// </summary>
    public static async Task BumpAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        SqlServerDatasetName dataset,
        CancellationToken cancellationToken)
    {
        await SqlServerDataStore.ExecuteNonQueryAsync(
            connection, transaction, SqlServerQueries.EnsureVersionTable(), [], cancellationToken).ConfigureAwait(false);
        await SqlServerDataStore.ExecuteNonQueryAsync(
            connection, transaction, SqlServerQueries.BumpVersion(), [dataset.Qualified], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The dataset's content version as an opaque token, read from the database
    /// on every call. No row (or no sidecar) is the unversioned token rather
    /// than a failure.
    /// </summary>
    public static async Task<string> VersionAsync(
        SqlConnection connection, SqlServerDatasetName dataset, CancellationToken cancellationToken)
    {
        var rows = await SqlServerDataStore
            .ReadRowsAsync(connection, SqlServerQueries.SelectVersion(), [dataset.Qualified], cancellationToken)
            .ConfigureAwait(false);
        return rows.Count > 0
            ? Convert.ToString(rows[0][0], System.Globalization.CultureInfo.InvariantCulture) ?? ContentVersions.Unversioned
            : ContentVersions.Unversioned;
    }
}
