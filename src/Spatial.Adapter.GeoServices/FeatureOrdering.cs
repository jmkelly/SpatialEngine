using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// Ordering a query's matched features: validating and compiling
/// <c>orderByFields</c>, then applying it as a stable multi-key sort. Split
/// out of <see cref="FeatureQueryEngine"/> so the query facade keeps only
/// orchestration and the ordering fan-out lives with the code that uses it
/// (ADR-0040).
/// </summary>
internal static class FeatureOrdering
{
    /// <summary>
    /// Validates <c>orderByFields</c> against the dataset schema and compiles
    /// each entry to a key plus direction. <c>OBJECTID</c> is accepted even
    /// though it is synthetic, because it is the layer's advertised object-id
    /// field and clients (QGIS) order by it for a stable paged sequence.
    /// Unknown fields and geometry fields are typed invalid-argument failures
    /// (HTTP 400).
    /// </summary>
    internal static OrderKey[]? Compile(DatasetDescription dataset, EsriFeatureQuery query)
    {
        if (query.OrderByFields is not { Count: > 0 } fields)
        {
            return null;
        }

        var keys = new OrderKey[fields.Count];
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            if (string.Equals(field.Name, EsriLayerModel.ObjectIdField, StringComparison.OrdinalIgnoreCase))
            {
                keys[i] = new OrderKey(-1, field.Descending, ObjectId: true);
                continue;
            }

            var index = dataset.Schema.IndexOf(field.Name);
            if (index < 0)
            {
                throw GeoServicesErrors.Invalid(
                    $"'orderByFields' names unknown field '{field.Name}' in layer '{dataset.Id}'.");
            }

            if (dataset.Schema[index].Kind == AttributeKind.Geometry)
            {
                throw GeoServicesErrors.Invalid(
                    $"'orderByFields' cannot order by geometry field '{field.Name}' in layer '{dataset.Id}'.");
            }

            keys[i] = new OrderKey(index, field.Descending);
        }

        return keys;
    }

    /// <summary>
    /// Applies the compiled ordering to the matched features before paging.
    /// LINQ's ordering is a stable sort, so equal keys keep their scan order.
    /// </summary>
    internal static List<MatchedFeature> Apply(List<MatchedFeature> matches, OrderKey[]? keys)
    {
        if (keys is null)
        {
            return matches;
        }

        IOrderedEnumerable<MatchedFeature>? ordered = null;
        foreach (var key in keys)
        {
            Func<MatchedFeature, AttributeValue> selector = key.ObjectId
                ? match => AttributeValue.FromInt64(match.ObjectId)
                : match => match.Feature[key.Index];
            ordered = ordered is null
                ? Order(matches, selector, key.Descending)
                : ThenOrder(ordered, selector, key.Descending);
        }

        return ordered!.ToList();
    }

    private static IOrderedEnumerable<MatchedFeature> Order(
        IEnumerable<MatchedFeature> matches, Func<MatchedFeature, AttributeValue> selector, bool descending) =>
        descending
            ? matches.OrderByDescending(selector, AttributeValueComparer.Instance)
            : matches.OrderBy(selector, AttributeValueComparer.Instance);

    private static IOrderedEnumerable<MatchedFeature> ThenOrder(
        IOrderedEnumerable<MatchedFeature> ordered, Func<MatchedFeature, AttributeValue> selector, bool descending) =>
        descending
            ? ordered.ThenByDescending(selector, AttributeValueComparer.Instance)
            : ordered.ThenBy(selector, AttributeValueComparer.Instance);

    /// <summary>One compiled <c>orderByFields</c> key: a schema index and direction, or the synthetic object id.</summary>
    internal sealed record OrderKey(int Index, bool Descending, bool ObjectId = false);

    /// <summary>
    /// Orders attribute values of the kinds a schema can declare. Nulls sort
    /// last in ascending order (and therefore first when the key is reversed
    /// for DESC), matching the conventional SQL default. Non-null values of a
    /// key always share one kind because the dataset schema fixes the column
    /// kind; a defensive fallback compares the kinds when they do not.
    /// </summary>
    internal sealed class AttributeValueComparer : IComparer<AttributeValue>
    {
        public static readonly AttributeValueComparer Instance = new();

        public int Compare(AttributeValue left, AttributeValue right)
        {
            if (left.IsNull || right.IsNull)
            {
                return CompareNulls(left, right);
            }

            return CompareValues(left, right);
        }

        private static int CompareNulls(AttributeValue left, AttributeValue right) =>
            left.IsNull ? (right.IsNull ? 0 : 1) : -1;

        private static int CompareValues(AttributeValue left, AttributeValue right)
        {
            if (left.Kind != right.Kind)
            {
                return left.Kind.CompareTo(right.Kind);
            }

            return CompareSameKind(left, right);
        }

        private static int CompareSameKind(AttributeValue left, AttributeValue right) =>
            KindComparers.TryGetValue(left.Kind, out var compare) ? compare(left, right) : 0;

        private static readonly Dictionary<AttributeKind, Func<AttributeValue, AttributeValue, int>> KindComparers = new()
        {
            [AttributeKind.Boolean] = (left, right) => left.BooleanValue.CompareTo(right.BooleanValue),
            [AttributeKind.Int64] = (left, right) => left.Int64Value.CompareTo(right.Int64Value),
            [AttributeKind.Double] = (left, right) => left.DoubleValue.CompareTo(right.DoubleValue),
            [AttributeKind.String] = (left, right) => string.CompareOrdinal(left.StringValue, right.StringValue),
            [AttributeKind.DateTimeOffset] = (left, right) => left.DateTimeOffsetValue.UtcTicks.CompareTo(right.DateTimeOffsetValue.UtcTicks),
            [AttributeKind.Guid] = (left, right) => left.GuidValue.CompareTo(right.GuidValue),
        };
    }

    /// <summary>Structural equality for projected distinct-value rows.</summary>
    internal sealed class AttributeRowComparer : IEqualityComparer<AttributeValue[]>
    {
        public static AttributeRowComparer Instance { get; } = new();

        public bool Equals(AttributeValue[]? left, AttributeValue[]? right) =>
            ReferenceEquals(left, right)
            || (left is not null && right is not null && left.AsSpan().SequenceEqual(right));

        public int GetHashCode(AttributeValue[] row)
        {
            var hash = new HashCode();
            foreach (var value in row)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }
}
