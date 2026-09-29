using Spatial.Contracts;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Core.Geometry;

namespace Spatial.Querying;

/// <summary>
/// The reference semantics of the reductions: count, distinct and grouped
/// aggregate (ADR-0074 §6). This is what a store's <c>COUNT</c>,
/// <c>DISTINCT</c> and <c>GROUP BY</c> are compared against, value for value,
/// so the rules that are easy to get subtly wrong are stated once here:
///
/// <list type="bullet">
/// <item>Nulls are skipped, not counted as zero: a count is the number of
/// non-null values, a sum ignores the nulls, and a statistic with no non-null
/// input is a <em>null</em> — never a zero.</item>
/// <item>A sum over an integer field is an integer sum; every other numeric
/// reduction is a double.</item>
/// <item>An ungrouped reduction of an empty set is one group of nulls, not zero
/// groups: the served statistics surface answers an empty statistics query
/// with one row of nulls, and a store that answered with nothing would be a
/// different answer, not a faster one.</item>
/// <item>Distinct rows keep first-seen order, and a group order is the order
/// the groups were first met — a store that sorts them differently has the
/// same set and a different answer, so the suite compares the sequence.</item>
/// <item>Variance and standard deviation are the sample forms (dividing by
/// n − 1), and are <em>null</em> for fewer than two values: the sample form of
/// a single observation is undefined, and a store's <c>VAR_SAMP</c> answers
/// null there too, so a zero would be the population form of a group the
/// sample form cannot describe.</item>
/// <item>The envelope of a geometry field is a rectangle, not a number
/// (ADR-0120): the smallest box over the group's non-null geometries, or a
/// null when the group has none — the same "no values to reduce" answer every
/// other statistic gives.</item>
/// </list>
/// </summary>
public static class FeatureReduction
{
    /// <summary>The number of features a plan selects.</summary>
    public static int CountFeatures(IReadOnlyList<Feature> selected)
    {
        ArgumentNullException.ThrowIfNull(selected);
        return selected.Count;
    }

    /// <summary>The deduplicated combinations of the requested fields, in first-seen order.</summary>
    public static DistinctPage Distinct(
        IFeatureSchema schema, IReadOnlyList<Feature> selected, DistinctQuery query)
    {
        ArgumentNullException.ThrowIfNull(selected);
        FeatureQueryValidation.ValidateDistinct(schema, query);

        var indexes = query.Fields.Select(schema.IndexOf).ToArray();
        var seen = new HashSet<IReadOnlyList<AttributeValue>>(AttributeRowComparer.Instance);
        var rows = new List<IReadOnlyList<AttributeValue>>();
        foreach (var feature in selected)
        {
            var row = new AttributeValue[indexes.Length];
            for (var i = 0; i < indexes.Length; i++)
            {
                row[i] = feature[indexes[i]];
            }

            if (seen.Add(row))
            {
                rows.Add(row);
            }
        }

        return new DistinctPage(query.Fields, rows, rows.Count);
    }

    /// <summary>The grouped reduction of the selected features.</summary>
    public static AggregatePage Aggregate(
        IFeatureSchema schema, IReadOnlyList<Feature> selected, AggregateQuery query)
    {
        ArgumentNullException.ThrowIfNull(selected);
        FeatureQueryValidation.ValidateAggregate(schema, query);

        var buckets = Group(selected, query.GroupBy?.Select(schema.IndexOf).ToArray() ?? []);
        var reduced = Buckets(buckets, query).Select(bucket => Reduce(schema, bucket, query.Specs)).ToArray();
        return new AggregatePage(
            query.GroupBy ?? (IReadOnlyList<string>)[],
            query.Specs.Select(spec => spec.Name).ToArray(),
            reduced,
            query.GroupBy is { Count: > 0 } ? reduced.Length : null);
    }

    /// <summary>
    /// The reduction of <em>no</em> rows, for an ungrouped request: one group
    /// of nulls, not zero groups (ADR-0098 §3). The reference gets this from
    /// <see cref="Aggregate"/>'s empty bucket, so a store that pushes the
    /// reduction into SQL and gets no row back composes this answer rather
    /// than reporting nothing — a pushed-down ungrouped reduction over an empty
    /// set that answered zero groups would be a different answer from the
    /// reference's, not a faster one.
    /// </summary>
    public static AggregateGroup EmptyGroup(IReadOnlyList<AggregateSpec> specs) =>
        new([], [.. specs.Select(_ => AttributeValue.Null)]);

