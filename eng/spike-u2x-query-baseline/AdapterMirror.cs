using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;
using Spatial.Querying;

namespace Spatial.Spike.QueryBaseline;

/// <summary>One matched feature with its Esri <c>OBJECTID</c> — the adapter's <c>MatchedFeature</c>.</summary>
internal readonly record struct Row(long ObjectId, Feature Feature);

/// <summary>One grouped statistics row: a group key, its row count and its average.</summary>
internal readonly record struct GroupRow(string Group, long Count, double Average);

/// <summary>
/// The layer facts the mirror needs, read off the dataset description once:
/// the field indexes, the geometry column index, and the object-id scheme
/// (<c>EsriObjectIdScheme.For</c> — a single integer identity column, else the
/// 1-based scan ordinal).
/// </summary>
internal sealed class LayerModel
{
    private LayerModel(FeatureSchema schema, int geometryIndex, int identityIndex, int populationIndex, int countryIndex)
    {
        Schema = schema;
        GeometryIndex = geometryIndex;
        IdentityIndex = identityIndex;
        PopulationIndex = populationIndex;
        CountryIndex = countryIndex;
    }

    internal FeatureSchema Schema { get; }

    internal int GeometryIndex { get; }

    internal int IdentityIndex { get; }

    internal int PopulationIndex { get; }

    internal int CountryIndex { get; }

    internal static LayerModel For(DatasetDescription dataset) =>
        new(
            dataset.Schema,
            IndexOfGeometry(dataset.Schema),
            IdentityIndexOf(dataset),
            dataset.Schema.IndexOf(QueryRequest.PopulationField),
            dataset.Schema.IndexOf(QueryRequest.CountryField));

    private static int IdentityIndexOf(DatasetDescription dataset)
    {
        if (dataset.IdColumns.Count == 1)
        {
            var index = dataset.Schema.IndexOf(dataset.IdColumns[0]);
            if (index >= 0 && dataset.Schema[index].Kind == AttributeKind.Int64)
            {
                return index;
            }
        }

        return -1;
    }

    private static int IndexOfGeometry(FeatureSchema schema)
    {
        for (var i = 0; i < schema.Count; i++)
        {
            if (schema[i].Kind == AttributeKind.Geometry)
            {
                return i;
            }
        }

        return -1;
    }

    internal long ResolveObjectId(Feature feature, long ordinal) =>
        IdentityIndex >= 0 ? feature[IdentityIndex].Int64Value : ordinal;
}

/// <summary>
/// A mirror of the in-adapter half of a Feature Service query, so the spike can
/// time the production path without the adapter's internals (they are
/// <c>internal</c>). It follows <c>FeatureSpatialMatcher.MatchAsync</c> (the
/// ordinal object-id scheme, the real predicate evaluator, the
/// envelope-intersects test), <c>FeatureOrdering.Apply</c>, the
/// <c>FeaturePaging</c> cap (<c>min(resultRecordCount, 1000)</c>) and
/// <c>FeatureStatisticsEngine</c>'s grouping. Fidelity is checked against the
/// real host by the <c>--host</c> mode.
/// </summary>
internal static class AdapterMirror
{
    private const int MaxRecordCount = 1000;

    internal static List<Row> Match(
        IReadOnlyList<FeatureBatch> batches,
        LayerModel model,
        Predicate? where,
        Envelope? queryEnvelope,
        CancellationToken cancellationToken)
    {
        var matches = new List<Row>();
        long ordinal = 0;
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            var objectId = model.ResolveObjectId(feature, ordinal);
            if (MatchesWhere(feature, where) && MatchesEnvelope(feature, model, queryEnvelope))
            {
                matches.Add(new Row(objectId, feature));
            }
        }

        return matches;
    }

    /// <summary>
    /// The attribute test, through <c>Spatial.Querying.ReferencePredicate</c>:
    /// the reference evaluator of the predicate vocabulary (ADR-0074 §4), which
    /// is the semantics every store's pushdown has to agree with. A clause
    /// naming the synthetic <c>OBJECTID</c> is not answerable here — the
    /// overlay the facade adds is internal — and the spike's own clauses name
    /// an attribute, never the id.
    /// </summary>
    internal static bool MatchesWhere(Feature feature, Predicate? where) =>
        where is null || ReferencePredicate.Matches(where, feature);

    private static bool MatchesEnvelope(Feature feature, LayerModel model, Envelope? queryEnvelope)
    {
        if (queryEnvelope is not { } query || model.GeometryIndex < 0)
        {
            return true;
        }

        if (feature[model.GeometryIndex].Kind != AttributeKind.Geometry)
        {
            return false;
        }

        return feature[model.GeometryIndex].GeometryValue.Envelope is { } envelope && envelope.Intersects(query);
    }

    /// <summary>The stable multi-key sort <c>FeatureOrdering.Apply</c> performs, on one key.</summary>
    internal static List<Row> Order(List<Row> rows, LayerModel model, string field, bool descending)
    {
        var index = model.Schema.IndexOf(field);
        var ordered = descending
            ? rows.OrderByDescending(row => row.Feature[index], AttributeOrder.Instance)
            : rows.OrderBy(row => row.Feature[index], AttributeOrder.Instance);
        return [.. ordered];
    }

    /// <summary>One page, under the same cap the adapter applies.</summary>
    internal static List<Row> Page(List<Row> rows, int offset, int requested)
    {
        var count = Math.Min(requested, MaxRecordCount);
        return [.. rows.Skip(Math.Min(offset, rows.Count)).Take(count)];
    }

    /// <summary>The grouped statistics the adapter computes over the matched set.</summary>
    internal static List<GroupRow> Statistics(List<Row> rows, LayerModel model, int groupIndex, int valueIndex)
    {
        var groups = new Dictionary<string, List<long>>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var key = row.Feature[groupIndex].StringValue;
            if (!groups.TryGetValue(key, out var values))
            {
                values = [];
                groups[key] = values;
            }

            values.Add(row.Feature[valueIndex].Int64Value);
        }

        return [.. groups.Select(group => new GroupRow(group.Key, group.Value.Count, group.Value.Average()))];
    }
}

/// <summary>
/// Attribute ordering, mirroring <c>FeatureOrdering.AttributeValueComparer</c>:
/// nulls last, then by kind, then by value within the kind.
/// </summary>
internal sealed class AttributeOrder : IComparer<AttributeValue>
{
    internal static readonly AttributeOrder Instance = new();

    public int Compare(AttributeValue left, AttributeValue right)
    {
        if (left.IsNull || right.IsNull)
        {
            return left.IsNull ? (right.IsNull ? 0 : 1) : -1;
        }

        return left.Kind != right.Kind
            ? left.Kind.CompareTo(right.Kind)
            : CompareSameKind(left, right);
    }

    private static int CompareSameKind(AttributeValue left, AttributeValue right) => left.Kind switch
    {
        AttributeKind.Int64 => left.Int64Value.CompareTo(right.Int64Value),
        AttributeKind.Double => left.DoubleValue.CompareTo(right.DoubleValue),
        AttributeKind.String => string.CompareOrdinal(left.StringValue, right.StringValue),
        _ => 0,
    };
}
