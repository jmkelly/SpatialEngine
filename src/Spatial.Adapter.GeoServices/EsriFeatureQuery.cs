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
    EsriTimeExtent? Time)
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
        ValidateResultShape(returnIdsOnly, returnCountOnly, returnExtentOnly, returnDistinctValues);
        return new EsriFeatureQuery(
            ParseObjectIds(parameters.Get("objectIds")),
            ParseWhere(parameters.Get("where")),
            ParseGeometry(parameters.Get("geometry"), fallback),
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
            ParseTime(parameters.Get("time")));
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

    /// <summary>
    /// Parses the <c>time</c> parameter: an instant (<c>time=ms</c>) or an
    /// extent (<c>time=start,end</c>) with <c>null</c> infinity bounds. Each
    /// bound is epoch milliseconds (an ISO-8601 date-time is also accepted).
    /// An absent or blank value leaves the extent unset.
    /// </summary>
    private static EsriTimeExtent? ParseTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(',');
        if (parts.Length == 1)
        {
            var instant = ParseTimeBound(parts[0], allowNull: false);
            return new EsriTimeExtent(instant, instant);
        }

        if (parts.Length == 2)
        {
            return new EsriTimeExtent(ParseTimeBound(parts[0], allowNull: true), ParseTimeBound(parts[1], allowNull: true));
        }

        throw EsriInteropException.Invalid($"'time' must be an instant or a start,end extent, got '{value}'.");
    }

    private static long? ParseTimeBound(string? value, bool allowNull)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
        {
            return allowNull
                ? null
                : throw EsriInteropException.Invalid("'time' must be epoch milliseconds or an ISO-8601 date-time.");
        }

        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
        {
            return milliseconds;
        }

        if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed.ToUnixTimeMilliseconds();
        }

        throw EsriInteropException.Invalid($"'time' bound '{text}' is not epoch milliseconds, an ISO-8601 date-time or null.");
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
    private static void ValidateResultShape(bool idsOnly, bool countOnly, bool extentOnly, bool distinctValues)
    {
        var requested = new List<string>(4);
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

        if (requested.Count > 1)
        {
            throw EsriInteropException.Invalid(
                $"The parameters {string.Join(", ", requested)} are mutually exclusive; request at most one result shape.");
        }
    }

    private static void RejectUnsupported(EsriRequestParameters parameters)
    {
        Reject(parameters, "outStatistics", "attribute statistics are not supported.");
        Reject(parameters, "groupByFieldsForStatistics", "statistics grouping is not supported.");
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
/// One <c>orderByFields</c> entry: the field name (validated against the
/// dataset schema by the service) and its sort direction.
/// </summary>
internal sealed record EsriOrderByField(string Name, bool Descending);

/// <summary>
/// The parsed <c>time</c> parameter (spec §9.1.4): an instant has equal
/// bounds; an extent carries <c>null</c> for an open (infinite) bound.
/// Bounds are epoch milliseconds.
/// </summary>
internal sealed record EsriTimeExtent(long? StartMs, long? EndMs);
