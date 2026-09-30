using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Esri.Codec;
using Spatial.Querying;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The <c>outStatistics</c> pipeline (spec §9.1.4.3): grouping, aggregation,
/// <c>having</c>, statistic ordering, paging and the statistics JSON.
/// Split out of <see cref="FeatureQueryEngine"/> so the query facade keeps
/// only orchestration and the statistics fan-out (schema, filter, codecs)
/// lives with the code that uses it (ADR-0040).
///
/// <para>
/// The fan-out is compiled once, into a <see cref="StatisticsSpec"/>, and the
/// rows can then come from either place: the matched feature set, or the
/// reduction a store returned when the plan carried the whole match
/// (ADR-0098 §7). The JSON is the same writer either way, which is what keeps
/// a pushed-down answer byte-identical to the in-memory one. What happens
/// <em>after</em> the rows is where the two paths part: on the match path the
/// writer applies the <c>having</c> clause, the statistic order and the page
/// over the groups, and on the pushed path all three were the store's to answer
/// — the clause and the page ride on the reduction, in that order, because a
/// cap cut before the filter would be a page of the wrong question (ADR-0128).
/// </para>
/// </summary>
internal static class FeatureStatisticsEngine
{
    /// <summary>The result name the row-count probe is asked under, when the caller needs to know whether the match set was empty.</summary>
    private const string RowCountProbe = "__spatial_match_rows";

    /// <summary>
    /// The <c>outStatistics</c> response (10.x): aggregations over the matched
    /// set, optionally grouped with a <c>having</c> filter on the groups.
    /// Percentile statistics (S3 <c>percentile_cont</c>/<c>percentile_disc</c>)
    /// aggregate the same way but never combine with <c>having</c>.
    /// Shape per Koop: <c>{displayFieldName, fields, features: [{attributes}]}</c>
    /// with no geometry. A statistics query over an empty set with no grouping
    /// yields one row of nulls (Esri response example 5).
    /// </summary>
    internal static IResult Statistics(
        DatasetDescription dataset,
        IReadOnlyList<MatchedFeature> matches,
        EsriFeatureQuery query)
    {
        var spec = Compile(dataset, query);
        var groups = GroupMatches(matches, spec.GroupFields);
        var rows = new List<StatisticRow>();
        if (groups.Count == 0 && spec.GroupFields.Count == 0)
        {
            rows.Add(NullRow(spec));
        }
        else
        {
            foreach (var group in groups)
            {
                rows.Add(ComputeRow(spec, group.Key, group.Value));
            }
        }

        return Write(spec, query, rows);
    }

    /// <summary>
    /// The response from the reduction a store returned: the groups the store
    /// answered with — the page of them, when the request asked for one — become
    /// the rows the writer already knew how to shape. Nothing is filtered and
    /// nothing is skipped here: the <c>having</c> clause and the page were the
    /// store's to answer (ADR-0128), so the rows it returned are the rows this
    /// response is, and its own "one more group" answer is the
    /// <c>exceededTransferLimit</c> flag.
    ///
    /// <para>
    /// The one thing the store's numbers do not carry is whether the match set
    /// was empty, and the served surface does: an ungrouped statistics query
    /// over no rows answers with <em>one row of nulls</em> (Esri response
    /// example 5), which is not the same as a reduction over zero rows — a
    /// row count is zero there, not null. So an ungrouped request asks the
    /// store for the row count alongside the statistics and writes the null row
    /// when it comes back zero, exactly as the in-memory path does.
    /// </para>
    /// </summary>
    internal static IResult StatisticsFromGroups(StatisticsSpec spec, EsriFeatureQuery query, AggregatePage page)
    {
        var rows = new List<StatisticRow>(page.Groups.Count);
        foreach (var group in page.Groups)
        {
            rows.Add(spec.GroupFields.Count == 0 && IsEmptyMatch(spec, group)
                ? NullRow(spec)
                : new StatisticRow([.. group.Key], spec.Values(group), spec.Kinds));
        }

        // A grouped reduction of an empty set is no groups, as it always was.
        return WriteStatistics(new StatisticsPage(
            spec,
            rows,
            page.HasMore,
            page.HasMore ? ResultPagination.Encode(FeaturePaging.ResolveOffset(query) + rows.Count) : null));
    }

