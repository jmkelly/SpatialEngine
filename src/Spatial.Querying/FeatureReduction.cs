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
/// same set and a different answer, so the suite compares the sequence. Both
/// are the plan's order where the plan states one it can be applied to: the
/// groups when every one of its terms names a group field, and the distinct
/// rows when its terms name the requested fields and cover them between them
/// (ADR-0115 §4, ADR-0133 §6) — the orders a <c>GROUP BY</c> and a
/// <c>DISTINCT</c> can be told to return. An order naming anything the group or
/// the distinct row does not carry is not an order of that kind at all, and the
/// first-seen order stands.</item>
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

    /// <summary>
    /// The deduplicated combinations of the requested fields.
    /// </summary>
    /// <param name="schema">The dataset's schema, which the requested fields resolve against.</param>
    /// <param name="selected">The features the plan selects, in the order it selected them.</param>
    /// <param name="query">The fields to deduplicate combinations of.</param>
    /// <param name="order">
    /// The <em>plan's</em> order, or <c>null</c> when the plan asked for none.
    /// It is applied when every term names a requested field <em>and</em> the
    /// terms between them cover every requested field: then the order is total
    /// over the distinct rows — they are unique per combination — and the rows
    /// are reported in it, which is the order a <c>DISTINCT</c> can be told to
    /// return (ADR-0133 §6, the same rule
    /// <see cref="Aggregate"/> applies to the groups). Any other plan leaves
    /// the rows in the order they were first seen, which is the store's own row
    /// order and the only one it states.
    /// </param>
    public static DistinctPage Distinct(
        IFeatureSchema schema, IReadOnlyList<Feature> selected, DistinctQuery query, IReadOnlyList<OrderTerm>? order = null)
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

        return new DistinctPage(query.Fields, DistinctOrder(query, order, rows), rows.Count);
    }

    /// <summary>
    /// The distinct rows in the plan's order when that order is total over them,
    /// and in first-seen order otherwise (ADR-0133 §6).
    /// </summary>
    private static List<IReadOnlyList<AttributeValue>> DistinctOrder(
        DistinctQuery query, IReadOnlyList<OrderTerm>? order, List<IReadOnlyList<AttributeValue>> rows)
    {
        if (order is not { Count: > 0 } terms)
        {
            return rows;
        }

        var keys = new List<SortKey>(terms.Count);
        foreach (var term in terms)
        {
            var index = query.Fields.ToList().FindIndex(field => string.Equals(field, term.Field, StringComparison.Ordinal));
            if (index < 0)
            {
                return rows;
            }

            keys.Add(new SortKey(index, term));
        }

        // A distinct row is unique per combination of the requested fields, so
        // an order that covers them all is a total order over the rows and two
        // callers cannot both be right about which of two ties comes first.
        if (keys.Select(key => key.Index).Distinct().Count() != query.Fields.Count)
        {
            return rows;
        }

        return
        [
            .. rows.OrderBy(
                row => row,
                Comparer<IReadOnlyList<AttributeValue>>.Create((left, right) => CompareRows(left, right, keys)))
        ];
    }

    /// <summary>The comparison of two distinct rows by the plan's terms, under the reference's value ordering.</summary>
    private static int CompareRows(
        IReadOnlyList<AttributeValue> left, IReadOnlyList<AttributeValue> right, List<SortKey> keys)
    {
        foreach (var key in keys)
        {
            var comparison = AttributeValueComparer.Instance.Compare(left[key.Index], right[key.Index]);
            if (comparison != 0)
            {
                return key.Term.IsDescending ? -comparison : comparison;
            }
        }

        return 0;
    }

    /// <summary>
    /// The grouped reduction of the selected features.
    /// </summary>
    /// <param name="schema">The dataset's schema, which the group key and the clause resolve against.</param>
    /// <param name="selected">The features the plan selects, in the order it selected them.</param>
    /// <param name="query">The reduction: the statistics, the grouping, the clause and the group page.</param>
    /// <param name="order">
    /// The <em>plan's</em> order, or <c>null</c> when the plan asked for none.
    /// It is applied when every term names a group field, because then the group
    /// key is a total order over the groups and this reduction can return the
    /// order a <c>GROUP BY</c> returns (ADR-0098 §3, ADR-0115 §4); a term the
    /// group row does not carry is not a group order at all, and the groups keep
    /// the first-seen order the scan gave them.
    /// </param>
    public static AggregatePage Aggregate(
        IFeatureSchema schema,
        IReadOnlyList<Feature> selected,
        AggregateQuery query,
        IReadOnlyList<OrderTerm>? order = null)
    {
        ArgumentNullException.ThrowIfNull(selected);
        FeatureQueryValidation.ValidateAggregate(schema, query);

        var buckets = Group(selected, query.GroupBy?.Select(schema.IndexOf).ToArray() ?? []);
        var reduced = Buckets(buckets, query)
            .Select(bucket => Reduce(schema, bucket, query.Specs))
            .ToArray();
        // The `having` clause and the page are two steps in that order, and both
        // are over the *group row* (ADR-0128): the cap cuts the groups the
        // clause kept, never the rows the groups were reduced from, which is
        // why neither is a member of the plan.
        var kept = Order(schema, query, reduced, order);
        kept = query.Having is { } having
            ? kept.Where(group => MatchesGroup(schema, query, having, group)).ToArray()
            : kept;
        var offset = Math.Min(query.Offset ?? 0, kept.Length);
        var page = query.Limit is { } cap ? kept.Skip(offset).Take(cap).ToArray() : kept.Skip(offset).ToArray();
        return new AggregatePage(
            query.GroupBy ?? (IReadOnlyList<string>)[],
            query.Specs.Select(spec => spec.Name).ToArray(),
            page,
            query.IsGrouped ? kept.Length : null,
            offset + page.Length < kept.Length);
    }

    /// <summary>
    /// The reduced groups in the order they are reported: the plan's order when
    /// every one of its terms names a group field, and the first-seen order
    /// otherwise. The clause and the page are applied after this, so a cap cuts
    /// the ordered group set — which is what a pushed <c>GROUP BY</c> with a
    /// <c>LIMIT</c> does, and what makes a store that reduces in managed code
    /// answer the same sequence as one that pushed the order down.
    /// </summary>
    private static AggregateGroup[] Order(
        IFeatureSchema schema, AggregateQuery query, AggregateGroup[] groups, IReadOnlyList<OrderTerm>? order)
    {
        if (order is not { Count: > 0 } terms || query.GroupBy is not { Count: > 0 } groupBy)
        {
            return groups;
        }

        var keys = new List<SortKey>(terms.Count);
        foreach (var term in terms)
        {
            var index = groupBy.ToList().FindIndex(field => string.Equals(field, term.Field, StringComparison.Ordinal));
            if (index < 0)
            {
                return groups;
            }

            keys.Add(new SortKey(index, term));
        }

        var ordered = groups.OrderBy(group => group, Comparer<AggregateGroup>.Create((left, right) => Compare(left, right, keys)));
        return [.. ordered];
    }

    /// <summary>The comparison of two group rows by the plan's terms, under the reference's value ordering.</summary>
    private static int Compare(AggregateGroup left, AggregateGroup right, List<SortKey> keys)
    {
        foreach (var key in keys)
        {
            var comparison = AttributeValueComparer.Instance.Compare(left.Key[key.Index], right.Key[key.Index]);
            if (comparison != 0)
            {
                return key.Term.IsDescending ? -comparison : comparison;
            }
        }

        return 0;
    }

    private readonly record struct SortKey(int Index, OrderTerm Term);

    /// <summary>
    /// Whether the reduced group satisfies a <c>having</c> clause. The group row
    /// is the key's values followed by one value per statistic, under the name
    /// each result is reported under, and it is answered by
    /// <see cref="ReferencePredicate"/> over a feature built from that row — the
    /// one predicate evaluation in the engine, so a clause the SQL providers
    /// push into a <c>HAVING</c> is measured against exactly these semantics.
    /// </summary>
    private static bool MatchesGroup(
        IFeatureSchema schema, AggregateQuery query, Predicate having, AggregateGroup group)
    {
        var fields = new List<FieldDefinition>(query.GroupRowFields.Count);
        var values = new AttributeValue[group.Key.Count + query.Specs.Count];
        for (var i = 0; i < query.GroupBy?.Count; i++)
        {
            var key = schema[schema.IndexOf(query.GroupBy![i])];
            fields.Add(new FieldDefinition(key.Name, ComparesAs(key.Kind), nullable: true));
            values[i] = group.Key[i];
        }

        for (var i = 0; i < query.Specs.Count; i++)
        {
            var spec = query.Specs[i];
            fields.Add(new FieldDefinition(spec.Name, ComparesAs(ResultKind(spec, FieldKind(schema, spec))), nullable: true));
            values[group.Key.Count + i] = group.Values[i];
        }

        return ReferencePredicate.Matches(having, new Feature(new FeatureId("group"), new FeatureSchema(fields), values));
    }

    /// <summary>The schema field a statistic reduces, for the result kind the reference reports it as.</summary>
    private static AttributeKind FieldKind(IFeatureSchema schema, AggregateSpec spec) =>
        spec.IsRowCount || schema.IndexOf(spec.Field) < 0 ? AttributeKind.Int64 : schema[schema.IndexOf(spec.Field)].Kind;

    /// <summary>
    /// The kind a reduced value is <em>compared</em> as: a value with nothing
    /// to reduce is a null, and a null has no kind to compare under, so it is
    /// read as the numeric the statistic would have produced. A comparison
    /// against it is false either way — a null satisfies nothing.
    /// </summary>
    private static AttributeKind ComparesAs(AttributeKind kind) => kind == AttributeKind.Null ? AttributeKind.Double : kind;

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
            AggregateStatistic.Envelope => BoundingRectangle(present),
            _ => NumericValue(present, spec),
        };
    }

    private static AttributeValue NumericValue(List<AttributeValue> present, AggregateSpec spec) =>
        spec.Statistic switch
        {
            AggregateStatistic.Average or AggregateStatistic.Variance or AggregateStatistic.StdDev =>
                MomentValue(present, spec),
            _ => PercentileValue(present, spec),
        };

    private static AttributeValue MomentValue(List<AttributeValue> present, AggregateSpec spec) =>
        spec.Statistic switch
        {
            AggregateStatistic.Average => AttributeValue.FromDouble(present.Select(AsDouble).Average()),
            AggregateStatistic.Variance => Sample(present, out var variance) ? AttributeValue.FromDouble(variance) : AttributeValue.Null,
            AggregateStatistic.StdDev => Sample(present, out var deviation)
                ? AttributeValue.FromDouble(Math.Sqrt(deviation))
                : AttributeValue.Null,
            _ => AttributeValue.Null,
        };

    private static AttributeValue PercentileValue(List<AttributeValue> present, AggregateSpec spec) =>
        spec.Statistic switch
        {
            AggregateStatistic.PercentileContinuous => Percentile(present, spec, continuous: true),
            AggregateStatistic.PercentileDiscrete => Percentile(present, spec, continuous: false),
            _ => AttributeValue.Null,
        };

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
