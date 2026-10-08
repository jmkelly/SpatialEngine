using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Core.Features.Query;
using Spatial.Esri.Codec;
using Spatial.Querying;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The per-layer <c>generateRenderer</c> classification (S4
/// generate-renderer-map-service-layer/, ADR-0055): a server-side renderer
/// derived from the layer's data for one request. It supports the two
/// classification definitions ArcGIS clients send:
/// <list type="bullet">
///   <item><c>classBreaksDef</c> with <c>esriClassifyEqualInterval</c> over a numeric field;</item>
///   <item><c>uniqueValueDef</c> with exactly one <c>uniqueValueFields</c> entry.</item>
/// </list>
/// <para>
/// The two reductions a classification needs are the store's, not a pass over
/// a materialised match set: the class-break domain is a minimum and a maximum
/// of the classification field, and the unique-value domain is a distinct set
/// (ADR-0112). The <c>where</c> clause rides along in the plan's predicate
/// (ADR-0097), and the quantisation — the equal-interval breaks and the
/// palette — stays here, over the two numbers the store returned.
/// </para>
/// <para>
/// A request whose clause no store can read — one naming the synthetic
/// <c>OBJECTID</c> of a layer whose object id is the scan ordinal — is not
/// reduced at all: it keeps the scan, because a reduction without the clause
/// would answer a different question (ADR-0097).
/// </para>
/// <para>
/// Anything else (quantile/natural-breaks methods, multi-field unique values)
/// is a typed <c>invalid.arguments</c>, never a silent fallback. The reduction
/// honours <c>where</c> (the shared <see cref="EsriWhere"/> grammar) and
/// cancellation. This is the single <c>generateRenderer</c> implementation for
/// map-service layers; the feature write-model track (T-038) must reuse it
/// rather than duplicate it.
/// </summary>
internal static class MapGenerateRenderer
{
    private const int MaxBreaks = 32;
    private const int MaxUniqueValues = 64;

    private static readonly int[][] QualitativePalette =
    [
        [228, 26, 28, 255],
        [55, 126, 184, 255],
        [77, 175, 74, 255],
        [152, 78, 163, 255],
        [255, 127, 0, 255],
        [255, 255, 51, 255],
        [166, 86, 40, 255],
        [247, 129, 191, 255],
    ];

    private static readonly int[] SequentialLow = [255, 255, 204, 255];
    private static readonly int[] SequentialHigh = [255, 0, 0, 255];

    /// <summary>
    /// Classifies the layer's features into a renderer. <paramref name="dataset"/>
    /// is the layer's catalogue description; <paramref name="classificationDefJson"/>
    /// is the required <c>classificationDef</c> parameter.
    /// </summary>
    public static async Task<EsriRenderer> GenerateAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        string? classificationDefJson,
        string? where,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(dataset);
        if (EsriLayerModel.IsTable(dataset))
        {
            throw GeoServicesErrors.Invalid(
                $"Layer '{dataset.Id}' has no geometry, so no renderer can be generated for it.");
        }

        var definition = ParseDefinition(classificationDefJson);
        var filter = ParseWhere(where);
        return definition.Type switch
        {
            "classbreaksdef" => await ClassBreaksAsync(store, dataset, definition, filter, cancellationToken),
            "uniquevaluedef" => await UniqueValuesAsync(store, dataset, definition, filter, cancellationToken),
            _ => throw GeoServicesErrors.Invalid(
                $"The classification type '{definition.RawType}' is not supported; use 'classBreaksDef' or 'uniqueValueDef'."),
        };
    }

    private static ClassificationDefinition ParseDefinition(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw GeoServicesErrors.Invalid("The 'classificationDef' parameter is required.");
        }

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (JsonException exception)
        {
            throw GeoServicesErrors.Invalid($"The 'classificationDef' parameter is not valid JSON: {exception.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String)
        {
            throw GeoServicesErrors.Invalid(
                "The 'classificationDef' parameter needs a 'type' of 'classBreaksDef' or 'uniqueValueDef'.");
        }

        return new ClassificationDefinition(type.GetString()!, root);
    }

    private static EsriWhere? ParseWhere(string? where)
    {
        if (string.IsNullOrWhiteSpace(where) || string.Equals(where.Trim(), "1=1", StringComparison.Ordinal))
        {
            return null;
        }

        if (!EsriWhere.TryParse(where, out var clause, out var error))
        {
            throw GeoServicesErrors.Invalid($"The 'where' parameter is not supported: {error}");
        }

        return clause;
    }

    private static async Task<EsriRenderer> ClassBreaksAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        ClassificationDefinition definition,
        EsriWhere? filter,
        CancellationToken cancellationToken)
    {
        var field = RequiredString(definition.Root, "classificationField", definition.RawType);
        var index = FieldIndex(dataset, field);
        RequireNumericField(dataset, field, index, definition.RawType);
        RequireEqualInterval(definition.Root);
        var breakCount = RequireBreakCount(definition.Root);

        var (minimum, maximum) = await ClassBreakDomainAsync(store, dataset, field, index, filter, cancellationToken);
        if (minimum is not { } min || maximum is not { } max)
        {
            throw GeoServicesErrors.Invalid(
                $"No features of layer '{dataset.Id}' match, so no breaks can be classified for field '{field}'.");
        }

        return BuildClassBreaks(dataset, field, min, max, breakCount);
    }

    private static void RequireNumericField(DatasetDescription dataset, string field, int index, string rawType)
    {
        var kind = dataset.Schema[index].Kind;
        if (kind is not (AttributeKind.Int64 or AttributeKind.Double))
        {
            throw GeoServicesErrors.Invalid(
                $"The classification field '{field}' is {kind}, but 'classBreaksDef' needs a numeric field.");
        }
    }

    private static void RequireEqualInterval(JsonElement root)
    {
        var method = OptionalString(root, "classificationMethod") ?? "esriClassifyEqualInterval";
        if (!string.Equals(method, "esriClassifyEqualInterval", StringComparison.Ordinal))
        {
            throw GeoServicesErrors.Invalid(
                $"The classification method '{method}' is not supported; use 'esriClassifyEqualInterval'.");
        }
    }

    private static int RequireBreakCount(JsonElement root)
    {
        var breakCount = OptionalInt(root, "breakCount") ?? 5;
        if (breakCount < 1 || breakCount > MaxBreaks)
        {
            throw GeoServicesErrors.Invalid(
                $"The 'breakCount' value '{breakCount}' is out of range; use 1 to {MaxBreaks}.");
        }

        return breakCount;
    }

    private static EsriRenderer BuildClassBreaks(DatasetDescription dataset, string field, double min, double max, int breakCount)
    {
        var bounds = min == max
            ? [max]
            : Enumerable.Range(1, breakCount).Select(step => min + ((max - min) * step / breakCount)).ToArray();
        var infos = bounds.Select((bound, position) =>
        {
            var low = position == 0 ? min : bounds[position - 1];
            return new EsriClassBreakInfo(bound, Symbol(dataset, SequentialRamp(bounds.Length == 1 ? 1.0 : (double)position / (bounds.Length - 1))), Label(low, bound));
        }).ToArray();
        return new EsriRenderer("classBreaks", Field: field, MinValue: min, ClassBreakInfos: infos);
    }

    private static async Task<EsriRenderer> UniqueValuesAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        ClassificationDefinition definition,
        EsriWhere? filter,
        CancellationToken cancellationToken)
    {
        var field = RequireSingleUniqueField(definition.Root);
        var index = FieldIndex(dataset, field);
        var values = await DistinctValuesAsync(store, dataset, field, index, filter, cancellationToken);
        RequireEnumerableValues(dataset, field, values);

        var infos = values
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select((value, position) => new EsriUniqueValueInfo(value, Symbol(dataset, QualitativePalette[position % QualitativePalette.Length]), value))
            .ToArray();
        return new EsriRenderer("uniqueValue", Field1: field, UniqueValueInfos: infos);
    }

    /// <summary>The single <c>uniqueValueFields</c> entry, or the Esri reason the request is unusable.</summary>
    private static string RequireSingleUniqueField(JsonElement root)
    {
        var fields = Fields(root);
        return fields.Count == 1 ? fields[0] : throw UnusableUniqueFields(fields.Count);
    }

    private static EsriInteropException UnusableUniqueFields(int count) =>
        count == 0
            ? GeoServicesErrors.Invalid("The 'uniqueValueFields' parameter needs exactly one field.")
            : GeoServicesErrors.Invalid(
                "Multi-field unique values are not supported; use exactly one 'uniqueValueFields' entry.");

    /// <summary>Rejects a value set that is empty (nothing matched) or larger than the enumeration bound.</summary>
    private static void RequireEnumerableValues(DatasetDescription dataset, string field, List<string> values)
    {
        if (values.Count == 0)
        {
            throw NoValues(dataset, field);
        }

        RequireBoundedValues(field, values);
    }

    private static void RequireBoundedValues(string field, List<string> values)
    {
        if (values.Count > MaxUniqueValues)
        {
            throw TooManyValues(field, values.Count);
        }
    }

    private static EsriInteropException NoValues(DatasetDescription dataset, string field) =>
        GeoServicesErrors.Invalid(
            $"No features of layer '{dataset.Id}' match, so no unique values can be enumerated for field '{field}'.");

    private static EsriInteropException TooManyValues(string field, int count) =>
        GeoServicesErrors.Invalid(
            $"Field '{field}' has {count} distinct values (at most {MaxUniqueValues} supported); narrow the request with 'where'.");

    /// <summary>
    /// The class-break domain: the smallest and the largest value the
    /// classification field takes over the features the request selects, or
    /// null when it takes none. Asked of the store as an aggregate whenever
    /// the request's clause can ride along, and otherwise read off the scan
    /// the same way the reduction would have computed it.
    /// </summary>
    private static async Task<(double? Minimum, double? Maximum)> ClassBreakDomainAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        string field,
        int index,
        EsriWhere? filter,
        CancellationToken cancellationToken)
    {
        var clause = ReductionClause(dataset, filter);
        if (clause is not null || filter is null)
        {
            return await ClassBreakReducedAsync(store, dataset, field, clause, cancellationToken);
        }

        return await ClassBreakScannedAsync(store, dataset, field, index, filter, cancellationToken);
    }

    private static async Task<(double? Minimum, double? Maximum)> ClassBreakReducedAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        string field,
        Predicate? clause,
        CancellationToken cancellationToken)
    {
        var reduction = new AggregateQuery(
        [
            new AggregateSpec(AggregateStatistic.Minimum, field),
            new AggregateSpec(AggregateStatistic.Maximum, field),
        ]);
        var page = await FeatureReductionFallback
            .AggregateAsync(store, dataset.Id, new FeatureQuery(Where: clause), reduction, cancellationToken)
            .ConfigureAwait(false);
        var group = page.Groups.Count > 0 ? page.Groups[0] : null;
        var values = group?.Values;
        return (Bound(values, 0, field), Bound(values, 1, field));
    }

    private static async Task<(double? Minimum, double? Maximum)> ClassBreakScannedAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        string field,
        int index,
        EsriWhere? filter,
        CancellationToken cancellationToken)
    {
        var scheme = EsriObjectIdScheme.For(dataset);
        double? minimum = null;
        double? maximum = null;
        long ordinal = 0;
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (!MatchesFilter(feature, ordinal, scheme, filter, index))
            {
                continue;
            }

            var value = StatisticNumber(feature[index], field);
            minimum = minimum is { } low ? Math.Min(low, value) : value;
            maximum = maximum is { } high ? Math.Max(high, value) : value;
        }

        return (minimum, maximum);
    }

    /// <summary>One reduced value of an aggregate group, or null when the group has no value for it.</summary>
    private static double? Bound(IReadOnlyList<AttributeValue>? values, int position, string field) =>
        values is { Count: > 0 } && position < values.Count && !values[position].IsNull
            ? StatisticNumber(values[position], field)
            : null;

    /// <summary>
    /// The clause the plan can carry, or null when there is no clause to carry
    /// one. A clause the resolver refuses (a synthetic <c>OBJECTID</c> on a
    /// layer whose object id is the scan ordinal) is reported as "no plan",
    /// which is what keeps the request on the scan path rather than reducing
    /// an unfiltered set (ADR-0097).
    /// </summary>
    private static Predicate? ReductionClause(DatasetDescription dataset, EsriWhere? filter) =>
        filter is null ? null : EsriWhereResolver.Pushdown(filter, EsriObjectIdScheme.For(dataset), dataset);

    private static bool MatchesFilter(Feature feature, long ordinal, EsriObjectIdScheme scheme, EsriWhere? filter, int index) =>
        Matches(feature, ordinal, scheme, filter) && !feature[index].IsNull;

    private static double StatisticNumber(AttributeValue value, string field) => value.Kind switch
    {
        AttributeKind.Int64 => value.Int64Value,
        AttributeKind.Double => value.DoubleValue,
        _ => throw GeoServicesErrors.Invalid(
            $"The classification field '{field}' holds {value.Kind} values, but 'classBreaksDef' needs numbers."),
    };

    /// <summary>
    /// The unique-value domain: the distinct non-null values the field takes
    /// over the features the request selects, deduplicated on the rendered
    /// value. Asked of the store as a distinct set whenever the request's
    /// clause can ride along, and otherwise read off the scan.
    /// </summary>
    private static async Task<List<string>> DistinctValuesAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        string field,
        int index,
        EsriWhere? filter,
        CancellationToken cancellationToken)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var clause = ReductionClause(dataset, filter);
        if (clause is not null || filter is null)
        {
            return await DistinctReducedAsync(store, dataset, field, clause, values, cancellationToken);
        }

        return await DistinctScannedAsync(store, dataset, index, filter, values, cancellationToken);
    }

    private static async Task<List<string>> DistinctReducedAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        string field,
        Predicate? clause,
        HashSet<string> values,
        CancellationToken cancellationToken)
    {
        var page = await FeatureReductionFallback
            .DistinctAsync(store, dataset.Id, new FeatureQuery(Where: clause), new DistinctQuery([field]), cancellationToken)
            .ConfigureAwait(false);
        // The store dedups by value, the renderer enumerates by rendered
        // value: two values that render alike are one class here, as they
        // were when the set was built in memory.
        foreach (var row in page.Rows)
        {
            if (row is { Count: > 0 } && !row[0].IsNull)
            {
                values.Add(Format(row[0]));
            }
        }

        return [.. values];
    }

    private static async Task<List<string>> DistinctScannedAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        int index,
        EsriWhere? filter,
        HashSet<string> values,
        CancellationToken cancellationToken)
    {
        var scheme = EsriObjectIdScheme.For(dataset);
        long ordinal = 0;
        var batches = await store.ScanAsync(dataset.Id, cancellationToken);
        foreach (var feature in batches.SelectMany(batch => batch.Features))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ordinal++;
            if (!Matches(feature, ordinal, scheme, filter))
            {
                continue;
            }

            var value = feature[index];
            if (!value.IsNull)
            {
                values.Add(Format(value));
            }
        }

        return [.. values];
    }

    private static bool Matches(Feature feature, long ordinal, EsriObjectIdScheme scheme, EsriWhere? filter)
    {
        if (filter is null)
        {
            return true;
        }

        if (!scheme.TryResolve(feature, ordinal, out var objectId))
        {
            throw GeoServicesErrors.ServerError(
                "The identity column of the layer is not an integer.");
        }

        return EsriPredicateEvaluator.Matches(filter.Predicate, feature, new EsriFieldOverlay(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(objectId)));
    }

    private static int FieldIndex(DatasetDescription dataset, string field)
    {
        var index = dataset.Schema.IndexOf(field);
        if (index < 0)
        {
            throw GeoServicesErrors.Invalid($"The field '{field}' does not exist in layer '{dataset.Id}'.");
        }

        return index;
    }

    private static string RequiredString(JsonElement root, string name, string type)
    {
        if (root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString()))
        {
            return value.GetString()!;
        }

        throw GeoServicesErrors.Invalid($"The '{type}' classification needs a non-empty '{name}'.");
    }

    private static string? OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? OptionalInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;

    private static IReadOnlyList<string> Fields(JsonElement root)
    {
        if (!root.TryGetProperty("uniqueValueFields", out var fields) || fields.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. fields.EnumerateArray()
            .Where(field => field.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(field.GetString()))
            .Select(field => field.GetString()!)];
    }

    private static readonly Dictionary<AttributeKind, Func<AttributeValue, string>> Formatters = new()
    {
        [AttributeKind.Int64] = FormatNumber,
        [AttributeKind.Double] = FormatNumber,
        [AttributeKind.String] = value => value.StringValue,
        [AttributeKind.Boolean] = FormatBoolean,
        [AttributeKind.DateTimeOffset] = FormatTimestamp,
        [AttributeKind.Guid] = value => value.GuidValue.ToString(),
    };

    internal static string Format(AttributeValue value) =>
        Formatters.TryGetValue(value.Kind, out var format) ? format(value) : string.Empty;

    private static string FormatBoolean(AttributeValue value) => value.BooleanValue ? "true" : "false";

    private static string FormatTimestamp(AttributeValue value) =>
        value.DateTimeOffsetValue.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);

    private static string FormatNumber(AttributeValue value) => value.Kind switch
    {
        AttributeKind.Int64 => value.Int64Value.ToString(CultureInfo.InvariantCulture),
        _ => value.DoubleValue.ToString(CultureInfo.InvariantCulture),
    };

    private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Label(double low, double high) => $"{Format(low)} - {Format(high)}";

    private static int[] SequentialRamp(double position) => [.. SequentialLow.Zip(SequentialHigh, (low, high) => (int)Math.Round(low + ((high - low) * position)))];

    private static EsriSymbol Symbol(DatasetDescription dataset, IReadOnlyList<int> color)
    {
        var family = dataset.GeometryType.Trim().ToLowerInvariant();
        if (family.StartsWith("line", StringComparison.Ordinal))
        {
            return new EsriSymbol("esriSLS", "esriSLSSolid", color, Width: 2);
        }

        if (family.StartsWith("polygon", StringComparison.Ordinal) || family.StartsWith("multipolygon", StringComparison.Ordinal))
        {
            return new EsriSymbol(
                "esriSFS", "esriSFSSolid", color,
                Outline: new EsriSymbolOutline("esriSLS", "esriSLSSolid", [0, 0, 0, 255], 1));
        }

        return new EsriSymbol("esriSMS", "esriSMSCircle", color, Size: 12);
    }

    private sealed record ClassificationDefinition(string RawType, JsonElement Root)
    {
        public string Type => RawType.ToLowerInvariant();
    }
}
