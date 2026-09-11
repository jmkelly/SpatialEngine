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
    int? ResultOffset,
    int? ResultRecordCount)
{
    /// <summary>The default spatial relation: the spec's coarse envelope test.</summary>
    public const string EnvelopeIntersects = "esriSpatialRelEnvelopeIntersects";

    /// <summary>The exact intersection relation.</summary>
    public const string Intersects = "esriSpatialRelIntersects";

    /// <summary>Parses the query parameters, rejecting unsupported constructs.</summary>
    public static EsriFeatureQuery Parse(EsriRequestParameters parameters, CoordinateReference? fallback)
    {
        RejectUnsupported(parameters);
        return new EsriFeatureQuery(
            ParseObjectIds(parameters.Get("objectIds")),
            ParseWhere(parameters.Get("where")),
            ParseGeometry(parameters.Get("geometry"), fallback),
            ParseSpatialRel(parameters.Get("spatialRel")),
            ParseOutFields(parameters.Get("outFields")),
            ParseOrderByFields(parameters.Get("orderByFields")),
            parameters.GetBool("returnGeometry", true),
            EsriValueParser.ParseSpatialReference(parameters.Get("outSR")),
            parameters.GetBool("returnIdsOnly", false),
            parameters.GetBool("returnCountOnly", false),
            ParseNonNegativeInt(parameters.Get("resultOffset"), "resultOffset"),
            ParseNonNegativeInt(parameters.Get("resultRecordCount"), "resultRecordCount"));
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

    private static void RejectUnsupported(EsriRequestParameters parameters)
    {
        Reject(parameters, "outStatistics", "attribute statistics are not supported.");
        Reject(parameters, "groupByFieldsForStatistics", "statistics grouping is not supported.");
        Reject(parameters, "returnExtentOnly", "returnExtentOnly is not supported.");
        Reject(parameters, "returnDistinctValues", "returnDistinctValues is not supported.");
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
