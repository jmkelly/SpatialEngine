using System.Globalization;
using System.Text.Json;
using Spatial.Esri.Codec;

namespace Spatial.Adapter.GeoServices;

/// <summary>
/// The parsed Feature Service editing request (spec §9.1.6–§9.1.9). Add and
/// update features keep the raw Esri JSON objects; the service decodes them
/// against the layer schema, so a malformed feature fails alone rather than
/// the whole request. Unsupported additions (<c>gdbVersion</c>,
/// <c>useGlobalIds</c>, <c>returnEditResults</c>) are rejected explicitly,
/// matching the read-only query's contract.
/// </summary>
internal sealed record EsriEditRequest(
    IReadOnlyList<JsonElement> Adds,
    IReadOnlyList<JsonElement> Updates,
    IReadOnlyList<long> Deletes,
    EsriFilterClause? DeleteWhere,
    bool RollbackOnFailure)
{
    private static readonly List<JsonElement> Empty = [];

    /// <summary>Parses <c>addFeatures</c> (<c>features</c>).</summary>
    public static EsriEditRequest ParseAdd(EsriRequestParameters parameters) =>
        new(ParseFeatures(parameters.Require("features")), Empty, [], null, Rollback(parameters));

    /// <summary>Parses <c>updateFeatures</c> (<c>features</c>).</summary>
    public static EsriEditRequest ParseUpdate(EsriRequestParameters parameters) =>
        new(Empty, ParseFeatures(parameters.Require("features")), [], null, Rollback(parameters));

    /// <summary>Parses <c>deleteFeatures</c> (<c>objectIds</c> and/or <c>where</c>).</summary>
    public static EsriEditRequest ParseDelete(EsriRequestParameters parameters)
    {
        var objectIds = ParseDeleteIds(parameters.Get("objectIds"));
        var where = ParseWhere(parameters.Get("where"));
        if (objectIds.Count == 0 && where is null)
        {
            throw GeoServicesErrors.Invalid("'deleteFeatures' requires 'objectIds' or 'where'.");
        }

        return new EsriEditRequest(Empty, Empty, objectIds, where, Rollback(parameters));
    }

    /// <summary>Parses <c>applyEdits</c> (<c>adds</c>, <c>updates</c>, <c>deletes</c>).</summary>
    public static EsriEditRequest ParseApply(EsriRequestParameters parameters) =>
        new(
            ParseOptionalFeatures(parameters.Get("adds")),
            ParseOptionalFeatures(parameters.Get("updates")),
            parameters.Has("deletes") ? ParseDeleteIds(parameters.Get("deletes")) : [],
            null,
            Rollback(parameters));

    /// <summary>Rejects request parameters the editing contract does not support.</summary>
    public static void RejectUnsupported(EsriRequestParameters parameters)
    {
        Reject(parameters, "gdbVersion", "versioned editing is not supported.");
        Reject(parameters, "useGlobalIds", "global-id matching is not supported; use OBJECTID.");
        Reject(parameters, "returnEditResults", "returnEditResults is not supported; per-feature results are always returned.");
    }

    private static bool Rollback(EsriRequestParameters parameters) => parameters.GetBool("rollbackOnFailure", false);

    private static List<JsonElement> ParseOptionalFeatures(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Empty : ParseFeatures(value);

    private static List<JsonElement> ParseFeatures(string value)
    {
        using var document = ParseDocument(value, "The 'features' parameter must be a JSON array of Esri feature objects.");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw GeoServicesErrors.Invalid("The 'features' parameter must be a JSON array of Esri feature objects.");
        }

        var features = new List<JsonElement>(document.RootElement.GetArrayLength());
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw GeoServicesErrors.Invalid("Every feature in 'features' must be a JSON object.");
            }

            features.Add(element.Clone());
        }

        return features;
    }

    private static IReadOnlyList<long> ParseDeleteIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var trimmed = value.Trim();
        if (!trimmed.StartsWith('['))
        {
            return ParseInt64List(trimmed);
        }

        using var document = ParseDocument(trimmed, "The 'deletes' parameter must be a JSON array of object ids.");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw GeoServicesErrors.Invalid("The 'deletes' parameter must be a JSON array of object ids.");
        }

        var ids = new List<long>(document.RootElement.GetArrayLength());
        foreach (var raw in document.RootElement.EnumerateArray())
        {
            var element = raw;
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("objectId", out var objectId))
            {
                element = objectId;
            }

            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt64(out var id))
            {
                throw GeoServicesErrors.Invalid("Every entry in 'deletes' must be an integer object id.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static EsriFilterClause? ParseWhere(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!EsriFilterClause.TryParse(value, out var clause, out var error))
        {
            throw GeoServicesErrors.Invalid($"The 'where' clause is not supported: {error}.");
        }

        return clause;
    }

    private static long[] ParseInt64List(string value)
    {
        var parts = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var ids = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!long.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                throw GeoServicesErrors.Invalid($"'objectIds' must be a comma-separated list of integers, got '{parts[i]}'.");
            }

            ids[i] = id;
        }

        return ids;
    }

    private static JsonDocument ParseDocument(string value, string message)
    {
        try
        {
            return JsonDocument.Parse(value);
        }
        catch (JsonException exception)
        {
            throw GeoServicesErrors.Invalid($"{message} ({exception.Message})", exception);
        }
    }

    private static void Reject(EsriRequestParameters parameters, string name, string message)
    {
        if (parameters.Has(name))
        {
            throw GeoServicesErrors.Invalid($"The '{name}' parameter is not supported: {message}");
        }
    }
}