    /// <summary>
    /// A count of <em>non-null</em> values, as a reduction over them reports it:
    /// zero non-null values is <em>no values to reduce</em>, which every other
    /// statistic answers as a null — a group whose every value is null has no
    /// count of them, and a store that pushed the reduction down will have been
    /// handed a dialect's zero for <c>COUNT(field)</c> and must translate it.
    /// </summary>
    public static AttributeValue Counted(long nonNullValues) =>
        nonNullValues == 0 ? AttributeValue.Null : AttributeValue.FromInt64(nonNullValues);

    /// <summary>
    /// The kind a reduced value has, so a caller can declare the result's
    /// type without re-deriving the rule: counts are integers, an integer
    /// field's sum is an integer, every other numeric reduction is a double,
    /// and an extreme takes the field's own kind.
    /// </summary>
    public static AttributeKind ResultKind(AggregateSpec spec, AttributeKind fieldKind) => spec.Statistic switch
    {
        AggregateStatistic.Count => AttributeKind.Int64,
        AggregateStatistic.Sum when fieldKind == AttributeKind.Int64 => AttributeKind.Int64,
        AggregateStatistic.Minimum or AggregateStatistic.Maximum => fieldKind,
        AggregateStatistic.Envelope => AttributeKind.Envelope,
        _ => AttributeKind.Double,
    };

    /// <summary>
    /// The buckets a reduction runs over: the met groups, or exactly one empty
    /// bucket when the reduction does not group — so an ungrouped reduction of
    /// an empty set is one group of nulls, the one row an empty statistics
    /// query answers with.
    /// </summary>
    private static List<GroupBucket> Buckets(List<GroupBucket> buckets, AggregateQuery query) =>
        buckets.Count == 0 && !query.IsGrouped ? [new GroupBucket([], [])] : buckets;

    /// <summary>Buckets the selected features by the group key, in first-seen order.</summary>
    private static List<GroupBucket> Group(IReadOnlyList<Feature> selected, int[] groupIndexes)
    {
        var buckets = new Dictionary<IReadOnlyList<AttributeValue>, GroupBucket>(AttributeRowComparer.Instance);
        var order = new List<GroupBucket>();
        foreach (var feature in selected)
        {
            var key = new AttributeValue[groupIndexes.Length];
            for (var i = 0; i < groupIndexes.Length; i++)
            {
                key[i] = feature[groupIndexes[i]];
            }

            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new GroupBucket(key, []);
                buckets[key] = bucket;
                order.Add(bucket);
            }

            bucket.Members.Add(feature);
        }

