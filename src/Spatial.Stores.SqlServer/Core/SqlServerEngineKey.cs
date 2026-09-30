using Spatial.Core.Features;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The engine's own key on a table it created (ADR-0147). SQL Server grids a
/// table in clustering order, so a spatial index needs a clustered primary key
/// and a created table has none of its own; this is the key that gives it one,
/// and the two rules that keep it the engine's business:
/// <list type="bullet">
/// <item>the **column** takes the first name the creating schema does not
/// already use, so a schema is never refused for a name the contract never
/// reserved;</item>
/// <item>the **constraint** takes a name derived from the table, because a
/// constraint name is unique per schema — a fixed one would make the second
/// created dataset in a schema fail — and it is that name the two schema reads
/// leave the key out by (see <see cref="Data.SqlServerQueries.ColumnsMetadata"/>).</item>
/// </list>
/// A dataset the engine did not create this way is unaffected: an ingested
/// table's own primary key is unnamed, so the server names it, and it stays the
/// dataset's feature identity (ADR-0041).
/// </summary>
internal static class SqlServerEngineKey
{
    /// <summary>
    /// The name the key takes when the creating schema is free to use it — the
    /// same name the ingest path's <see cref="IngestIdentity.Auto"/> table uses,
    /// so the two create paths read alike to an operator.
    /// </summary>
    public const string ColumnName = SqlServerIngestPlan.AutoIdentityColumn;

    /// <summary>The primary key constraint the create statement declares, for one dataset.</summary>
    public static string Constraint(SqlServerDatasetName dataset) =>
        SqlServerIndexName.Shorten($"spatial_key_{dataset.Table}");

    /// <summary>
    /// The first key column name the schema does not already declare:
    /// <c>id</c>, then <c>id_1</c>, <c>id_2</c>… Deterministic for a given
    /// schema, and compared case-insensitively because the name must not
    /// collide with a column under the database's collation, which usually
    /// folds case.
    /// </summary>
    public static string NextColumn(IFeatureSchema schema)
    {
        for (var suffix = 0; ; suffix++)
        {
            var candidate = suffix == 0 ? ColumnName : $"{ColumnName}_{suffix}";
            if (Declares(schema, candidate) is false)
            {
                return candidate;
            }
        }
    }

    private static bool Declares(IFeatureSchema schema, string name)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (string.Equals(schema[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
