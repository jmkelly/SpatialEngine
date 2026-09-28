using System.Globalization;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Builds the key equality clauses a relationship traversal filters the
/// related layer with (ADR-0077). The engine holds no join operator, so the
/// declaration's two columns become ordinary terms of the same closed
/// where-grammar the query path already uses: the traversal never invents a
/// filter language and never forwards client text as structure, it renders
/// key values it read from stored features and parses the result with the
/// engine's own parser. Key kinds are validated to match at declaration time
/// (ADR-0077 §2), so an equality is only ever built from a comparable pair.
/// </summary>
internal static class FeatureRelationshipKeys
{
    /// <summary>
    /// Renders one <c>column = value</c> term from a stored key attribute, or
    /// null when the key value is null: a record whose key is null relates to
    /// nothing, which is not the same claim as "relates to every record".
    /// </summary>
    public static string? Equality(string column, AttributeValue value)
    {
        if (value.IsNull)
        {
            return null;
        }

        return $"{column} = {Literal(value)}";
    }

    /// <summary>
    /// Renders a value as a where-grammar literal: invariant numbers,
    /// single-quoted text (embedded quotes doubled) and the
    /// <c>TRUE</c>/<c>FALSE</c> keywords. Kinds with no literal form —
    /// geometry and dates — are a typed <c>invalid.arguments</c> naming them,
    /// which the declaration-time validation already excludes.
    /// </summary>
    internal static string Literal(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Int64 => value.Int64Value.ToString(CultureInfo.InvariantCulture),
        AttributeKind.Double => value.DoubleValue.ToString("R", CultureInfo.InvariantCulture),
        AttributeKind.String => Quote(value.StringValue ?? string.Empty),
        AttributeKind.Guid => Quote(value.GuidValue.ToString("D")),
        AttributeKind.Boolean => value.BooleanValue ? "TRUE" : "FALSE",
        _ => throw GeoServicesErrors.Invalid(
            $"A relationship key of kind {value.Kind} has no equality literal; key columns must be scalar."),
    };

    /// <summary>Conjoins rendered terms with <c>AND</c>, the composite-key case.</summary>
    public static string Conjoin(params string?[] terms) =>
        string.Join(" AND ", terms.Where(term => term is not null).Select(term => term!));

    /// <summary>
    /// Disjoins rendered terms with <c>OR</c> — the many-to-many case, where
    /// the join rows name the keys the related layer is filtered by. An empty
    /// set is the constant false term, so "no join rows" answers no related
    /// records instead of every record.
    /// </summary>
    public static string Disjoin(IEnumerable<string?> terms)
    {
        var rendered = terms.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => $"({term})").ToArray();
        return rendered.Length switch
        {
            0 => "1 = 0",
            1 => rendered[0],
            _ => string.Join(" OR ", rendered),
        };
    }

    /// <summary>
    /// Parses a rendered term into the engine's where-grammar. A clause that
    /// does not parse is a typed <c>invalid.arguments</c>: the text came from
    /// a stored key value or a validated column name, so a parse failure is a
    /// key value the grammar cannot express, not a client error.
    /// </summary>
    public static EsriFilterClause Parse(string text)
    {
        if (!EsriFilterClause.TryParse(text, out var clause, out var error) || clause is null)
        {
            throw GeoServicesErrors.Invalid($"The relationship key term '{text}' is not a valid where clause: {error}.");
        }

        return clause;
    }

    private static string Quote(string text) => $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";
}
