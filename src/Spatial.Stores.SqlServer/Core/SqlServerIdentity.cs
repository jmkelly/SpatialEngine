using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The identity of a feature in a SQL Server dataset: the dataset's id columns,
/// their attribute kinds, and the bind parameters that select a set of
/// features by identity (ADR-0037). Identity order is the id-column order, so
/// one parameter tuple per requested <see cref="FeatureId"/>.
/// </summary>
internal static class SqlServerIdentity
{
    /// <summary>The attribute kind of each identity column, in id-column order.</summary>
    internal static AttributeKind[] Kinds(DatasetDescription description) =>
        description.IdColumns
            .Select(column => description.Schema[description.Schema.IndexOf(column)].Kind)
            .ToArray();

    /// <summary>The bound parameter tuple selecting one feature by identity.</summary>
    internal static object?[] Values(DatasetDescription description, FeatureId id) =>
        SqlServerDiagnostics.ParseFeatureIdentity(Kinds(description), id);

    /// <summary>One bound parameter tuple per requested id, flattened for the statement's parameter list.</summary>
    internal static List<object?> Parameters(DatasetDescription description, IReadOnlyList<FeatureId> ids)
    {
        var kinds = Kinds(description);
        var parameters = new List<object?>(ids.Count * kinds.Length);
        foreach (var id in ids)
        {
            parameters.AddRange(SqlServerDiagnostics.ParseFeatureIdentity(kinds, id));
        }

        return parameters;
    }
}
