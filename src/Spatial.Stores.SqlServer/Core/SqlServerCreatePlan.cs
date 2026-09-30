using Spatial.Core.Features;
using Spatial.Stores.SqlServer.Data;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The pure planning of one <c>IDataCatalogue.CreateAsync</c> (ADR-0147): the
/// table definition a created dataset is given and the index statements it
/// carries. Kept free of SqlClient, like <see cref="SqlServerIngestPlan"/>, so
/// the decision SQL Server forces on this path is a test rather than a
/// discovery: **a spatial index is only built on a table that has a clustered
/// primary key**, because the grid's cell order has to agree with the way rows
/// A created table has no key of its own, so the plan adds one: the engine's,
/// on a name the creating schema is free of (see <see cref="SqlServerEngineKey"/>).
/// <para>
/// The key is the engine's own business and never reaches the contract: the
/// create statement declares it under a constraint name derived from the
/// table (<see cref="SqlServerEngineKey.Constraint"/>) and the two schema reads
/// leave a column keyed that way out of the columns and the primary key they
/// report, and a created dataset stays the keyless dataset ADR-0131 and
/// ADR-0140 are written against. Without that, a created SQL Server dataset
/// would grow a field and a feature identity that a created PostGIS dataset does
/// not have — a difference between two providers answering the same contract,
/// made for one provider's storage engine.
/// </para>
/// <para>
/// The key exists for the spatial index and nothing else, so it follows
/// <c>SqlServerOptions.CreateIndexes</c> (the option the storage carries): with
/// index creation off there is no index to grid and the created table is the
/// one ADR-0092 shipped.
/// </para>
/// </summary>
internal sealed record SqlServerCreatePlan(
    SqlServerDatasetName Dataset,
    FeatureSchema Schema,
    string? IdentityColumn)
{
    /// <summary>The DDL the create transaction runs, key included.</summary>
    public string CreateTableSql() => SqlServerQueries.CreateTable(Dataset, Schema, IdentityColumn);

    /// <summary>
    /// The index statements the create transaction runs (ADR-0092), which is
    /// where the engine key earns its place: it is what makes
    /// <c>clusteredPrimaryKey</c> true, and a spatial index is emitted only then.
    /// Empty when index creation is off, because that is the one thing the
    /// engine key exists for.
    /// </summary>
    public IReadOnlyList<string> CreateIndexSql() =>
        IdentityColumn is null
            ? []
            : SqlServerIndexPlan.CreateIndexes(Dataset, Schema, clusteredPrimaryKey: true);

    /// <summary>
    /// Validates a dataset and the schema it is created with, and decides
    /// whether the engine key is added, and under what name.
    /// </summary>
    public static SqlServerCreatePlan Create(
        SqlServerDatasetName dataset, FeatureSchema schema, bool createIndexes)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return new SqlServerCreatePlan(dataset, schema, createIndexes ? SqlServerEngineKey.NextColumn(schema) : null);
    }
}