        return order;
    }

    private static AggregateGroup Reduce(IFeatureSchema schema, GroupBucket bucket, IReadOnlyList<AggregateSpec> specs)
    {
        var values = new AttributeValue[specs.Count];
        for (var i = 0; i < specs.Count; i++)
        {
            values[i] = Value(schema, bucket.Members, specs[i]);
        }

        return new AggregateGroup(bucket.Key, values);
    }

    private static AttributeValue Value(IFeatureSchema schema, List<Feature> members, AggregateSpec spec)
    {
        if (spec.IsRowCount)
        {
            return AttributeValue.FromInt64(members.Count);
        }

        var index = schema.IndexOf(spec.Field);
        var present = NonNull(members, index);
        if (present.Count == 0)
        {
            return AttributeValue.Null;
        }

        return spec.Statistic switch
        {
            AggregateStatistic.Count => AttributeValue.FromInt64(present.Count),
            AggregateStatistic.Minimum or AggregateStatistic.Maximum => Extreme(present, spec.Statistic),
            AggregateStatistic.Sum => Sum(schema[index].Kind, present),
            AggregateStatistic.Average => AttributeValue.FromDouble(present.Select(AsDouble).Average()),
            AggregateStatistic.Variance => Sample(present, out var variance) ? AttributeValue.FromDouble(variance) : AttributeValue.Null,
            AggregateStatistic.StdDev => Sample(present, out var deviation)
                ? AttributeValue.FromDouble(Math.Sqrt(deviation))
                : AttributeValue.Null,
            AggregateStatistic.PercentileContinuous => Percentile(present, spec, continuous: true),
            AggregateStatistic.PercentileDiscrete => Percentile(present, spec, continuous: false),
            AggregateStatistic.Envelope => BoundingRectangle(present),
            _ => AttributeValue.Null,
        };
    }

    /// <summary>
    /// The bounding rectangle of the group's non-null geometries, or the
    /// envelope of the first one when there is only one (ADR-0120). The nulls
    /// are already gone, so a group of nothing never reaches here: it is a
    /// null, like every other reduction of no non-null input.
    /// </summary>
    private static AttributeValue BoundingRectangle(List<AttributeValue> present)
    {
        var extent = present[0].GeometryValue.Envelope ?? Envelope.Empty;
        for (var i = 1; i < present.Count; i++)
        {
            extent = extent.Union(present[i].GeometryValue.Envelope ?? Envelope.Empty);
        }

        return AttributeValue.FromEnvelope(extent);
    }

    private static List<AttributeValue> NonNull(List<Feature> members, int index)
    {
        var present = new List<AttributeValue>();
        foreach (var member in members)
        {
            if (!member[index].IsNull)
            {
                present.Add(member[index]);
            }
        }

        return present;
    }

    /// <summary>An integer field's sum stays an integer; everything else reduces as a double.</summary>
    private static AttributeValue Sum(AttributeKind kind, List<AttributeValue> values) =>
        kind == AttributeKind.Int64 && values.All(value => value.Kind == AttributeKind.Int64)
            ? AttributeValue.FromInt64(values.Sum(value => value.Int64Value))
            : AttributeValue.FromDouble(values.Sum(AsDouble));

    private static AttributeValue Extreme(List<AttributeValue> values, AggregateStatistic statistic)
    {
        var best = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            var order = AttributeValueComparer.Instance.Compare(values[i], best);
            if ((statistic == AggregateStatistic.Minimum && order < 0) || (statistic == AggregateStatistic.Maximum && order > 0))
            {
                best = values[i];
            }
        }

        return best;
    }

    /// <summary>
    /// A percentile over the group's non-null numeric values, ranked in the
    /// requested order: continuous interpolates linearly at rank
    /// <c>f × (n − 1)</c>, discrete returns the dataset value at rank
    /// <c>ceil(f × n)</c>.
    /// </summary>
    private static AttributeValue Percentile(List<AttributeValue> values, AggregateSpec spec, bool continuous)
    {
        var numbers = values.Select(AsDouble).OrderBy(number => number).ToList();
        if (spec.PercentileDescending)
        {
            numbers.Reverse();
        }

        var fraction = spec.PercentileFraction;
        return AttributeValue.FromDouble(continuous ? Continuous(numbers, fraction) : Discrete(numbers, fraction));
    }

    private static double Continuous(List<double> sorted, double fraction)
    {
        var rank = fraction * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }

    private static double Discrete(List<double> sorted, double fraction) =>
        sorted[Math.Clamp((int)Math.Ceiling(fraction * sorted.Count) - 1, 0, sorted.Count - 1)];

    /// <summary>
    /// The sample variance of the group's non-null values, or <c>false</c> — a
    /// null result — for fewer than two of them: the sample form divides by
    /// n − 1, and one value has no such division. The standard deviation is its
    /// square root, so it is undefined on exactly the same groups.
    /// </summary>
    private static bool Sample(List<AttributeValue> values, out double result)
    {
        if (values.Count < 2)
        {
            result = 0;
            return false;
        }

        var numbers = values.Select(AsDouble).ToArray();
        var mean = numbers.Average();
        result = numbers.Sum(number => (number - mean) * (number - mean)) / (numbers.Length - 1);
        return true;
    }

    private static double AsDouble(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Int64 => value.Int64Value,
        AttributeKind.Double => value.DoubleValue,
        _ => throw SpatialException.BadArguments($"Cannot aggregate a non-numeric value of kind {value.Kind}."),
    };

    private sealed class GroupBucket(IReadOnlyList<AttributeValue> key, List<Feature> members)
    {
        public IReadOnlyList<AttributeValue> Key { get; } = key;

        public List<Feature> Members { get; } = members;
    }
}
