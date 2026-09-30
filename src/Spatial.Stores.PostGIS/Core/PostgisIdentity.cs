using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;

namespace Spatial.Stores.PostGIS.Core;

/// <summary>
/// The identity of a feature in a PostGIS dataset: the dataset's id columns,
/// their attribute kinds, the bind parameters that select a set of features by
/// identity (ADR-0037), and the comparison those parameters are made in
/// (ADR-0126). Identity order is the id-column order, so one parameter tuple
/// per requested <see cref="FeatureId"/>.
/// </summary>
internal static class PostgisIdentity
{
    /// <summary>The attribute kind of each identity column, in id-column order.</summary>
    internal static AttributeKind[] Kinds(DatasetDescription description) =>
        description.IdColumns
            .Select(column => description.Schema[description.Schema.IndexOf(column)].Kind)
            .ToArray();

    /// <summary>The bound parameter tuple selecting one feature by identity.</summary>
    internal static object?[] Values(DatasetDescription description, FeatureId id) =>
        PostgisDiagnostics.ParseFeatureIdentity(Kinds(description), id);

    /// <summary>One bound parameter tuple per requested id, flattened for the statement's parameter list.</summary>
    internal static List<object?> Parameters(DatasetDescription description, IReadOnlyList<FeatureId> ids)
    {
        var kinds = Kinds(description);
        var parameters = new List<object?>(ids.Count * kinds.Length);
        foreach (var id in ids)
        {
            parameters.AddRange(PostgisDiagnostics.ParseFeatureIdentity(kinds, id));
        }

        return parameters;
    }

    /// <summary>
    /// Whether this dataset's identity includes a <em>text</em> column, which is
    /// the only part of an identity comparison a collation can change — and so
    /// the only reason to pay for the catalog read that decides whether the
    /// comparison states its order (ADR-0126, as ADR-0123 for the attribute
    /// half of the same <c>WHERE</c>).
    /// </summary>
    internal static bool ComparesText(DatasetDescription description) =>
        description.IdColumns.Any(column => IsText(description.Schema, column));

    /// <summary>
    /// One identity column as the left operand of its own comparison: a
    /// <em>text</em> column carries <c>COLLATE "C"</c> unless this database
    /// already compares text by bytes, because a text identity under a
    /// case-folding collation is a case-insensitive identity — the store would
    /// answer with a feature the contract distinguishes from the one it asked
    /// for, and an edit would write to that one too (ADR-0126). A column of any
    /// other kind carries no collation: <c>COLLATE</c> is a string operator.
    /// </summary>
    internal static string Operand(IFeatureSchema schema, string column, bool byteOrderText) =>
        PostgisPlanQueries.Ordered(column, schema, byteOrderText);

    /// <summary>
    /// The identity columns as one bound predicate, numbering its parameters
    /// from <paramref name="from"/>. Every statement that names a feature does
    /// it through here — the pushed restriction, the lookup, the update's
    /// target, the delete's target and the attachment probe's — so none of them
    /// can answer for, or write to, a feature other than the one the identity
    /// named (ADR-0126).
    /// </summary>
    internal static string Tuple(
        FeatureSchema schema, IReadOnlyList<string> identityColumns, int from, bool byteOrderText) =>
        string.Join(" AND ", identityColumns.Select(
            (column, i) => $"{Operand(schema, column, byteOrderText)} = @p{from + i}"));

    private static bool IsText(FeatureSchema schema, string column)
    {
        var index = schema.IndexOf(column);
        return index >= 0 && schema[index].Kind == AttributeKind.String;
    }
}