    /// <summary>
    /// The request as the store's own reduction surface takes it: the statistics
    /// as an <see cref="AggregateQuery"/> (the two vocabularies are
    /// name-for-name), the group fields as the grouping, the <c>having</c>
    /// clause as a predicate over the group row, the page as the reduction's
    /// own cap and start, and — ungrouped only — the row count the empty-set
    /// rule above is decided by.
    ///
    /// <para>
    /// The cap and the clause go on the <em>reduction</em> and not on the plan:
    /// a plan's cap would cut rows the store never grouped, and the two steps
    /// have an order — the clause chooses which groups exist, and only then is
    /// the page cut — that a plan's cap could not express at all
    /// (ADR-0098 §7 as amended by SpatialEngine-u2x.9.2, ADR-0128).
    /// </para>
    /// </summary>
    internal static AggregateQuery Reduction(StatisticsSpec spec, EsriFeatureQuery query)
    {
        var page = new AggregateQuery(
            spec.Aggregate.Specs,
            spec.Aggregate.GroupBy,
            spec.Having,
            FeaturePaging.EffectivePageSize(query),
            FeaturePaging.ResolveOffset(query));
        return spec.GroupFields.Count == 0
            ? page with { Specs = [.. page.Specs, RowCount(spec)] }
            : page;
    }

    /// <summary>
    /// The row-count probe, under a result name no requested statistic uses, so
    /// the extra statistic can never collide with one the response reports.
    /// </summary>
    private static AggregateSpec RowCount(StatisticsSpec spec)
    {
        var name = RowCountProbe;
        while (spec.Statistics.Any(statistic => string.Equals(statistic.OutStatisticFieldName, name, StringComparison.OrdinalIgnoreCase)))
        {
            name += "_";
        }

        return new AggregateSpec(AggregateStatistic.Count, AggregateSpec.AllFields, name);
    }

    /// <summary>
    /// Everything after the rows on the <em>match</em> path: <c>having</c>, the
    /// statistic order, the group page and the JSON. The pushed path does none
    /// of it — the store answered the clause and the page, and its page is
    /// written as it came back — so what is shared is the JSON, which is what
    /// keeps a pushed-down answer byte-identical to the in-memory one.
    /// </summary>
    private static IResult Write(StatisticsSpec spec, EsriFeatureQuery query, List<StatisticRow> rows)
    {
        if (query.Having is { } having)
        {
            rows = rows.Where(row => HavingMatches(row, spec, having)).ToList();
        }

        rows = ApplyStatisticOrder(rows, spec, query.OrderByFields);
        var offset = Math.Min(FeaturePaging.ResolveOffset(query), rows.Count);
        var count = FeaturePaging.EffectivePageSize(query);
        var page = rows.Skip(offset).Take(count).ToArray();
        var exceeded = offset + page.Length < rows.Count;
        return WriteStatistics(new StatisticsPage(
            spec,
            page,
            exceeded,
            exceeded ? ResultPagination.Encode(offset + page.Length) : null));
    }

    /// <summary>
    /// Compiles the served statistics onto the contract's own vocabulary
    /// (ADR-0098 §7): the same validation the match path has always run, the
    /// group fields resolved against the schema, and one
    /// <see cref="AggregateSpec"/> per <c>outStatistics</c> entry.
    /// </summary>
    internal static StatisticsSpec Compile(DatasetDescription dataset, EsriFeatureQuery query)
    {
        var statistics = query.OutStatistics!;
        var groupFields = ResolveGroupFields(dataset, query.GroupByFields);
        var inputs = ResolveStatisticInputs(dataset, statistics);
        var specs = new AggregateSpec[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            specs[i] = Aggregate(inputs[i]);
        }

        var aggregate = new AggregateQuery(
            specs,
            groupFields.Count == 0 ? null : groupFields.Select(field => field.Name).ToArray());
        return new StatisticsSpec(dataset, groupFields, statistics, inputs, aggregate, Kinds(inputs, specs), Having(dataset, query, aggregate));
    }

