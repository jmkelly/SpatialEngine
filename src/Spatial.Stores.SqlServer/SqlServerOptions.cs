namespace Spatial.Stores.SqlServer;

/// <summary>
/// Options for the SQL Server store (ADR-0033): the connection string comes
/// from host configuration (<c>Spatial:SqlServer:ConnectionString</c>) or the
/// <c>SPATIAL_SQLSERVER_CONNECTION</c> environment variable — never from a
/// request body. Empty means unconfigured: every operation throws
/// <c>store.unavailable</c> naming the setting.
/// </summary>
public sealed class SqlServerOptions
{
    public const string EnvironmentVariable = "SPATIAL_SQLSERVER_CONNECTION";

    /// <summary>How long a discovered description is reused before it is re-read (ADR-0151).</summary>
    public static readonly TimeSpan DefaultDescriptionCacheTtl = TimeSpan.FromSeconds(30);

    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Whether a created dataset gets its spatial and attribute indexes
    /// (ADR-0092). On by default, because a dataset with no indexes makes every
    /// pushed-down query a scan; turn it off only for a bulk load that would
    /// rather build the indexes afterwards, and accept that the dataset is
    /// unindexed until it does.
    /// </summary>
    public bool CreateIndexes { get; set; } = true;

    /// <summary>
    /// How long the store reuses a dataset description it has discovered from
    /// the catalogue before reading it again (ADR-0151). Every read face
    /// describes its dataset first, so this is what keeps a walk of <c>N</c>
    /// pages over one layer to a single description read rather than one per
    /// page.
    ///
    /// <para>
    /// Every write the store performs drops the descriptions it can have
    /// changed, so the window is about changes made <em>outside</em> the store
    /// — a hand-run <c>ALTER TABLE</c>, a migration by another process — and not
    /// about the store's own. Within it, a description read before the change
    /// is the one reads keep answering with; a column added out of band is not
    /// in the schema until the entry expires. A non-positive value turns the
    /// cache off, which is the store's earlier behaviour and the right setting
    /// for a database whose schema moves on a schedule this store cannot see.
    /// </para>
    /// </summary>
    public TimeSpan DescriptionCacheTtl { get; set; } = DefaultDescriptionCacheTtl;

    public static SqlServerOptions FromEnvironment() =>
        new() { ConnectionString = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? string.Empty };
}
