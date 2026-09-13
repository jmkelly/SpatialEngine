using Spatial.Core.Geometry;
using Spatial.Interop.Esri;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The parsed Feature Service <c>query</c> request (spec §9.1.4). Parsing
/// enforces the supported v1.0 subset and rejects the documented 10.x
/// additions explicitly, so a client never gets a silently widened query.
/// </summary>
internal sealed record EsriFeatureQuery(
    IReadOnlyList<long>? ObjectIds,
    EsriFilterClause? Where,
    IGeometry? Geometry,
    string SpatialRel,
    IReadOnlyList<string>? OutFields,
    IReadOnlyList<EsriOrderByField>? OrderByFields,
    bool ReturnGeometry,
    CoordinateReference? OutSr,
    bool ReturnIdsOnly,
    bool ReturnCountOnly,
    bool ReturnExtentOnly,
    bool ReturnDistinctValues,
    int? ResultOffset,
    int? ResultRecordCount,
    IReadOnlyList<EsriOutStatistic>? OutStatistics,
    IReadOnlyList<string>? GroupByFields,
    EsriFilterClause? Having)
{
    /// <summary>The default spatial relation: the spec's coarse envelope test.</summary>
    public const string EnvelopeIntersects = "esriSpatialRelEnvelopeIntersects";

    /// <summary>The exact intersection relation.</summary>
    public const string Intersects = "esriSpatialRelIntersects";

    /// <summary>Parses the query parameters, rejecting unsupported constructs.</summary>
    public static EsriFeatureQuery Parse(EsriRequestParameters parameters, CoordinateReference? fallback)
    {
        RejectUnsupported(parameters);
        var returnIdsOnly = parameters.GetBool("returnIdsOnly", false);
        var returnCountOnly = parameters.GetBool("returnCountOnly", false);
        var returnExtentOnly = parameters.GetBool("returnExtentOnly", false);
        var returnDistinctValues = parameters.GetBool("returnDistinctValues", false);
        var outStatistics = ParseOutStatistics(parameters.Get("outStatistics"));
        var groupByFields = ParseGroupByFields(parameters.Get("groupByFieldsForStatistics"));
        var having = ParseHaving(parameters.Get("having"));
        if (groupByFields is not null && outStatistics is null)
        {
            throw EsriInteropException.Invalid("'groupByFieldsForStatistics' requires 'outStatistics'.");
        }

        if (having is not null && outStatistics is null)
        {
            throw EsriInteropException.Invalid("'having' requires 'outStatistics'.");
        }

        ValidateResultShape(returnIdsOnly, returnCountOnly, returnExtentOnly, returnDistinctValues, outStatistics is not null);
        var inSr = EsriValueParser.ParseSpatialReference(parameters.Get("inSR"));
        return new EsriFeatureQuery(
            ParseObjectIds(parameters.Get("objectIds")),
            ParseWhere(parameters.Get("where")),
            ParseGeometry(parameters.Get("geometry"), inSr ?? fallback),
            ParseSpatialRel(parameters.Get("spatialRel")),
            ParseOutFields(parameters.Get("outFields")),
            ParseOrderByFields(parameters.Get("orderByFields")),
            parameters.GetBool("returnGeometry", true),
            EsriValueParser.ParseSpatialReference(parameters.Get("outSR")),
            returnIdsOnly,
            returnCountOnly,
            returnExtentOnly,
            returnDistinctValues,
            ParseNonNegativeInt(parameters.Get("resultOffset"), "resultOffset"),
            ParseNonNegativeInt(parameters.Get("resultRecordCount"), "resultRecordCount"),
            outStatistics,
            groupByFields,
            having);
    }

    private static IReadOnlyList<long>? ParseObjectIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return EsriValueParser.ParseInt64s(value, "objectIds");
    }

    private static EsriFilterClause? ParseWhere(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!EsriFilterClause.TryParse(value, out var clause, out var error))
        {
            throw EsriInteropException.Invalid($"The 'where' clause is not supported: {error}.");
        }

        return clause;
    }

    private static IGeometry? ParseGeometry(string? value, CoordinateReference? fallback) =>
        string.IsNullOrWhiteSpace(value) ? null : EsriValueParser.ParseGeometry(value, fallback);

    private static string ParseSpatialRel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EnvelopeIntersects;
        }

        return value.Trim() switch
        {
            EnvelopeIntersects or Intersects => value.Trim(),
            _ => throw EsriInteropException.Invalid(
                $"spatialRel '{value}' is not supported; use {EnvelopeIntersects} or {Intersects}."),
        };
    }

    private static string[]? ParseOutFields(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "*")
        {
            return null;
        }

        return value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }

    private static EsriOrderByField[]? ParseOrderByFields(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var fields = value
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseOrderByField)
            .ToArray();

        if (fields.Length == 0)
        {
            throw EsriInteropException.Invalid("'orderByFields' must name at least one field.");
        }

        return fields;
    }

    private static EsriOrderByField ParseOrderByField(string entry)
    {
        var parts = entry.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2)
        {
            throw EsriInteropException.Invalid(
                $"'orderByFields' entry '{entry}' is not a field name optionally followed by ASC or DESC.");
        }

        if (parts.Length == 1)
        {
            return new EsriOrderByField(parts[0], false);
        }

        return parts[1].ToUpperInvariant() switch
        {
            "ASC" => new EsriOrderByField(parts[0], false),
            "DESC" => new EsriOrderByField(parts[0], true),
            _ => throw EsriInteropException.Invalid(
                $"'orderByFields' entry '{entry}' has direction '{parts[1]}'; use ASC or DESC."),
        };
    }

    private static int? ParseNonNegativeInt(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0)
        {
            throw EsriInteropException.Invalid($"'{name}' must be a non-negative integer, got '{value}'.");
        }

        return number;
    }

    /// <summary>
    /// The four result-shape selectors are mutually exclusive. Choosing one
    /// explicitly avoids silently dropping a client's request; an ambiguous
    /// combination is a typed <c>invalid.arguments</c> failure.
    /// </summary>
    private static void ValidateResultShape(bool idsOnly, bool countOnly, bool extentOnly, bool distinctValues, bool statistics = false)
    {
        var requested = new List<string>(5);
        if (idsOnly)
        {
            requested.Add("returnIdsOnly");
        }

        if (countOnly)
        {
            requested.Add("returnCountOnly");
        }

        if (extentOnly)
        {
            requested.Add("returnExtentOnly");
        }

        if (distinctValues)
        {
            requested.Add("returnDistinctValues");
        }

        if (statistics)
        {
            requested.Add("outStatistics");
        }

        if (requested.Count > 1)
        {
            throw EsriInteropException.Invalid(
                $"The parameters {string.Join(", ", requested)} are mutually exclusive; request at most one result shape.");
        }
    }

    private static List<EsriOutStatistic>? ParseOutStatistics(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(value);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw EsriInteropException.Invalid($"'outStatistics' must be a JSON array, got an unparsable value: {exception.Message}.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != System.Text.Json.JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
            {
                throw EsriInteropException.Invalid("'outStatistics' must be a non-empty JSON array.");
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var statistics = new List<EsriOutStatistic>(document.RootElement.GetArrayLength());
            foreach (var element in document.RootElement.EnumerateArray())
            {
                statistics.Add(ParseOutStatistic(element, seen));
            }

            return statistics;
        }
    }

    private static EsriOutStatistic ParseOutStatistic(System.Text.Json.JsonElement element, HashSet<string> seen)
    {
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw EsriInteropException.Invalid("'outStatistics' entries must be objects with statisticType, onStatisticField and outStatisticFieldName.");
        }

        var type = element.TryGetProperty("statisticType", out var typeElement) && typeElement.ValueKind == System.Text.Json.JsonValueKind.String
            ? typeElement.GetString()?.Trim().ToLowerInvariant()
            : null;
        var onField = element.TryGetProperty("onStatisticField", out var onElement) && onElement.ValueKind == System.Text.Json.JsonValueKind.String
            ? onElement.GetString()?.Trim()
            : null;
        var outName = element.TryGetProperty("outStatisticFieldName", out var outElement) && outElement.ValueKind == System.Text.Json.JsonValueKind.String
            ? outElement.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(onField) || string.IsNullOrWhiteSpace(outName))
        {
            throw EsriInteropException.Invalid("'outStatistics' entries need a non-empty statisticType, onStatisticField and outStatisticFieldName.");
        }

        if (type is not ("count" or "sum" or "min" or "max" or "avg" or "stddev" or "var"))
        {
            throw EsriInteropException.Invalid($"Statistic type '{type}' is not supported; use count, sum, min, max, avg, stddev or var.");
        }

        if (!seen.Add(outName!))
        {
            throw EsriInteropException.Invalid($"Duplicate outStatisticFieldName '{outName}'.");
        }

        return new EsriOutStatistic(type!, onField!, outName!);
    }

    private static string[]? ParseGroupByFields(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var fields = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0)
        {
            throw EsriInteropException.Invalid("'groupByFieldsForStatistics' must name at least one field.");
        }

        return fields;
    }

    private static EsriFilterClause? ParseHaving(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!EsriFilterClause.TryParse(value, out var clause, out var error))
        {
            throw EsriInteropException.Invalid($"The 'having' clause is not supported: {error}.");
        }

        return clause;
    }

    private static void RejectUnsupported(EsriRequestParameters parameters)
    {
        Reject(parameters, "time", "temporal queries are not supported.");
        Reject(parameters, "returnZ", "Z output is not supported.");
        Reject(parameters, "returnM", "M output is not supported.");
    }

    private static void Reject(EsriRequestParameters parameters, string name, string message)
    {
        if (parameters.Has(name))
        {
            throw EsriInteropException.Invalid($"The '{name}' parameter is not supported: {message}");
        }
    }
}

/// <summary>
/// One <c>outStatistics</c> entry: the aggregation, its input field and
/// the output alias (spec §9.1.4, 10.x statistics).
/// </summary>
internal sealed record EsriOutStatistic(string StatisticType, string OnStatisticField, string OutStatisticFieldName);

/// <summary>
/// One <c>orderByFields</c> entry: the field name (validated against the
/// dataset schema by the service) and its sort direction.
/// </summary>
internal sealed record EsriOrderByField(string Name, bool Descending);