    /// <summary>
    /// The <c>having</c> clause as a predicate over the <em>group row</em> — the
    /// group fields and the statistics' result names, which is the whole
    /// vocabulary a clause over a reduced group has (ADR-0128). The Esri clause
    /// is parsed once at the boundary into the same predicate vocabulary the
    /// plan's <c>where</c> uses, so this is a validated tree rather than text a
    /// store would have to parse, and the names it may use are checked here so
    /// the pushed reduction and the match path reject the same clause with the
    /// same message.
    /// </summary>
    private static Predicate? Having(DatasetDescription dataset, EsriFeatureQuery query, AggregateQuery aggregate)
    {
        if (query.Having?.Predicate is not { } clause)
        {
            return null;
        }

        var names = aggregate.GroupRowFields;
        foreach (var field in clause.Fields())
        {
            if (!names.Contains(field.Name, StringComparer.Ordinal))
            {
                throw GeoServicesErrors.Invalid(
                    $"The 'having' clause names unknown statistic or group field '{field.Name}' in layer '{dataset.Id}'.");
            }
        }

        return clause;
    }

    /// <summary>One served statistic as the store's reduction surface states it.</summary>
    private static AggregateSpec Aggregate(StatisticInput input)
    {
        var statistic = input.Spec;
        return new AggregateSpec(
            Statistic(statistic.StatisticType),
            input.CountRows ? AggregateSpec.AllFields : statistic.OnStatisticField,
            statistic.OutStatisticFieldName,
            statistic.PercentileValue,
            statistic.PercentileDescending);
    }

    private static AggregateStatistic Statistic(string type) => type switch
    {
        "count" => AggregateStatistic.Count,
        "sum" => AggregateStatistic.Sum,
        "min" => AggregateStatistic.Minimum,
        "max" => AggregateStatistic.Maximum,
        "avg" => AggregateStatistic.Average,
        "var" => AggregateStatistic.Variance,
        "stddev" => AggregateStatistic.StdDev,
        "percentile_cont" => AggregateStatistic.PercentileContinuous,
        _ => AggregateStatistic.PercentileDiscrete,
    };

    /// <summary>
    /// The kind each result is reported as, from the reference's own rule
    /// (<see cref="FeatureReduction.ResultKind"/>) rather than a second copy of
    /// it here: a count is an integer, an integer field's sum is an integer,
    /// every other numeric reduction is a double and an extreme takes the
    /// field's kind. A kind the Esri field type has no name for is reported as
    /// a double, as it always was.
    /// </summary>
    private static AttributeKind[] Kinds(List<StatisticInput> inputs, AggregateSpec[] specs)
    {
        var kinds = new AttributeKind[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            var kind = FeatureReduction.ResultKind(specs[i], inputs[i].Kind);
            kinds[i] = kind is AttributeKind.Int64 or AttributeKind.Double or AttributeKind.String
                or AttributeKind.DateTimeOffset or AttributeKind.Guid or AttributeKind.Boolean
                    ? kind
                    : AttributeKind.Double;
        }

        return kinds;
    }

