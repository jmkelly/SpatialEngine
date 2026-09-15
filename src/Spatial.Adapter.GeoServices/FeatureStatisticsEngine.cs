using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The <c>outStatistics</c> pipeline (spec §9.1.4.3): grouping, aggregation,
/// <c>having</c>, statistic ordering, paging and the statistics JSON.
/// Split out of <see cref="FeatureQueryEngine"/> so the query facade keeps
/// only orchestration and the statistics fan-out (schema, filter, codecs)
/// lives with the code that uses it (ADR-0040).
/// </summary>
internal static class FeatureStatisticsEngine
{
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
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches,
        EsriFeatureQuery query)
    {
        var statistics = query.OutStatistics!;
        var groupFields = ResolveGroupFields(dataset, query.GroupByFields);
        var statInputs = ResolveStatisticInputs(dataset, statistics);
        var groups = GroupMatches(matches, groupFields);
        var rows = new List<StatisticRow>();
        if (groups.Count == 0 && groupFields.Count == 0)
        {
            rows.Add(NullRow(groupFields, statistics, statInputs));
        }
        else
        {
            foreach (var group in groups)
            {
                rows.Add(ComputeRow(group.Key, group.Value, groupFields, statistics, statInputs));
            }
        }

        if (query.Having is { } having)
        {
            rows = rows.Where(row => HavingMatches(row, groupFields, statistics, having)).ToList();
        }

        rows = ApplyStatisticOrder(rows, groupFields, statistics, query.OrderByFields, dataset);
        var offset = Math.Min(FeatureQueryEngine.ResolveOffset(query), rows.Count);
        var count = FeatureQueryEngine.EffectivePageSize(query);
        var page = rows.Skip(offset).Take(count).ToArray();
        var exceeded = offset + page.Length < rows.Count;
        return WriteStatistics(new StatisticsPage(dataset, groupFields, statistics, statInputs, page, exceeded, exceeded ? ResultPagination.Encode(offset + page.Length) : null));
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

    private static List<KeyValuePair<AttributeValue[], List<FeatureQueryEngine.MatchedFeature>>> GroupMatches(
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> matches, IReadOnlyList<GroupField> groupFields)
    {
        var groups = new Dictionary<AttributeValue[], List<FeatureQueryEngine.MatchedFeature>>(FeatureQueryEngine.AttributeRowComparer.Instance);
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

        return order.Select(key => new KeyValuePair<AttributeValue[], List<FeatureQueryEngine.MatchedFeature>>(key, groups[key])).ToList();
    }

    private static StatisticRow NullRow(
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        IReadOnlyList<StatisticInput> inputs)
    {
        var groupValues = new AttributeValue[groupFields.Count];
        for (var i = 0; i < groupValues.Length; i++)
        {
            groupValues[i] = AttributeValue.Null;
        }

        var values = new AttributeValue[statistics.Count];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = AttributeValue.Null;
        }

        return new StatisticRow(groupValues, values, StatisticKinds(inputs));
    }

    private static StatisticRow ComputeRow(
        AttributeValue[] key,
        IReadOnlyList<FeatureQueryEngine.MatchedFeature> members,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        List<StatisticInput> inputs)
    {
        var values = new AttributeValue[statistics.Count];
        for (var i = 0; i < statistics.Count; i++)
        {
            values[i] = Aggregate(members, inputs[i]);
        }

        return new StatisticRow(key, values, StatisticKinds(inputs));
    }

    private static AttributeKind[] StatisticKinds(IReadOnlyList<StatisticInput> inputs)
    {
        var kinds = new AttributeKind[inputs.Count];
        for (var i = 0; i < inputs.Count; i++)
        {
            kinds[i] = ResultKind(inputs[i]);
        }

        return kinds;
    }

    private static AttributeKind ResultKind(StatisticInput input) => input.Spec.StatisticType switch
    {
        "count" => AttributeKind.Int64,
        "sum" when input.Kind == AttributeKind.Int64 => AttributeKind.Int64,
        "sum" or "avg" or "stddev" or "var" or "percentile_cont" or "percentile_disc" => AttributeKind.Double,
        "min" or "max" => input.Kind,
        _ => AttributeKind.Double,
    };

    internal static AttributeValue Aggregate(IReadOnlyList<FeatureQueryEngine.MatchedFeature> members, StatisticInput input)
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
    private static List<AttributeValue> CollectNonNull(IReadOnlyList<FeatureQueryEngine.MatchedFeature> members, int index)
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
            var order = FeatureQueryEngine.AttributeValueComparer.Instance.Compare(candidate, best);
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
        var type = input.Spec.StatisticType;
        if (type == "sum" && input.Kind == AttributeKind.Int64 && raw.All(value => value.Kind == AttributeKind.Int64))
        {
            return AttributeValue.FromInt64(raw.Sum(value => value.Int64Value));
        }

        var numbers = raw.Select(ToDouble).ToArray();
        return type switch
        {
            "sum" => AttributeValue.FromDouble(numbers.Sum()),
            "avg" => AttributeValue.FromDouble(numbers.Average()),
            "var" => AttributeValue.FromDouble(Variance(numbers)),
            "stddev" => AttributeValue.FromDouble(Math.Sqrt(Variance(numbers))),
            _ => AttributeValue.Null,
        };
    }

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

    private static double Variance(double[] numbers)
    {
        if (numbers.Length <= 1)
        {
            return 0;
        }

        var mean = numbers.Average();
        return numbers.Sum(number => (number - mean) * (number - mean)) / (numbers.Length - 1);
    }

    private static bool HavingMatches(
        StatisticRow row,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        EsriFilterClause having)
    {
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
        return having.Matches(feature);
    }

    private static AttributeKind KindForComparison(AttributeKind kind) => kind == AttributeKind.Null ? AttributeKind.Double : kind;

    private static List<StatisticRow> ApplyStatisticOrder(
        List<StatisticRow> rows,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        IReadOnlyList<EsriOrderByField>? orderBy,
        DatasetDescription dataset)
    {
        if (orderBy is not { Count: > 0 })
        {
            return rows;
        }

        IOrderedEnumerable<StatisticRow>? ordered = null;
        foreach (var key in orderBy)
        {
            var selector = StatisticSelector(key.Name, groupFields, statistics, dataset);
            ordered = ordered is null
                ? (key.Descending ? rows.OrderByDescending(selector, FeatureQueryEngine.AttributeValueComparer.Instance) : rows.OrderBy(selector, FeatureQueryEngine.AttributeValueComparer.Instance))
                : (key.Descending ? ordered.ThenByDescending(selector, FeatureQueryEngine.AttributeValueComparer.Instance) : ordered.ThenBy(selector, FeatureQueryEngine.AttributeValueComparer.Instance));
        }

        return ordered!.ToList();
    }

    private static Func<StatisticRow, AttributeValue> StatisticSelector(
        string name,
        IReadOnlyList<GroupField> groupFields,
        IReadOnlyList<EsriOutStatistic> statistics,
        DatasetDescription dataset)
    {
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
        var dataset = page.Dataset;
        var groupFields = page.GroupFields;
        var statistics = page.Statistics;
        var inputs = page.Inputs;
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
                FeatureResponseWriter.WriteField(writer, statistics[i].OutStatisticFieldName, EsriFieldType.FromAttributeKind(ResultKindForWrite(inputs[i], rows)), true, false);
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

    private static AttributeKind ResultKindForWrite(StatisticInput input, IReadOnlyList<StatisticRow> rows)
    {
        var kind = ResultKind(input);
        if (kind is not (AttributeKind.Int64 or AttributeKind.Double or AttributeKind.String or AttributeKind.DateTimeOffset or AttributeKind.Guid or AttributeKind.Boolean))
        {
            return AttributeKind.Double;
        }

        return kind;
    }

    private sealed record GroupField(string Name, int Index, AttributeKind Kind);

    internal sealed record StatisticInput(EsriOutStatistic Spec, int Index, AttributeKind Kind, bool CountRows);

    private sealed record StatisticRow(AttributeValue[] GroupValues, AttributeValue[] StatValues, AttributeKind[] StatKinds);

    /// <summary>
    /// One statistics response page: the layer, its grouping, the requested
    /// statistics and their resolved inputs, the computed rows and the paging
    /// outcome. Grouping one value keeps the writer to a single parameter.
    /// </summary>
    private sealed record StatisticsPage(
        DatasetDescription Dataset,
        IReadOnlyList<GroupField> GroupFields,
        IReadOnlyList<EsriOutStatistic> Statistics,
        IReadOnlyList<StatisticInput> Inputs,
        IReadOnlyList<StatisticRow> Rows,
        bool Exceeded,
        string? NextToken);
}
