using System.Globalization;
using System.Text;
using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;

namespace Spatial.Querying;

/// <summary>
/// The page cursor: the opaque continuation token a store issues for a plan
/// (ADR-0074 §5). A cursor is not a client-authored offset — it is a store
/// answer, and a token that is malformed, or that was issued for a different
/// plan, is <c>invalid.arguments</c> rather than a silently different page.
///
/// <para>
/// The token carries the next page start and a fingerprint of the plan it was
/// issued for (its restrictions, projection, ordering and cap). The
/// fingerprint is what makes a cursor safe: continuing a plan with a
/// different <c>where</c>, a different order or a different projection is a
/// client error, and the store says so instead of returning a page of a
/// different question. The fingerprint is deliberately opaque to the client —
/// base64url of a version tag, the start and a hash — so the encoding is an
/// implementation detail a store may change without breaking a client.
/// </para>
/// </summary>
public static class FeaturePageCursor
{
    /// <summary>The sort key a store appends to any requested order, so the total order is deterministic.</summary>
    public const string IdentityTieBreak = "__feature_id";

    private const string Version = "v1";
    private const string Separator = ".";

    /// <summary>The page start a plan begins at: the cursor's start when one is present, else the offset.</summary>
    public static int StartOffset(FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Cursor is not { } cursor)
        {
            return query.Offset ?? 0;
        }

        return Decode(query, cursor);
    }

    /// <summary>Issues the token that continues a plan from the given page start.</summary>
    public static string Issue(FeatureQuery query, int start) =>
        $"{Version}{Separator}{start.ToString(CultureInfo.InvariantCulture)}{Separator}{Fingerprint(query)}";

    /// <summary>
    /// Reads the page start out of a token the store issued for this plan. A
    /// token that is not one this store could have issued for this plan is
    /// <c>invalid.arguments</c>.
    /// </summary>
    public static int Decode(FeatureQuery query, string cursor)
    {
        var parts = cursor.Split(Separator);
        if (parts.Length != 3
            || !string.Equals(parts[0], Version, StringComparison.Ordinal)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
            || start < 0)
        {
            throw Invalid(cursor);
        }

        var expected = Fingerprint(query);
        return string.Equals(parts[2], expected, StringComparison.Ordinal) ? start : throw Invalid(cursor);
    }

    private static SpatialException Invalid(string cursor) =>
        SpatialException.BadArguments(
            $"The pagination cursor is not one this query can continue from; start the query again without it: '{cursor}'.");

    /// <summary>
    /// A stable, short digest of everything about a plan that decides which
    /// page a cursor points into. Two plans with the same digest are the same
    /// question; the identity restriction and the box take part as well as the
    /// shaping, so a cursor cannot be carried from one restriction to another.
    /// </summary>
    public static string Fingerprint(FeatureQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = new StringBuilder()
            .Append(Ids(query.Ids))
            .Append('|').Append(Box(query.BoundingBox))
            .Append('|').Append(Fields(query.Projection))
            .Append('|').Append(Order(query.Order))
            .Append('|').Append(query.Limit?.ToString(CultureInfo.InvariantCulture) ?? "*")
            .ToString();
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    private static string Ids(IReadOnlyList<FeatureId>? ids) =>
        ids is null ? "*" : string.Join(",", ids.Select(id => id.Value));

    private static string Box(BoundingBox? box) =>
        box is null
            ? "*"
            : string.Join(
                ",",
                new[] { box.MinX, box.MinY, box.MaxX, box.MaxY }
                    .Select(value => value.ToString("R", CultureInfo.InvariantCulture)));

    private static string Fields(IReadOnlyList<string>? fields) => fields is null ? "*" : string.Join(",", fields);

    private static string Order(IReadOnlyList<OrderTerm>? order) =>
        order is null ? "*" : string.Join(",", order.Select(term => $"{term.Field}:{(term.IsDescending ? "desc" : "asc")}"));
}