    private static List<GroupField> ResolveGroupFields(DatasetDescription dataset, IReadOnlyList<string>? names)
    {
        var fields = new List<GroupField>();
        if (names is null)
        {
            return fields;
        }

        foreach (var name in names)
        {
            var index = dataset.Schema.IndexOf(name);
            if (index < 0)
            {
                throw GeoServicesErrors.Invalid($"'groupByFieldsForStatistics' names unknown field '{name}' in layer '{dataset.Id}'.");
            }

            if (dataset.Schema[index].Kind == AttributeKind.Geometry)
            {
                throw GeoServicesErrors.Invalid($"'groupByFieldsForStatistics' cannot group by geometry field '{name}'.");
            }

            if (fields.Any(field => string.Equals(field.Name, dataset.Schema[index].Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw GeoServicesErrors.Invalid($"Duplicate group field '{name}'.");
            }

            fields.Add(new GroupField(dataset.Schema[index].Name, index, dataset.Schema[index].Kind));
        }

        return fields;
    }

    private static List<StatisticInput> ResolveStatisticInputs(DatasetDescription dataset, IReadOnlyList<EsriOutStatistic> statistics)
    {
        var inputs = new List<StatisticInput>(statistics.Count);
        foreach (var statistic in statistics)
        {
            if ((statistic.OnStatisticField == "*" || string.Equals(statistic.OnStatisticField, statistic.OutStatisticFieldName, StringComparison.OrdinalIgnoreCase)) && statistic.StatisticType == "count")
            {
                inputs.Add(new StatisticInput(statistic, -1, AttributeKind.Int64, true));
                continue;
            }

            var index = dataset.Schema.IndexOf(statistic.OnStatisticField);
            if (index < 0)
            {
                throw GeoServicesErrors.Invalid($"Statistic '{statistic.OutStatisticFieldName}' names unknown field '{statistic.OnStatisticField}' in layer '{dataset.Id}'.");
            }

            var kind = dataset.Schema[index].Kind;
            if (kind == AttributeKind.Geometry)
            {
                throw GeoServicesErrors.Invalid($"Statistic '{statistic.OutStatisticFieldName}' cannot aggregate geometry field '{statistic.OnStatisticField}'.");
            }

            if (statistic.StatisticType is "sum" or "avg" or "stddev" or "var" or "percentile_cont" or "percentile_disc" && kind is not (AttributeKind.Int64 or AttributeKind.Double))
            {
                throw GeoServicesErrors.Invalid($"Statistic '{statistic.StatisticType}' on field '{statistic.OnStatisticField}' needs a numeric field.");
            }

            inputs.Add(new StatisticInput(statistic, index, kind, false));
        }

        return inputs;
    }

    private static List<KeyValuePair<AttributeValue[], List<MatchedFeature>>> GroupMatches(
        IReadOnlyList<MatchedFeature> matches, IReadOnlyList<GroupField> groupFields)
    {
        var groups = new Dictionary<AttributeValue[], List<MatchedFeature>>(FeatureOrdering.AttributeRowComparer.Instance);
        var order = new List<AttributeValue[]>();
        foreach (var match in matches)
        {
            var key = new AttributeValue[groupFields.Count];
            for (var i = 0; i < groupFields.Count; i++)
            {
                key[i] = match.Feature[groupFields[i].Index];
            }

            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
                order.Add(key);
            }

            list.Add(match);
        }

        return order.Select(key => new KeyValuePair<AttributeValue[], List<MatchedFeature>>(key, groups[key])).ToList();
    }

    private static StatisticRow NullRow(StatisticsSpec spec)
    {
        var groupValues = new AttributeValue[spec.GroupFields.Count];
        for (var i = 0; i < groupValues.Length; i++)
        {
            groupValues[i] = AttributeValue.Null;
        }

        var values = new AttributeValue[spec.Statistics.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = AttributeValue.Null;
        }

        return new StatisticRow(groupValues, values, spec.Kinds);
    }

    private static StatisticRow ComputeRow(
        StatisticsSpec spec,
        AttributeValue[] key,
        IReadOnlyList<MatchedFeature> members)
    {
        var values = new AttributeValue[spec.Statistics.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Aggregate(members, spec.Inputs[i]);
        }

        return new StatisticRow(key, values, spec.Kinds);
    }

    internal static AttributeValue Aggregate(IReadOnlyList<MatchedFeature> members, StatisticInput input)
    {
        var type = input.Spec.StatisticType;
        if (type == "count" && input.CountRows)
        {
            return AttributeValue.FromInt64(members.Count);
        }

        var raw = CollectNonNull(members, input.Index);
        if (raw.Count == 0)
        {
            return AttributeValue.Null;
        }

        if (type == "count")
        {
            return AttributeValue.FromInt64(raw.Count);
        }

        if (type is "percentile_cont" or "percentile_disc")
        {
            return Percentile(raw, input.Spec);
        }

        if (type is "min" or "max")
        {
            return AggregateMinMax(raw, type);
        }

        return AggregateNumeric(raw, input);
    }

    /// <summary>Collects the group's non-null values for one statistic input, skipping nulls.</summary>
    private static List<AttributeValue> CollectNonNull(IReadOnlyList<MatchedFeature> members, int index)
    {
        var raw = new List<AttributeValue>();
        foreach (var member in members)
        {
            var value = member.Feature[index];
            if (!value.IsNull)
            {
                raw.Add(value);
            }
        }

        return raw;
    }

    /// <summary>Reduces the group's non-null values to their extreme under the dataset's value ordering.</summary>
    private static AttributeValue AggregateMinMax(List<AttributeValue> raw, string type)
    {
        var best = raw[0];
        foreach (var candidate in raw.Skip(1))
        {
            var order = FeatureOrdering.AttributeValueComparer.Instance.Compare(candidate, best);
            if ((type == "min" && order < 0) || (type == "max" && order > 0))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Reduces the group's non-null values to their numeric summary (sum/avg/var/stddev).</summary>
    private static AttributeValue AggregateNumeric(List<AttributeValue> raw, StatisticInput input)
    {
        if (IsExactInt64Sum(input, raw))
        {
            return AttributeValue.FromInt64(raw.Sum(value => value.Int64Value));
        }

        return NumericStatistic(input.Spec.StatisticType, raw.Select(ToDouble).ToArray());
    }

    /// <summary>Whether the group sums exactly as Int64, so the double reduction is unnecessary.</summary>
    private static bool IsExactInt64Sum(StatisticInput input, List<AttributeValue> raw) =>
        IsInt64Sum(input) && AllInt64(raw);

    private static bool IsInt64Sum(StatisticInput input) =>
        input.Spec.StatisticType == "sum" && input.Kind == AttributeKind.Int64;

    private static bool AllInt64(List<AttributeValue> raw) => raw.All(value => value.Kind == AttributeKind.Int64);

    /// <summary>The numeric reductions, keyed by Esri statistic type; an unlisted type yields null.</summary>
    private static readonly Dictionary<string, Func<double[], double?>> NumericStatistics = new(StringComparer.Ordinal)
    {
        ["sum"] = numbers => numbers.Sum(),
        ["avg"] = numbers => numbers.Average(),
        ["var"] = Variance,
        ["stddev"] = numbers => Variance(numbers) is { } variance ? Math.Sqrt(variance) : null,
    };

    private static AttributeValue NumericStatistic(string type, double[] numbers) =>
        NumericStatistics.TryGetValue(type, out var reduce) && reduce(numbers) is { } value
            ? AttributeValue.FromDouble(value)
            : AttributeValue.Null;

    /// <summary>
    /// The S3 percentile statistic over the group's non-null numeric
    /// values, ranked in the requested order: discrete returns the dataset
    /// value at rank <c>ceil(fraction × n)</c>, continuous linearly
    /// interpolates at rank <c>fraction × (n − 1)</c>.
    /// </summary>
    private static AttributeValue Percentile(List<AttributeValue> raw, EsriOutStatistic spec)
    {
        var numbers = raw.Select(ToDouble).ToList();
        numbers.Sort();
        if (spec.PercentileDescending)
        {
            numbers.Reverse();
        }

        var fraction = spec.PercentileValue ?? 0;
        return spec.StatisticType == "percentile_disc"
            ? AttributeValue.FromDouble(DiscretePercentile(numbers, fraction))
            : AttributeValue.FromDouble(ContinuousPercentile(numbers, fraction));
    }

    private static double DiscretePercentile(List<double> sorted, double fraction)
    {
        var rank = (int)Math.Ceiling(fraction * sorted.Count);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Count - 1)];
    }

    private static double ContinuousPercentile(List<double> sorted, double fraction)
    {
        var rank = fraction * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        return lower == upper
            ? sorted[lower]
            : sorted[lower] + ((rank - lower) * (sorted[upper] - sorted[lower]));
    }

    private static double ToDouble(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Int64 => value.Int64Value,
        AttributeKind.Double => value.DoubleValue,
        _ => throw GeoServicesErrors.Invalid($"Cannot aggregate non-numeric value of kind {value.Kind}."),
    };

    /// <summary>
    /// The sample variance, or <c>null</c> for fewer than two values: the
    /// sample form divides by n − 1, and a single value has no such division —
    /// the same answer SQL's <c>VAR_SAMP</c> gives, and the same one
    /// <see cref="FeatureReduction"/> states, which is what a store that pushes
    /// the reduction down is measured against.
    /// </summary>
    private static double? Variance(double[] numbers)
    {
        if (numbers.Length < 2)
        {
            return null;
        }

        var mean = numbers.Average();
        return numbers.Sum(number => (number - mean) * (number - mean)) / (numbers.Length - 1);
    }

    private static bool HavingMatches(StatisticRow row, StatisticsSpec spec, EsriWhere having)
    {
        var groupFields = spec.GroupFields;
        var statistics = spec.Statistics;
        var fields = new List<FieldDefinition>(groupFields.Count + statistics.Count);
        var values = new List<AttributeValue>(fields.Capacity);
        for (var i = 0; i < groupFields.Count; i++)
        {
            fields.Add(new FieldDefinition(groupFields[i].Name, KindForComparison(groupFields[i].Kind)));
            values.Add(row.GroupValues[i]);
        }

        for (var i = 0; i < statistics.Count; i++)
        {
            fields.Add(new FieldDefinition(statistics[i].OutStatisticFieldName, KindForComparison(row.StatKinds[i])));
            values.Add(row.StatValues[i]);
        }

        var schema = new FeatureSchema(fields);
        var feature = new Feature(new FeatureId("having"), schema, values.ToArray());
        return EsriPredicateEvaluator.Matches(having.Predicate, feature);
    }

    private static AttributeKind KindForComparison(AttributeKind kind) => kind == AttributeKind.Null ? AttributeKind.Double : kind;

    private static List<StatisticRow> ApplyStatisticOrder(
        List<StatisticRow> rows,
        StatisticsSpec spec,
        IReadOnlyList<EsriOrderByField>? orderBy)
    {
        if (orderBy is not { Count: > 0 })
        {
            return rows;
        }

        IOrderedEnumerable<StatisticRow>? ordered = null;
        foreach (var key in orderBy)
        {
            var selector = StatisticSelector(key.Name, spec);
            ordered = ordered is null
                ? (key.Descending ? rows.OrderByDescending(selector, FeatureOrdering.AttributeValueComparer.Instance) : rows.OrderBy(selector, FeatureOrdering.AttributeValueComparer.Instance))
                : (key.Descending ? ordered.ThenByDescending(selector, FeatureOrdering.AttributeValueComparer.Instance) : ordered.ThenBy(selector, FeatureOrdering.AttributeValueComparer.Instance));
        }

        return ordered!.ToList();
    }

    private static Func<StatisticRow, AttributeValue> StatisticSelector(string name, StatisticsSpec spec)
    {
        var groupFields = spec.GroupFields;
        var statistics = spec.Statistics;
        var dataset = spec.Dataset;
        var groupIndex = IndexOfField(groupFields, group => group.Name, name);
        if (groupIndex >= 0)
        {
            var groupPosition = groupIndex;
            return row => row.GroupValues[groupPosition];
        }

        var statisticIndex = IndexOfField(statistics, statistic => statistic.OutStatisticFieldName, name);
        if (statisticIndex >= 0)
        {
            var statisticPosition = statisticIndex;
            return row => row.StatValues[statisticPosition];
        }

        throw GeoServicesErrors.Invalid($"'orderByFields' names unknown statistic or group field '{name}' in layer '{dataset.Id}'.");
    }

    private static int IndexOfField<T>(IReadOnlyList<T> items, Func<T, string> nameOf, string name) =>
        items.Select((item, index) => (Index: index, Name: nameOf(item)))
            .Where(pair => string.Equals(pair.Name, name, StringComparison.OrdinalIgnoreCase))
            .Select(pair => (int?)pair.Index)
            .FirstOrDefault() ?? -1;

    private static IResult WriteStatistics(StatisticsPage page)
    {
        var spec = page.Spec;
        var groupFields = spec.GroupFields;
        var statistics = spec.Statistics;
        var rows = page.Rows;
        var exceeded = page.Exceeded;
        var nextToken = page.NextToken;
        return EsriJson.Write(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("displayFieldName", groupFields.Count > 0 ? groupFields[0].Name : string.Empty);
            writer.WritePropertyName("fields");
            writer.WriteStartArray();
            foreach (var group in groupFields)
            {
                FeatureResponseWriter.WriteField(writer, group.Name, EsriFieldType.FromAttributeKind(group.Kind), true, false);
            }

            for (var i = 0; i < statistics.Count; i++)
            {
                FeatureResponseWriter.WriteField(writer, statistics[i].OutStatisticFieldName, EsriFieldType.FromAttributeKind(spec.Kinds[i]), true, false);
            }

            writer.WriteEndArray();
            writer.WritePropertyName("features");
            writer.WriteStartArray();
            foreach (var row in rows)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("attributes");
                writer.WriteStartObject();
                for (var i = 0; i < groupFields.Count; i++)
                {
                    EsriAttributeCodec.Write(writer, groupFields[i].Name, row.GroupValues[i]);
                }

                for (var i = 0; i < statistics.Count; i++)
                {
                    EsriAttributeCodec.Write(writer, statistics[i].OutStatisticFieldName, row.StatValues[i]);
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteBoolean("exceededTransferLimit", exceeded);
            FeatureResponseWriter.WritePaginationToken(writer, nextToken);
            writer.WriteEndObject();
        });
    }

    internal sealed record GroupField(string Name, int Index, AttributeKind Kind);

    internal sealed record StatisticInput(EsriOutStatistic Spec, int Index, AttributeKind Kind, bool CountRows);

    private sealed record StatisticRow(AttributeValue[] GroupValues, AttributeValue[] StatValues, AttributeKind[] StatKinds);

    /// <summary>
    /// A served statistics request compiled once: the layer it was compiled
    /// against, the group fields, the requested statistics and their resolved
    /// inputs, the same request as the store's own reduction surface states it,
    /// the <c>having</c> clause over the group row, and the kind each result is
    /// reported as. Both paths — the matched set and the pushed-down reduction —
    /// are written from one of these, so they can only differ in where the
    /// numbers came from.
    /// </summary>
    internal sealed record StatisticsSpec(
        DatasetDescription Dataset,
        IReadOnlyList<GroupField> GroupFields,
        IReadOnlyList<EsriOutStatistic> Statistics,
        IReadOnlyList<StatisticInput> Inputs,
        AggregateQuery Aggregate,
        AttributeKind[] Kinds,
        Predicate? Having)
    {
        /// <summary>The statistic values of a returned group, without the row-count probe.</summary>
        public AttributeValue[] Values(AggregateGroup group) =>
            [.. group.Values.Take(Statistics.Count)];
    }

    /// <summary>
    /// Whether a returned group came from an empty match set — the served
    /// surface's one-row-of-nulls rule, which a reduction's own numbers cannot
    /// express (a row count of zero is not a null) and the row-count probe the
    /// ungrouped request asked for can.
    /// </summary>
    private static bool IsEmptyMatch(StatisticsSpec spec, AggregateGroup group) =>
        group.Values.Count > spec.Statistics.Count
        && group.Values[spec.Statistics.Count].Kind == AttributeKind.Int64
        && group.Values[spec.Statistics.Count].Int64Value == 0;

    /// <summary>
    /// One statistics response page: the compiled request the rows were written
    /// from, the rows of this page and the paging outcome.
    /// </summary>
    private sealed record StatisticsPage(
        StatisticsSpec Spec,
        IReadOnlyList<StatisticRow> Rows,
        bool Exceeded,
        string? NextToken);
}
