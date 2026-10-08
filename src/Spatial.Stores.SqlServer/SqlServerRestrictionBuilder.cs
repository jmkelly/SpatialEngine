using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Stores.SqlServer.Core;

namespace Spatial.Stores.SqlServer;

/// <summary>
/// The restriction a plan pushes into the <c>WHERE</c>: the identity
/// restriction together with the bounding-box pre-filter and the attribute
/// predicate, all as bound values over discovered identifiers (ADR-0038).
/// One static home for the three fragments every pushed read and reduction
/// compiles, so a plan and a filter cannot drift into two different answers.
/// </summary>
internal static class SqlServerRestrictionBuilder
{
    /// <summary>
    /// The restriction a plan pushes into the <c>WHERE</c>: the identity
    /// restriction, the bounding-box pre-filter and the attribute predicate,
    /// all as bound values over discovered identifiers, compiled by the same
    /// <see cref="SqlServerPredicateSql"/> the scan path uses so a plan and a
    /// filter cannot drift into two different answers.
    /// </summary>
    internal static string? Predicate(DatasetDescription description, FeatureQuery query, List<object?> parameters)
    {
        var identity = Identity(description, query.Ids, parameters);
        var restriction = SqlServerPredicateSql.Build(description, query.BoundingBox, query.Where, parameters);
        return (identity, restriction) switch
        {
            (null, null) => null,
            (null, _) => restriction,
            (_, null) => identity,
            _ => $"({identity}) AND ({restriction})",
        };
    }

    /// <summary>
    /// The identity restriction as one OR-group per requested tuple, one bound
    /// parameter per identity value, in input order (ADR-0038). An empty set of
    /// ids selects nothing, and a table with no identity column cannot be
    /// restricted by identity at all — which cannot arise here, because a plan
    /// this reader takes has an identity tie-break to make its order total.
    /// </summary>
    internal static string? Identity(
        DatasetDescription description, IReadOnlyList<FeatureId>? ids, List<object?> parameters)
    {
        if (ids is null || description.IdColumns.Count == 0)
        {
            return null;
        }

        if (ids.Count == 0)
        {
            return "(1 = 0)";
        }

        var groups = ids
            .Select(id => string.Join(
                " AND ",
                description.IdColumns.Select((column, position) =>
                    $"{SqlServerIdentifier.Quote(column)} = {Parameter(parameters, SqlServerIdentity.Values(description, id)[position])}")))
            .ToArray();
        return groups.Length == 1 ? groups[0] : "(" + string.Join(" OR ", groups) + ")";
    }

    private static string Parameter(List<object?> parameters, object? value)
    {
        parameters.Add(value);
        return $"@p{parameters.Count - 1}";
    }
}
