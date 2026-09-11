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
    bool ReturnGeometry,
    CoordinateReference? OutSr,
    bool ReturnIdsOnly,
    bool ReturnCountOnly,
    bool ReturnExtentOnly,
    bool ReturnDistinctValues,
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
            parameters.GetBool("returnGeometry", true),
            EsriValueParser.ParseSpatialReference(parameters.Get("outSR")),
            returnIdsOnly,
            returnCountOnly,
            returnExtentOnly,
            returnDistinctValues,
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
