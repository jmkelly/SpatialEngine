using System.Globalization;
using System.Text.Json;
using Spatial.Contracts;
using Spatial.Contracts.Providers;
using Spatial.Core.Features;
using Spatial.Esri.Codec;

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
/// Anything else (quantile/natural-breaks methods, multi-field unique values)
/// is a typed <c>invalid.arguments</c>, never a silent fallback. The scan
/// honours <c>where</c> (the shared <see cref="EsriFilterClause"/> grammar)
/// and cancellation. This is the single <c>generateRenderer</c>
/// implementation for map-service layers; the feature write-model track
/// (T-038) must reuse it rather than duplicate it.
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

    private static EsriFilterClause? ParseWhere(string? where)
    {
        if (string.IsNullOrWhiteSpace(where) || string.Equals(where.Trim(), "1=1", StringComparison.Ordinal))
        {
            return null;
        }

        if (!EsriFilterClause.TryParse(where, out var clause, out var error))
        {
            throw GeoServicesErrors.Invalid($"The 'where' parameter is not supported: {error}");
        }

        return clause;
    }

    private static async Task<EsriRenderer> ClassBreaksAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        ClassificationDefinition definition,
        EsriFilterClause? filter,
        CancellationToken cancellationToken)
    {
        var field = RequiredString(definition.Root, "classificationField", definition.RawType);
        var index = FieldIndex(dataset, field);
        var kind = dataset.Schema[index].Kind;
        if (kind is not (AttributeKind.Int64 or AttributeKind.Double))
        {
            throw GeoServicesErrors.Invalid(
                $"The classification field '{field}' is {kind}, but 'classBreaksDef' needs a numeric field.");
        }

        var method = OptionalString(definition.Root, "classificationMethod") ?? "esriClassifyEqualInterval";
        if (!string.Equals(method, "esriClassifyEqualInterval", StringComparison.Ordinal))
        {
            throw GeoServicesErrors.Invalid(
                $"The classification method '{method}' is not supported; use 'esriClassifyEqualInterval'.");
        }

        var breakCount = OptionalInt(definition.Root, "breakCount") ?? 5;
        if (breakCount < 1 || breakCount > MaxBreaks)
        {
            throw GeoServicesErrors.Invalid(
                $"The 'breakCount' value '{breakCount}' is out of range; use 1 to {MaxBreaks}.");
        }

        var values = await NumericValuesAsync(store, dataset, index, filter, field, cancellationToken);
        if (values.Count == 0)
        {
            throw GeoServicesErrors.Invalid(
                $"No features of layer '{dataset.Id}' match, so no breaks can be classified for field '{field}'.");
        }

        var min = values.Min();
        var max = values.Max();
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
        EsriFilterClause? filter,
        CancellationToken cancellationToken)
    {
        var fields = Fields(definition.Root);
        if (fields.Count == 0)
        {
            throw GeoServicesErrors.Invalid("The 'uniqueValueFields' parameter needs exactly one field.");
        }

        if (fields.Count > 1)
        {
            throw GeoServicesErrors.Invalid(
                "Multi-field unique values are not supported; use exactly one 'uniqueValueFields' entry.");
        }

        var field = fields[0];
        var index = FieldIndex(dataset, field);
        var values = await DistinctValuesAsync(store, dataset, index, filter, cancellationToken);
        if (values.Count == 0)
        {
            throw GeoServicesErrors.Invalid(
                $"No features of layer '{dataset.Id}' match, so no unique values can be enumerated for field '{field}'.");
        }

        if (values.Count > MaxUniqueValues)
        {
            throw GeoServicesErrors.Invalid(
                $"Field '{field}' has {values.Count} distinct values (at most {MaxUniqueValues} supported); narrow the request with 'where'.");
        }

        var infos = values
            .OrderBy(value => value, StringComparer.Ordinal)
            .Select((value, position) => new EsriUniqueValueInfo(value, Symbol(dataset, QualitativePalette[position % QualitativePalette.Length]), value))
            .ToArray();
        return new EsriRenderer("uniqueValue", Field1: field, UniqueValueInfos: infos);
    }

    private static async Task<List<double>> NumericValuesAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        int index,
        EsriFilterClause? filter,
        string field,
        CancellationToken cancellationToken)
    {
        var values = new List<double>();
        var scheme = EsriObjectIdScheme.For(dataset);
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

            values.Add(StatisticNumber(feature[index], field));
        }

        return values;
    }

    private static bool MatchesFilter(Feature feature, long ordinal, EsriObjectIdScheme scheme, EsriFilterClause? filter, int index) =>
        Matches(feature, ordinal, scheme, filter) && !feature[index].IsNull;

    private static double StatisticNumber(AttributeValue value, string field) => value.Kind switch
    {
        AttributeKind.Int64 => value.Int64Value,
        AttributeKind.Double => value.DoubleValue,
        _ => throw GeoServicesErrors.Invalid(
            $"The classification field '{field}' holds {value.Kind} values, but 'classBreaksDef' needs numbers."),
    };

    private static async Task<List<string>> DistinctValuesAsync(
        IFeatureStore store,
        DatasetDescription dataset,
        int index,
        EsriFilterClause? filter,
        CancellationToken cancellationToken)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
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

    private static bool Matches(Feature feature, long ordinal, EsriObjectIdScheme scheme, EsriFilterClause? filter)
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

        return filter.Matches(feature, new EsriSyntheticField(EsriLayerModel.ObjectIdField, AttributeValue.FromInt64(objectId)));
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
