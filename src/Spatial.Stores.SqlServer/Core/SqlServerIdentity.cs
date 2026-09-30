using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.SqlServer.Core;

/// <summary>
/// The identity of a feature in a SQL Server dataset: the dataset's id columns,
/// their attribute kinds, the bind parameters that select a set of
/// features by identity (ADR-0037), and the comparison those parameters are
/// made in (ADR-0126). Identity order is the id-column order, so
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

    /// <summary>
    /// One identity column as the left operand of its own comparison. A
    /// <em>text</em> column carries
    /// <see cref="SqlServerPredicateSql.ByteOrderCollation"/>, because the
    /// collation SQL Server ships by default is case-insensitive: a
    /// <c>[code] = 'delta'</c> that inherits it is <c>[code] = 'Delta'</c>, so
    /// a text identity under a case-folding collation is a case-insensitive
    /// identity — the store answers with a feature the contract distinguishes
    /// from the one it asked for, and an edit writes to that one too
    /// (ADR-0126). A column of any other kind carries no collation, because
    /// T-SQL will not apply a text collation to a number, a date or a guid.
    /// </summary>
    public static string Operand(IFeatureSchema schema, string column)
    {
        var index = schema.IndexOf(column);
        return index >= 0 && schema[index].Kind == AttributeKind.String
            ? $"{SqlServerIdentifier.Quote(column)} COLLATE {SqlServerPredicateSql.ByteOrderCollation}"
            : SqlServerIdentifier.Quote(column);
    }

    /// <summary>
    /// The identity columns as one bound predicate, numbering its parameters
    /// from <paramref name="from"/>. Every statement that names a feature does
    /// it through here — the lookup, the update's target, the delete's target
    /// and the attachment probe's — so none of them can answer for, or write
    /// to, a feature other than the one the identity named (ADR-0126).
    /// </summary>
    public static string Tuple(IFeatureSchema schema, IReadOnlyList<string> identityColumns, int from) =>
        string.Join(" AND ", identityColumns.Select((column, i) => $"{Operand(schema, column)} = @p{from + i}"));
}
